# Run from an extracted publish folder. No elevation, services, scheduled tasks or execution-policy changes.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\PdfButler'),
    [switch]$NoContextMenu,
    [switch]$OpenDefaultApps,
    # Replace an existing PDF Butler installation without asking.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This installer is for Windows.' }
if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'PdfButler.exe'))) { throw 'Run this script from the published application folder.' }
$destination = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$source = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($source -eq $destination -or $source.StartsWith("$destination\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The install folder contains this publish folder. Extract the ZIP somewhere else, or use Register-PdfButler.ps1 to register in place.'
}
$replace = $false
if (Test-Path -LiteralPath $destination) {
    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) {
        # Only ever replace a folder that holds a previous PDF Butler install, never an arbitrary folder.
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'PdfButler.exe'))) {
            throw "$destination is not empty and does not contain a PDF Butler installation. Choose a new or empty install folder."
        }
        $running = Get-Process -Name PdfButler -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith("$destination\", [StringComparison]::OrdinalIgnoreCase) }
        if ($running) { throw 'PDF Butler is running from the install folder. Close it, then run the installer again.' }
        $replace = $true
    }
}
$action = if ($replace) { 'Replace the existing PDF Butler installation, add a Start menu shortcut and register for this user' }
          else { 'Copy PDF Butler, add a Start menu shortcut and register for this user' }
if (-not $PSCmdlet.ShouldProcess($destination, $action)) { return }
if ($replace) {
    if (-not ($Force -or $PSCmdlet.ShouldContinue("$destination already contains a PDF Butler installation. Delete it and install this version?", 'Replace existing installation'))) {
        Write-Host 'Install cancelled. Nothing was changed.'
        return
    }
    Remove-Item -LiteralPath $destination -Recurse -Force
}
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
