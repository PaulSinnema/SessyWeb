# Removes the one-off probe tests emptied on 22-11-2026. Add -WhatIf to only list them.
param([switch]$WhatIf)

Write-Host "Start: $(Get-Date -Format 'HH:mm:ss')"
$root = $PSScriptRoot
Write-Host "Root: $root"
if (-not $root) { Write-Warning 'PSScriptRoot is empty: run the script with -File, not by pasting it.'; return }

$files = @(
    'SessyUnitTests\AfternoonChargeProbeTests.cs',
    'SessyUnitTests\BusinessCaseProbeTests.cs',
    'SessyUnitTests\EveningPeakProbeTests.cs',
    'SessyUnitTests\EveningSellProbeTests.cs',
    'SessyUnitTests\NightDrainProbeTests.cs',
    'SessyUnitTests\YesterdayAnalysisTests.cs'
)

Write-Host "Checking $($files.Count) paths..."
$targets = @()
foreach ($file in $files) {
    $path = Join-Path $root $file
    Write-Host "  check $path" -NoNewline
    if (Test-Path -LiteralPath $path) { Write-Host '  -> found'; $targets += $path }
    else { Write-Host '  -> not present' }
}
Write-Host "$($targets.Count) files to remove."

if (-not $targets) { Write-Host 'Nothing to remove.'; return }

$removed = 0
foreach ($target in $targets) {
    if ($WhatIf) { Write-Host "Would remove $target"; continue }
    Write-Host "Removing $target ..." -NoNewline
    try { Remove-Item -LiteralPath $target -Force -ErrorAction Stop; Write-Host ' done'; $removed++ }
    catch { Write-Host ' FAILED'; Write-Warning "  $($_.Exception.Message)" }
}

Write-Host "Finished: $removed of $($targets.Count) removed at $(Get-Date -Format 'HH:mm:ss')."
