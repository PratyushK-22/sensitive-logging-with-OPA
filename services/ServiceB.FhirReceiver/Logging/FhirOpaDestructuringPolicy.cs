using System.Text.Json;
using System.Text.Json.Nodes;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Serilog.Core;
using Serilog.Events;
using Shared.Opa;

namespace ServiceB.FhirReceiver.Logging;

/// <summary>
/// Serilog destructuring policy that intercepts any <see cref="Resource"/> being logged
/// (e.g. <c>_log.LogInformation("Received {@Patient}", patient)</c>), serialises it to
/// canonical FHIR JSON, asks OPA which fields to mask/hash/redact for *logging*, applies
/// the decision, and emits the masked tree to the log pipeline.
/// <para>
/// Call sites do not change. This is the "mask only at the time of logging" pattern.
/// </para>
/// </summary>
public sealed class FhirOpaDestructuringPolicy : IDestructuringPolicy
{
    private readonly OpaClient _opa;
    private readonly string _policyPath;
    private readonly FhirJsonSerializer _serializer = new(new SerializerSettings { Pretty = false });

    public FhirOpaDestructuringPolicy(OpaClient opa, string policyPath = "fhir/log_mask/decision")
    {
        _opa = opa;
        _policyPath = policyPath;
    }

    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        result = null!;

        if (value is not Resource res)
            return false;

        try
        {
            // 1. Canonical FHIR JSON.
            var json = _serializer.SerializeToString(res);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node is null) return false;

            // 2. Build the OPA input. Caller context can be enriched later via a scope/AsyncLocal.
            var input = new JsonObject
            {
                ["resource"] = node.DeepClone()
            };

            // 3. Synchronous wait is acceptable here only because OPA runs as a localhost sidecar.
            //    For chatty paths add a small in-memory cache keyed by (resourceType, structural-hash).
            var decision = _opa
                .EvaluateAsync(_policyPath, input, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            FieldTransformer.Apply(node, decision.Transforms);

            // 4. Convert the masked JsonNode tree into a CLR shape Serilog can render natively
            //    (Dictionary<string,object?> / List<object?> / scalars), then let Serilog
            //    destructure it — the output looks like a real structured object, not an
            //    escaped JSON string.
            var clr = JsonToClr(node);
            result = propertyValueFactory.CreatePropertyValue(clr, destructureObjects: true);
            return true;
        }
        catch
        {
            // If anything goes wrong, fail closed: emit a placeholder rather than the raw resource.
            result = new ScalarValue($"<{value.GetType().Name}: redacted (masking error)>");
            return true;
        }
    }

    private static object? JsonToClr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var dict = new Dictionary<string, object?>(obj.Count);
                foreach (var kvp in obj)
                    dict[kvp.Key] = JsonToClr(kvp.Value);
                return dict;
            case JsonArray arr:
                var list = new List<object?>(arr.Count);
                foreach (var item in arr) list.Add(JsonToClr(item));
                return list;
            case JsonValue val:
                // A JsonValue can wrap either a JsonElement (when parsed from JSON)
                // or a raw CLR primitive (when created via JsonValue.Create, e.g. our
                // replacement values). Handle both without throwing.
                if (val.TryGetValue<JsonElement>(out var element))
                {
                    return element.ValueKind switch
                    {
                        JsonValueKind.String => element.GetString(),
                        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => element.ToString()
                    };
                }
                return val.GetValue<object?>();
            default:
                return node.ToJsonString();
        }
    }
}
