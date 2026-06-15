# Multi-stage build for Service A (FHIR sender). Runs once and exits.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# NuGet restore is the network-heavy step; be patient and retry on transient failures.
ENV NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY services/ServiceA.FhirSender/ServiceA.FhirSender.csproj services/ServiceA.FhirSender/
COPY services/Shared.Opa/Shared.Opa.csproj services/Shared.Opa/
RUN for i in 1 2 3; do \
      dotnet restore services/ServiceA.FhirSender/ServiceA.FhirSender.csproj \
        --runtime linux-x64 \
        /p:RestoreLockedMode=false \
        && break || { echo "restore attempt $i failed, retrying..."; sleep 5; }; \
    done

COPY services/Shared.Opa/ services/Shared.Opa/
COPY services/ServiceA.FhirSender/ services/ServiceA.FhirSender/
RUN dotnet publish services/ServiceA.FhirSender/ServiceA.FhirSender.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# sample-data is mounted at runtime so you can edit the resource without rebuilding.
ENV SAMPLE_FILE=/data/patient.json
ENV SERVICE_B_URL=http://serviceb:5002

# Pass the file path + service B URL as args.
ENTRYPOINT ["sh", "-c", "dotnet ServiceA.FhirSender.dll \"$SAMPLE_FILE\" \"$SERVICE_B_URL\""]
