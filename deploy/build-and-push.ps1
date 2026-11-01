# Build and push KasaMonitor image to Docker Hub.
# Usage:  .\deploy\build-and-push.ps1 [-Version 0.2.0] [-NoPush]
#
# Run from repo root. Assumes Docker is running and you've done `docker login` once.

param(
    [string]$Version = "0.2.0",
    [string]$Repo = "duobit/kasamonitor",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"

Write-Host "=== Building $Repo`:$Version (and :latest) ===" -ForegroundColor Cyan
docker build `
    -t "$Repo`:$Version" `
    -t "$Repo`:latest" `
    -f deploy/Dockerfile .

if ($LASTEXITCODE -ne 0) { throw "docker build failed" }

if ($NoPush) {
    Write-Host "Skip push (-NoPush). Image available locally as $Repo`:$Version." -ForegroundColor Yellow
    exit 0
}

Write-Host "=== Pushing to Docker Hub ===" -ForegroundColor Cyan
docker push "$Repo`:$Version"
if ($LASTEXITCODE -ne 0) { throw "docker push :$Version failed (did you 'docker login'?)" }
docker push "$Repo`:latest"
if ($LASTEXITCODE -ne 0) { throw "docker push :latest failed" }

Write-Host "=== Done. Pull on QNAP via:" -ForegroundColor Green
Write-Host "      docker pull $Repo`:$Version"
