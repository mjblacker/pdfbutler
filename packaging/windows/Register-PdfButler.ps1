# Registers this user's Open with / Default Apps candidate and optional classic PDF context-menu verb.
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'PdfButler.exe'),
    [switch]$NoContextMenu,
    [switch]$OpenDefaultApps
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows registration is only available on Windows.' }
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
if ([IO.Path]::GetFileName($exe) -ne 'PdfButler.exe') { throw 'Expected PdfButler.exe.' }
if (-not $PSCmdlet.ShouldProcess($exe, 'Register PdfButler for the current user (does not change the default PDF app)')) { return }
$command = '"' + $exe + '" "%1"'
$icon = '"' + $exe + '",0'
function Set-Values([string]$Path, [hashtable]$Values) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Path)
    try { foreach ($entry in $Values.GetEnumerator()) { $key.SetValue($entry.Key, [string]$entry.Value, [Microsoft.Win32.RegistryValueKind]::String) } }
    finally { $key.Dispose() }
}
$classes = 'Software\Classes'
Set-Values "$classes\PdfButler.Pdf" @{ '' = 'PDF document'; FriendlyTypeName = 'PDF document (PdfButler)' }
Set-Values "$classes\PdfButler.Pdf\DefaultIcon" @{ '' = $icon }
Set-Values "$classes\PdfButler.Pdf\shell\open\command" @{ '' = $command }
Set-Values "$classes\.pdf\OpenWithProgids" @{ 'PdfButler.Pdf' = '' }
Set-Values "$classes\Applications\PdfButler.exe" @{ FriendlyAppName = 'PDF Butler' }
Set-Values "$classes\Applications\PdfButler.exe\SupportedTypes" @{ '.pdf' = '' }
Set-Values "$classes\Applications\PdfButler.exe\shell\open\command" @{ '' = $command }
Set-Values 'Software\PdfButler\Capabilities' @{ ApplicationName = 'PDF Butler'; ApplicationDescription = 'A fast, read-only PDF reader'; ApplicationIcon = $icon }
Set-Values 'Software\PdfButler\Capabilities\FileAssociations' @{ '.pdf' = 'PdfButler.Pdf' }
Set-Values 'Software\RegisteredApplications' @{ PdfButler = 'Software\PdfButler\Capabilities' }
if (-not $NoContextMenu) {
    Set-Values "$classes\SystemFileAssociations\.pdf\shell\PdfButler" @{ '' = 'Open in PDF Butler'; Icon = $icon; MultiSelectModel = 'Document'; NeverDefault = '' }
    Set-Values "$classes\SystemFileAssociations\.pdf\shell\PdfButler\command" @{ '' = $command }
} else {
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$classes\SystemFileAssociations\.pdf\shell\PdfButler", $false)
}
# Notify Explorer without restarting it. Never write the protected UserChoice key.
if (-not ('PdfButler.ShellNotify' -as [type])) {
    Add-Type -TypeDefinition 'namespace PdfButler { public static class ShellNotify { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[PdfButler.ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Registered for this user. Your existing default PDF application is unchanged.'
if ($OpenDefaultApps) { Start-Process 'ms-settings:defaultapps' }
