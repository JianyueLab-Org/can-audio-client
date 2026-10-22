param([switch]$Release)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ui/src/ControllerClient/ControllerClient.csproj'
dotnet run --project $project --configuration $(if ($Release) { 'Release' } else { 'Debug' })
exit $LASTEXITCODE
