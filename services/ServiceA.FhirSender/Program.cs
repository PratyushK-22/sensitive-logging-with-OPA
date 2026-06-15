using System.Text;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Shared.Opa;

// Service A — FHIR sender.
//
// - Uses the generic Host so we get DI, configuration and ILogger out of the box.
// - Registers a typed HttpClient wired to the OpaMaskingHandler "middleware".
//   Every outbound JSON body is intercepted, sent to OPA, and rewritten before it
//   leaves the process. The PostAsync call site below is unchanged.
//
// Usage:
//   dotnet run --project services/ServiceA.FhirSender -- <resource.json> [serviceB-url]
//   or set SAMPLE_FILE / SERVICE_B_URL env vars.

var resourcePath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("SAMPLE_FILE")
      ?? throw new ArgumentException("Provide the FHIR resource path as the first argument or set SAMPLE_FILE.");

var serviceBUrl = args.Length > 1
    ? args[1]
    : Environment.GetEnvironmentVariable("SERVICE_B_URL") ?? "http://localhost:5002";

var opaUrl = Environment.GetEnvironmentVariable("OPA_URL") ?? "http://localhost:8181";
var opaPolicy = Environment.GetEnvironmentVariable("OPA_POLICY") ?? "fhir/log_mask/decision";

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    using var host = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices(services =>
        {
            // OPA client — its own HttpClient; not wrapped by the masking handler
            // (otherwise we'd recursively mask the OPA request itself).
            services.AddHttpClient<OpaClient>(c =>
            {
                c.BaseAddress = new Uri(opaUrl);
                c.Timeout = TimeSpan.FromSeconds(5);
            });

            // The masking "middleware" for the outbound pipeline.
            services.AddTransient(sp =>
                new OpaMaskingHandler(
                    sp.GetRequiredService<OpaClient>(),
                    sp.GetRequiredService<ILogger<OpaMaskingHandler>>(),
                    opaPolicy));

            // The named HttpClient Service A uses to call Service B. The handler
            // is wired into its delegating pipeline so PostAsync masks automatically.
            services.AddHttpClient("ServiceB", c =>
            {
                c.BaseAddress = new Uri(serviceBUrl);
                c.DefaultRequestHeaders.Accept.Add(new("application/fhir+json"));
            })
            .AddHttpMessageHandler<OpaMaskingHandler>();
        })
        .Build();

    var log = host.Services.GetRequiredService<ILogger<Program>>();
    var clients = host.Services.GetRequiredService<IHttpClientFactory>();

    if (!File.Exists(resourcePath))
    {
        log.LogError("Resource file not found: {Path}", resourcePath);
        return 1;
    }

    log.LogInformation("Reading FHIR resource from {Path}", resourcePath);
    var fileJson = await File.ReadAllTextAsync(resourcePath);

    // Parse with Hl7.Fhir to prove a real model round-trip (not a blind string forward).
    var parser = new FhirJsonParser(new ParserSettings
    {
        AcceptUnknownMembers = true,
        AllowUnrecognizedEnums = true
    });

    Resource resource;
    try
    {
        resource = parser.Parse<Resource>(fileJson);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to parse FHIR JSON");
        return 1;
    }

    log.LogInformation("Parsed {ResourceType} (id={Id})", resource.TypeName, resource.Id);

    // Re-serialise so what goes on the wire is canonical FHIR JSON.
    var serializer = new FhirJsonSerializer(new SerializerSettings { Pretty = false });
    var wireJson = await serializer.SerializeToStringAsync(resource);

    var http = clients.CreateClient("ServiceB");
    var url = $"/fhir/{resource.TypeName}";

    log.LogInformation("POST {BaseUrl}{Path}", serviceBUrl, url);
    using var content = new StringContent(wireJson, Encoding.UTF8, "application/fhir+json");

    HttpResponseMessage response;
    try
    {
        response = await http.PostAsync(url, content);
    }
    catch (HttpRequestException ex)
    {
        log.LogError(ex, "Service B unreachable at {Url}", serviceBUrl);
        return 2;
    }

    var body = await response.Content.ReadAsStringAsync();
    log.LogInformation("Service B responded {Status}: {Body}", (int)response.StatusCode, body);
    return response.IsSuccessStatusCode ? 0 : 3;
}
finally
{
    await Log.CloseAndFlushAsync();
}
