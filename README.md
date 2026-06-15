# sensitive-logging-with-OPA

A .NET 8 demo of **field-level masking & hashing of HL7 FHIR resources**
driven by **Open Policy Agent (OPA)** policies, masking sensitive data at
two chokepoints:

1. **On the wire** — Service A's `HttpClient` runs an `OpaMaskingHandler`
   `DelegatingHandler` that intercepts every JSON request body, asks OPA
   which fields are sensitive, and rewrites the body before it leaves
   the process. Uses the FHIR-schema-safe `fhir.transit_mask` policy.
2. **At log time** — Service B's Serilog `IDestructuringPolicy` runs OPA
   on every `Resource` logged with `{@Resource}` and emits a masked
   structured log line. Uses the stricter `fhir.log_mask` policy.

Developers keep writing `log.LogInformation("...{@Resource}", x)` and
`http.PostAsync(...)`. They cannot forget to mask.

## Architecture

See [docs/DESIGN.md](docs/DESIGN.md) for full diagrams and threat model.

```
patient.json --> Service A --(HttpClient pipeline)--> OpaMaskingHandler --(masked JSON)--> Service B
                      |                                       |                              |
                      v                                        v                              v
                   ILogger                              POST /v1/data/...                LogInformation({@Resource})
                                                              OPA                              |
                                                                                               v
                                                                              FhirOpaDestructuringPolicy
                                                                              -> POST /v1/data/...
                                                                              -> masked Serilog log line
```

## Quick start (Docker)

From the repo root, with **Docker Desktop running**:

```powershell
.\deploy.ps1            # build, start opa + serviceb, run servicea once, show masked logs
.\deploy.ps1 -Action run     # resend the sample patient
.\deploy.ps1 -Action logs    # tail masked log lines from Service B
.\deploy.ps1 -Action down    # stop everything
.\deploy.ps1 -NoCache        # force a clean rebuild
```

Linux/macOS:

```bash
chmod +x deploy.sh
./deploy.sh             # same actions: build|run|logs|down|clean
```

Manual equivalent:

```powershell
docker compose build serviceb
docker compose --profile sender build servicea
docker compose up -d opa serviceb
docker compose --profile sender run --rm servicea
docker compose logs serviceb
```

## What you should see

Service A console:

```
[INF] Shared.Opa.OpaMaskingHandler OPA returned 8 transform(s); 8 applied to outbound Patient
[INF] Program Service B responded 200: {"received":"Patient","id":"p-001"}
```

Service B console (note the masked fields):

```
[INF] Received FHIR resource { resourceType: "Patient", identifier: [{ value: "sha256:..." }],
      name: [{ family: "sha256:...", given: ["***", "***"] }],
      telecom: [{ system: "phone", value: "***" }, { system: "email", value: "sha256:..." }],
      birthDate: "REDACTED", address: [{ city: "Springfield", postalCode: "***" }] }
```

The original `patient.json` is **never** written to a log.

## Layout

```
OPA.sln
docker-compose.yml
deploy.ps1 / deploy.sh
policies/
  fhir_log_mask.rego          # log-time policy (strict; "REDACTED" allowed)
  fhir_transit_mask.rego      # wire-time policy (FHIR-schema-safe)
sample-data/
  patient.json
services/
  Shared.Opa/                 # OpaClient, FieldTransformer, OpaMaskingHandler
  ServiceA.FhirSender/        # Generic Host + ILogger + HttpClient pipeline
  ServiceB.FhirReceiver/      # ASP.NET Core 8 + Serilog destructuring policy
docs/
  DESIGN.md
```

## Prerequisites

- .NET 8 SDK (only needed if you want to build outside Docker)
- Docker Desktop

## Editing the policies

Both `.rego` files are bind-mounted into the OPA container. Edit and OPA
hot-reloads — no rebuild needed:

```powershell
notepad policies\fhir_log_mask.rego
notepad policies\fhir_transit_mask.rego
```

You can also test a policy directly via OPA's Data API:

```powershell
$body = '{ "input": { "resource": ' + (Get-Content -Raw sample-data/patient.json) + ' } }'
Invoke-RestMethod -Method Post `
  -Uri http://localhost:8181/v1/data/fhir/transit_mask/decision `
  -ContentType 'application/json' -Body $body | ConvertTo-Json -Depth 10
```
