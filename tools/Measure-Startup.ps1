[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string[]]$Documents,
    [ValidateRange(20,1000)][int]$Repetitions = 20,
    [string]$OutputFile = (Join-Path $PSScriptRoot '../artifacts/startup.json')
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
function Run-Measurement([string]$Document) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--measure')
    $start.ArgumentList.Add($Document)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(65000)) { $process.Kill($true); throw 'Measurement exceeded 65 seconds.' }
        $json = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Measurement failed ($($process.ExitCode)): $errors $json" }
        return ($json | ConvertFrom-Json)
    } finally { $process.Dispose() }
}
function Percentile($Values, [double]$Fraction) {
    $sorted = @($Values | Sort-Object)
    return [Math]::Round($sorted[[Math]::Ceiling($Fraction * $sorted.Count) - 1], 1)
}
$samples = @()
$summary = @()
foreach ($document in $Documents) {
    $path = (Resolve-Path -LiteralPath $document).Path
    Run-Measurement $path | Out-Null # one unrecorded warm-up: these are warm filesystem-cache launches
    $runs = @(for ($i = 0; $i -lt $Repetitions; $i++) { Run-Measurement $path })
    $samples += $runs
    $launch = $runs | ForEach-Object { $_.'first-page-presented' }
    $open = $runs | ForEach-Object { $_.'first-page-presented' - $_.'open-start' }
    $row = [ordered]@{
        document = $path
        repetitions = $Repetitions
        launchP50Ms = Percentile $launch 0.50
        launchP95Ms = Percentile $launch 0.95
        openP50Ms = Percentile $open 0.50
        openP95Ms = Percentile $open 0.95
        workingSetP95MB = Percentile ($runs | ForEach-Object workingSetMB) 0.95
    }
    $summary += [pscustomobject]$row
}
$result = [ordered]@{
    dateUtc = [DateTime]::UtcNow.ToString('o')
    executable = $exe
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    filesystemCache = 'Warm (one discarded warm-up per document); each sample is a new process.'
    summary = $summary
    samples = $samples
}
$out = [IO.Path]::GetFullPath($OutputFile)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($out)) | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $out -Encoding utf8
$summary | Format-Table
Write-Host "Saved $out"
