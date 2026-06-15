using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Shared.Opa;

/// <summary>
/// Walks RFC-6901 JSON Pointers and applies an op (<c>mask</c>, <c>hash</c>, <c>redact</c>,
/// <c>remove</c>) to the target leaf in a <see cref="JsonNode"/> tree.
/// </summary>
public static class FieldTransformer
{
    private const string MaskValue = "***";
    private const string RedactedValue = "REDACTED";

    public static int Apply(JsonNode root, IEnumerable<FieldTransform> transforms)
    {
        var applied = 0;
        foreach (var t in transforms)
            if (TryApply(root, t)) applied++;
        return applied;
    }

    private static bool TryApply(JsonNode root, FieldTransform t)
    {
        var segments = ParsePointer(t.Path);
        if (segments.Count == 0) return false;

        JsonNode? parent = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            parent = Step(parent, segments[i]);
            if (parent is null) return false;
        }

        var leaf = segments[^1];
        return t.Op.ToLowerInvariant() switch
        {
            "mask"   => Replace(parent, leaf, JsonValue.Create(MaskValue)),
            "hash"   => Replace(parent, leaf, JsonValue.Create(HashOf(GetLeafString(parent, leaf)))),
            "redact" => Replace(parent, leaf, JsonValue.Create(RedactedValue)),
            "remove" => Remove(parent, leaf),
            _ => false
        };
    }

    private static JsonNode? Step(JsonNode? node, string segment) => node switch
    {
        JsonObject obj => obj.TryGetPropertyValue(segment, out var v) ? v : null,
        JsonArray arr => int.TryParse(segment, out var idx) && idx >= 0 && idx < arr.Count ? arr[idx] : null,
        _ => null
    };

    private static bool Replace(JsonNode? parent, string leaf, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj when obj.ContainsKey(leaf):
                obj[leaf] = value;
                return true;
            case JsonArray arr when int.TryParse(leaf, out var idx) && idx >= 0 && idx < arr.Count:
                arr[idx] = value;
                return true;
            default:
                return false;
        }
    }

    private static bool Remove(JsonNode? parent, string leaf)
    {
        switch (parent)
        {
            case JsonObject obj:
                return obj.Remove(leaf);
            case JsonArray arr when int.TryParse(leaf, out var idx) && idx >= 0 && idx < arr.Count:
                arr.RemoveAt(idx);
                return true;
            default:
                return false;
        }
    }

    private static string? GetLeafString(JsonNode? parent, string leaf)
    {
        var node = parent switch
        {
            JsonObject obj => obj.TryGetPropertyValue(leaf, out var v) ? v : null,
            JsonArray arr when int.TryParse(leaf, out var idx) && idx >= 0 && idx < arr.Count => arr[idx],
            _ => null
        };
        return node?.GetValue<object?>()?.ToString();
    }

    private static string HashOf(string? input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static List<string> ParsePointer(string pointer)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(pointer) || pointer == "/") return result;
        if (pointer[0] != '/') return result;
        foreach (var raw in pointer[1..].Split('/'))
            result.Add(raw.Replace("~1", "/").Replace("~0", "~"));
        return result;
    }
}
