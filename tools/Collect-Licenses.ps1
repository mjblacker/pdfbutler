# Run after restoring the app. Copies upstream notices verbatim; does not remove earlier versions.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$assets = Get-Content (Join-Path $repo 'src/PdfButler.App/obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $package = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $library.Value.path
        if (Test-Path -LiteralPath $candidate) { $package = $candidate; break }
    }
    if (-not $package) { throw "Missing restored package: $($library.Name)" }
    $files = Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '^(LICENSE|THIRD.PARTY.NOTICES|COPYING)(\..*)?$'
    if ($files) {
        $destination = Join-Path $repo "licenses/packages/$($library.Name)"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $files | Copy-Item -Destination $destination
    }
}
