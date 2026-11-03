[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')][string]$Runtime = 'win-x64',
    [string]$OutputDirectory,
    [switch]$NoReadyToRun
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo "artifacts/publish/$Runtime" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1)) {
    throw 'Publish destination must be new or empty, to avoid stale DLLs. Choose -OutputDirectory or remove the previous output yourself.'
}
& dotnet publish (Join-Path $repo 'src/PdfButler.App') -p:PublishProfile=$Runtime -o $output "-p:PublishReadyToRun=$(-not $NoReadyToRun)"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
$native = if ($Runtime -eq 'win-x64') { 'pdfium.dll' } else { 'libpdfium.so' }
if (-not (Test-Path -LiteralPath (Join-Path $output $native))) { throw "Missing $native beside the application; do not deploy this folder." }
Write-Host "Ready: $output"
