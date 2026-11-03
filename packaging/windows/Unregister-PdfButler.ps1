[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows registration is only available on Windows.' }
if (-not $PSCmdlet.ShouldProcess('Current-user PdfButler shell registration', 'Remove (keeps application files and other PDF applications)')) { return }
$root = [Microsoft.Win32.Registry]::CurrentUser
foreach ($path in @('Software\Classes\PdfButler.Pdf', 'Software\Classes\Applications\PdfButler.exe', 'Software\Classes\SystemFileAssociations\.pdf\shell\PdfButler', 'Software\PdfButler\Capabilities')) {
    $root.DeleteSubKeyTree($path, $false)
}
foreach ($entry in @(@('Software\Classes\.pdf\OpenWithProgids', 'PdfButler.Pdf'), @('Software\RegisteredApplications', 'PdfButler'))) {
    $key = $root.OpenSubKey($entry[0], $true)
    if ($key) { try { $key.DeleteValue($entry[1], $false) } finally { $key.Dispose() } }
}
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'PDF Butler.lnk'
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
if (-not ('PdfButler.ShellNotify' -as [type])) {
    Add-Type -TypeDefinition 'namespace PdfButler { public static class ShellNotify { [System.Runtime.InteropServices.DllImport("shell32.dll")] public static extern void SHChangeNotify(uint e, uint f, System.IntPtr a, System.IntPtr b); } }'
}
[PdfButler.ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
Write-Host 'Registration removed. Application files were kept; close PdfButler and delete its install folder to finish uninstalling.'
