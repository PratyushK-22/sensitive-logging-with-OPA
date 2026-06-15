using System.IO;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Serilog;
using ServiceB.FhirReceiver.Logging;
using Shared.Opa;

var builder = WebApplication.CreateBuilder(args);

var opaUrl = builder.Configuration["Opa:Url"] ?? "http://localhost:8181";
var opaPolicy = builder.Configuration["Opa:PolicyPath"] ?? "fhir/log_mask/decision";

// OPA client — a regular typed HttpClient. We need an instance available at the time
// we configure Serilog (before the host is built), so we construct one here and also
// register it in DI for any controller use.
var opaHttp = new HttpClient { BaseAddress = new Uri(opaUrl), Timeout = TimeSpan.FromSeconds(5) };
var opaClient = new OpaClient(opaHttp);

builder.Services.AddSingleton(opaClient);

// Serilog: register the destructuring policy so any Resource logged with the @ operator
// (e.g. {@Patient}) is masked by OPA before it reaches a sink.
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .Destructure.With(new FhirOpaDestructuringPolicy(opaClient, opaPolicy))
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Properties:j}{NewLine}{Exception}"));

var app = builder.Build();

// POST /fhir/{resourceType} — accept any FHIR resource, parse with Hl7.Fhir, log it.
// The log call uses {@Resource} so the destructuring policy fires; OPA decides what
// fields to mask/hash/redact in the emitted log line.
app.MapPost("/fhir/{resourceType}", async (string resourceType, HttpRequest request, ILogger<Program> log) =>
{
    using var reader = new StreamReader(request.Body);
    var raw = await reader.ReadToEndAsync();

    var parser = new FhirJsonParser(new ParserSettings
    {
        AcceptUnknownMembers = true,
        AllowUnrecognizedEnums = true
    });

    Resource resource;
    try
    {
        resource = parser.Parse<Resource>(raw);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "Failed to parse inbound FHIR JSON for {ResourceType}", resourceType);
        return Results.Problem(title: "Invalid FHIR JSON", detail: ex.Message, statusCode: 400);
    }

    if (!string.Equals(resource.TypeName, resourceType, StringComparison.OrdinalIgnoreCase))
        return Results.Problem(
            title: "Resource type mismatch",
            detail: $"URL says {resourceType} but body is {resource.TypeName}",
            statusCode: 400);

    // *** The single, unchanged log call. ***
    // The Serilog destructuring policy will run OPA against this object and emit
    // a masked structured payload to the console sink.
    log.LogInformation("Received FHIR resource {@Resource}", resource);

    return Results.Ok(new { received = resource.TypeName, id = resource.Id });
});

app.MapGet("/", () => Results.Text(
    "Service B — FHIR receiver with Serilog OPA-driven log masking.\n" +
    "POST FHIR JSON to /fhir/{resourceType} and watch the console: PHI/PII appears masked.\n"));

app.Run();
