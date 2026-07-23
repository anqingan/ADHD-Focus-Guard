[CmdletBinding()]
param(
    [switch]$SelfContained,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Vigil.App\Vigil.App.csproj"
$output = Join-Path $root "artifacts\ADHD-Focus-Guard-win-x64"

if ($SelfContained) {
    dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $output
}
else {
    dotnet publish $project `
        --configuration $Configuration `
        --self-contained false `
        --output $output
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Write-Host "ADHD Focus Guard was published to: $output"
