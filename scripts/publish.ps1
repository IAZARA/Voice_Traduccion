param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\VoiceTraductor.App\VoiceTraductor.App.csproj"
$output = Join-Path $repositoryRoot "artifacts\publish\win-x64"

dotnet test (Join-Path $repositoryRoot "VoiceTraductor.sln") --configuration $Configuration
dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $output

Write-Host "VoiceTraductor publicado en: $output"
