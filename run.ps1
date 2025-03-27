# DevToolbox Runner
# This script makes it easy to run the DevToolbox app

# Determine if development or production build
param (
    [switch]$Release,
    [switch]$NoBuild
)

$projectPath = "DevToolbox.UI"
$configuration = if ($Release) { "Release" } else { "Debug" }

# Display header
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "          DEVTOOLBOX RUNNER          " -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Configuration: $configuration" -ForegroundColor Yellow

# Build if not skipped
if (-not $NoBuild) {
    Write-Host "`nBuilding project..." -ForegroundColor Cyan
    dotnet build $projectPath -c $configuration
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    
    Write-Host "Build successful!" -ForegroundColor Green
}

# Run the application
Write-Host "`nStarting DevToolbox..." -ForegroundColor Cyan
dotnet run --project $projectPath -c $configuration --no-build

# Check if application exited with an error
if ($LASTEXITCODE -ne 0) {
    Write-Host "Application exited with code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`nApplication closed successfully." -ForegroundColor Green 