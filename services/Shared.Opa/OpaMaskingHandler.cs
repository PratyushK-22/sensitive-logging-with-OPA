using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Shared.Opa;

/// <summary>
/// "Middleware" for the outbound HTTP pipeline: a <see cref="DelegatingHandler"/> that
/// intercepts every JSON request body, asks OPA which fields are sensitive, applies
/// the resulting transforms, and replaces the request content before it hits the wire.
/// <para>
/// Register on an <c>HttpClient</c> with
/// <c>services.AddHttpClient(...).AddHttpMessageHandler&lt;OpaMaskingHandler&gt;()</c>.
/// All call sites (<c>http.PostAsync(...)</c>) remain unchanged.
/// </para>
/// </summary>
public sealed class OpaMaskingHandler : DelegatingHandler
{
    private readonly OpaClient _opa;
    private readonly ILogger<OpaMaskingHandler> _log;
    private readonly string _policyPath;

    public OpaMaskingHandler(OpaClient opa, ILogger<OpaMaskingHandler> log, string policyPath = "fhir/log_mask/decision")
    {
        _opa = opa;
        _log = log;
        _policyPath = policyPath;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await TryMaskAsync(request, cancellationToken))
        {
            // request.Content has been replaced in place; continue.
        }
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<bool> TryMaskAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null) return false;

        var mediaType = request.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return false;

        var json = await request.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return false;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return false; }

        if (root is not JsonObject resource) return false;

        var input = new JsonObject { ["resource"] = resource.DeepClone() };
        var decision = await _opa.EvaluateAsync(_policyPath, input, ct);
        var applied = FieldTransformer.Apply(resource, decision.Transforms);

        _log.LogInformation(
            "OPA returned {Count} transform(s); {Applied} applied to outbound {ResourceType}",
            decision.Transforms.Count, applied,
            resource["resourceType"]?.GetValue<string>() ?? "<unknown>");

        var maskedJson = resource.ToJsonString();
        var newContent = new StringContent(maskedJson, Encoding.UTF8, mediaType);

        // Preserve any extra Content-* headers the caller set (e.g. Content-Disposition),
        // but NEVER copy Content-Length / Content-Type — StringContent computes those itself,
        // and a stale Content-Length would cause "content would exceed Content-Length".
        foreach (var header in request.Content.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!newContent.Headers.Contains(header.Key))
                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content.Dispose();
        request.Content = newContent;
        return true;
    }
}
