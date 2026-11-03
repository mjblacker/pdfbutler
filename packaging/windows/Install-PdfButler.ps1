# Run from an extracted publish folder. No elevation, services, scheduled tasks or execution-policy changes.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\PdfButler'),
    [switch]$NoContextMenu,
    [switch]$OpenDefaultApps
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This installer is for Windows.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'PdfButler.exe'))) { throw 'Run this script from the published application folder.' }
$destination = [IO.Path]::GetFullPath($InstallDirectory)
if (Test-Path -LiteralPath $destination) {
    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) { throw 'Choose a new or empty install folder. Existing files are never deleted or overwritten.' }
}
if (-not $PSCmdlet.ShouldProcess($destination, 'Copy PDF Butler, add a Start menu shortcut and register for this user')) { return }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot | Copy-Item -Destination $destination -Recurse
$exe = Join-Path $destination 'PdfButler.exe'
& (Join-Path $destination 'Register-PdfButler.ps1') -ExecutablePath $exe -NoContextMenu:$NoContextMenu -OpenDefaultApps:$OpenDefaultApps
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'PDF Butler.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $destination
$shortcut.IconLocation = "$exe,0"
$shortcut.Save()
Write-Host "Installed to $destination"
