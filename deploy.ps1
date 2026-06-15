<#
.SYNOPSIS
  One-shot deploy + run for the OPA / FHIR sensitive-logging demo.

.DESCRIPTION
  Builds the two .NET service images, starts OPA + Service B, then runs
  Service A as a one-shot sender so you can see:
    - Service A: the OpaMaskingHandler middleware masking the egress body
    - Service B: the Serilog destructuring policy producing a masked log

.PARAMETER Action
  up      Build, start opa+serviceb, run servicea once, tail serviceb logs (default).
  build   Just (re)build the two .NET service images.
  run     Just run servicea once against the already-running stack.
  logs    Tail Service B logs.
  down    Stop and remove all containers.
  clean   Down + remove the locally-built images and the build cache.

.PARAMETER NoCache
  Pass --no-cache to docker build. Use this after pulling new code.

.EXAMPLE
  .\deploy.ps1                # full demo: build, start, run, show logs
  .\deploy.ps1 -Action run    # just resend the patient
  .\deploy.ps1 -Action down   # tear it all down
  .\deploy.ps1 -NoCache       # force a clean rebuild
#>
[CmdletBinding()]
param(
    [ValidateSet('up','build','run','logs','down','clean')]
    [string]$Action = 'up',
    [switch]$NoCache
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Test-Docker {
    try { docker info --format '{{.ServerVersion}}' 1>$null 2>$null }
    catch { throw "Docker Desktop is not running. Start it and try again." }
    if ($LASTEXITCODE -ne 0) { throw "Docker Desktop is not running. Start it and try again." }
}

function Invoke-Build {
    $cacheArg = if ($NoCache) { '--no-cache' } else { '' }
    Write-Host "==> Building Service B image $cacheArg" -ForegroundColor Cyan
    docker compose build $cacheArg serviceb
    if ($LASTEXITCODE -ne 0) { throw "serviceb build failed" }

    Write-Host "==> Building Service A image $cacheArg" -ForegroundColor Cyan
    docker compose --profile sender build $cacheArg servicea
    if ($LASTEXITCODE -ne 0) { throw "servicea build failed" }
}

function Invoke-Up {
    Write-Host "==> Starting OPA + Service B in the background" -ForegroundColor Cyan
    docker compose up -d opa serviceb
    if ($LASTEXITCODE -ne 0) { throw "compose up failed" }
}

function Invoke-Run {
    Write-Host "==> Sending sample Patient via Service A (one-shot)" -ForegroundColor Cyan
    docker compose --profile sender run --rm servicea
}

function Show-Logs {
    Write-Host "==> Last masked log lines from Service B" -ForegroundColor Cyan
    docker compose logs --tail=80 serviceb |
        Select-String -Pattern 'Received FHIR|Resource ='
}

function Invoke-Down {
    Write-Host "==> Stopping containers" -ForegroundColor Cyan
    docker compose --profile sender down
}

function Invoke-Clean {
    Write-Host "==> Removing local images and build cache" -ForegroundColor Cyan
    docker compose --profile sender down --rmi local -v
    docker builder prune -f | Out-Null
}

Test-Docker

switch ($Action) {
    'build' { Invoke-Build }
    'run'   { Invoke-Run; Show-Logs }
    'logs'  { Show-Logs }
    'down'  { Invoke-Down }
    'clean' { Invoke-Clean }
    'up'    {
        Invoke-Build
        Invoke-Up
        # Give Service B a moment to come up before the sender fires.
        Start-Sleep -Seconds 2
        Invoke-Run
        Show-Logs
        Write-Host ""
        Write-Host "Stack is running. Useful next commands:" -ForegroundColor Green
        Write-Host "  .\deploy.ps1 -Action run    # resend the patient"
        Write-Host "  .\deploy.ps1 -Action logs   # tail masked logs"
        Write-Host "  .\deploy.ps1 -Action down   # stop everything"
    }
}
