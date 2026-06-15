# Multi-stage build for Service B (FHIR receiver).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

ENV NUGET_XMLDOC_MODE=skip \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# Copy csproj first for better layer caching.
COPY services/ServiceB.FhirReceiver/ServiceB.FhirReceiver.csproj services/ServiceB.FhirReceiver/
COPY services/Shared.Opa/Shared.Opa.csproj services/Shared.Opa/
RUN for i in 1 2 3; do \
      dotnet restore services/ServiceB.FhirReceiver/ServiceB.FhirReceiver.csproj \
        --runtime linux-x64 \
        /p:RestoreLockedMode=false \
        && break || { echo "restore attempt $i failed, retrying..."; sleep 5; }; \
    done

COPY services/Shared.Opa/ services/Shared.Opa/
COPY services/ServiceB.FhirReceiver/ services/ServiceB.FhirReceiver/
RUN dotnet publish services/ServiceB.FhirReceiver/ServiceB.FhirReceiver.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Listen on all interfaces inside the container.
ENV ASPNETCORE_URLS=http://+:5002
EXPOSE 5002

ENTRYPOINT ["dotnet", "ServiceB.FhirReceiver.dll"]
