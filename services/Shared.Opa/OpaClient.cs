using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shared.Opa;

/// <summary>
/// Decision returned by OPA: a list of field-level transforms keyed by RFC-6901 JSON Pointer.
/// </summary>
public sealed record OpaDecision(IReadOnlyList<FieldTransform> Transforms);

/// <summary>
/// One transform. <c>Op</c> is one of: <c>mask</c>, <c>hash</c>, <c>redact</c>, <c>remove</c>.
/// <c>Path</c> is an RFC-6901 pointer relative to the resource root, e.g. <c>/name/0/family</c>.
/// </summary>
public sealed record FieldTransform(string Path, string Op);

/// <summary>
/// Thin client for OPA's Data API: <c>POST {base}/v1/data/{policyPath}</c> with <c>{ "input": ... }</c>.
/// </summary>
public sealed class OpaClient
{
    private readonly HttpClient _http;
    private readonly ILogger<OpaClient> _log;

    public OpaClient(HttpClient http, ILogger<OpaClient>? log = null)
    {
        _http = http;
        _log = log ?? NullLogger<OpaClient>.Instance;
    }

    public async Task<OpaDecision> EvaluateAsync(string policyPath, JsonObject input, CancellationToken ct = default)
    {
        var url = $"/v1/data/{policyPath.Trim('/')}";
        var payload = new JsonObject { ["input"] = input.DeepClone() };

        try
        {
            using var resp = await _http.PostAsJsonAsync(url, payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("OPA returned {Status} for {Url}", (int)resp.StatusCode, url);
                return new OpaDecision(Array.Empty<FieldTransform>());
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("result", out var result))
                return new OpaDecision(Array.Empty<FieldTransform>());

            var list = new List<FieldTransform>();
            if (result.TryGetProperty("transforms", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in arr.EnumerateArray())
                {
                    var path = t.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var op = t.TryGetProperty("op", out var o) ? o.GetString() : null;
                    if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(op))
                        list.Add(new FieldTransform(path!, op!));
                }
            }
            return new OpaDecision(list);
        }
        catch (Exception ex)
        {
            // Fail-closed: callers treat an empty decision as "do nothing extra"; production code
            // can choose to throw and surface a 5xx instead.
            _log.LogError(ex, "OPA evaluation failed for {Url}", url);
            return new OpaDecision(Array.Empty<FieldTransform>());
        }
    }
}
