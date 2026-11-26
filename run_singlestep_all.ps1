# run_singlestep_all.ps1
# Runs --verify-singlestep on every *.json.bin file in the corpus folder and writes a summary.
# Place in the project root (the folder that contains GenesisRecomp.slnx) and run from there:
#   dotnet build .\GenesisRecomp.slnx -c Release
#   powershell -ExecutionPolicy Bypass -File .\run_singlestep_all.ps1
#
# Skipped cases are reported separately and are never counted as passes.
# A failure in one of the 15 baseline files is a REGRESSION (they had 0 failures before).
# A failure in any other file is NEW COVERAGE (never run before), not a regression.

param(
    [string]$Corpus = "..\m68000\v1",
    [int]$Limit = 2500,
    [string]$Summary = "singlestep_summary.txt",
    [string]$FailureDir = "singlestep_failures"
)

$project = ".\GenesisRecomp\GenesisRecomp.csproj"

# Historical results (original verifier: handled exceptions were skipped). Passed counts here
# are a floor: the current verifier compares handled exceptions, so Passed should be >= these.
$baseline = [ordered]@{
    "NOP"         = 2500
    "MOVE.b"      = 2078
    "MOVE.w"      = 906
    "MOVE.l"      = 904
    "MOVEA.l"     = 1571
    "MOVEA.w"     = 1559
    "MOVEfromSR"  = 1431
    "MOVEfromUSP" = 1293
    "MOVEtoCCR"   = 1395
    "MOVEtoSR"    = 705
    "MOVEtoUSP"   = 1274
    "MOVEM.l"     = 1134
    "MOVEM.w"     = 1172
    "MOVEP.l"     = 2500
    "MOVEP.w"     = 2500
}

if (-not (Test-Path $Corpus)) {
    Write-Host "Corpus folder not found: $Corpus"
    exit 2
}

New-Item -ItemType Directory -Force -Path $FailureDir | Out-Null

$all = Get-ChildItem -Path $Corpus -Filter "*.json.bin" | Sort-Object Name
$byName = @{}
foreach ($f in $all) { $byName[$f.Name -replace '\.json\.bin$', ''] = $f }

$ordered = New-Object System.Collections.ArrayList
foreach ($name in $baseline.Keys) {
    if ($byName.ContainsKey($name)) { [void]$ordered.Add(@{ Name = $name; File = $byName[$name]; Group = "baseline" }) }
    else { Write-Host "WARNING: baseline file missing from corpus: $name" }
}
foreach ($f in $all) {
    $name = $f.Name -replace '\.json\.bin$', ''
    if (-not $baseline.Contains($name)) { [void]$ordered.Add(@{ Name = $name; File = $f; Group = "new" }) }
}

function Get-Count([string]$text, [string]$label) {
    $m = [regex]::Match($text, $label + '\s*:\s*([\d,]+)')
    if ($m.Success) { return [int]($m.Groups[1].Value -replace ',', '') }
    return -1
}

$rows = New-Object System.Collections.ArrayList
$index = 0
foreach ($item in $ordered) {
    $index++
    $name = $item.Name
    Write-Host ("[{0}/{1}] {2}" -f $index, $ordered.Count, $name)

    if (Test-Path "singlestep_failures.txt") { Remove-Item "singlestep_failures.txt" -Force }

    $text = (& dotnet run --project $project -c Release --no-build -- --verify-singlestep $item.File.FullName $Limit 2>&1 | Out-String)
    $exit = $LASTEXITCODE

    $cases   = Get-Count $text "Cases"
    $passed  = Get-Count $text "Passed"
    $failed  = Get-Count $text "Failed"
    $skipped = Get-Count $text "Skipped"

    $status = "OK"
    if ($cases -lt 0 -or $passed -lt 0 -or $failed -lt 0 -or $skipped -lt 0) { $status = "ERROR (no result; exit $exit)" }
    elseif ($failed -gt 0 -and $item.Group -eq "baseline") { $status = "REGRESSION" }
    elseif ($failed -gt 0) { $status = "FAILURES (new coverage)" }
    elseif ($item.Group -eq "baseline" -and $passed -lt $baseline[$name]) { $status = "PASSED BELOW BASELINE" }
    elseif ($cases -ge 0 -and ($passed + $failed + $skipped) -ne $cases) { $status = "COUNT MISMATCH" }

    if (Test-Path "singlestep_failures.txt") {
        Copy-Item "singlestep_failures.txt" (Join-Path $FailureDir ($name + ".txt")) -Force
        Remove-Item "singlestep_failures.txt" -Force
    }
    if ($status -like "ERROR*") {
        Set-Content -Path (Join-Path $FailureDir ($name + ".error.txt")) -Value $text
    }

    [void]$rows.Add([pscustomobject]@{
        File    = $name
        Group   = $item.Group
        Cases   = $cases
        Passed  = $passed
        Failed  = $failed
        Skipped = $skipped
        Status  = $status
    })
}

function Sum-Rows($rs, [string]$prop) {
    $t = 0
    foreach ($r in $rs) { if ($r.$prop -gt 0) { $t += $r.$prop } }
    return $t
}

$base = @($rows | Where-Object { $_.Group -eq "baseline" })
$new  = @($rows | Where-Object { $_.Group -eq "new" })

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("SingleStep summary  (limit $Limit cases per file; corpus: $Corpus)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Baseline 15 files (previously run):")
[void]$sb.AppendLine(($base | Format-Table File, Cases, Passed, Failed, Skipped, Status -AutoSize | Out-String))
[void]$sb.AppendLine(("Baseline totals: cases {0:N0}  passed {1:N0}  failed {2:N0}  skipped {3:N0}" -f (Sum-Rows $base "Cases"), (Sum-Rows $base "Passed"), (Sum-Rows $base "Failed"), (Sum-Rows $base "Skipped")))
[void]$sb.AppendLine("Reference (old verifier): cases 37,500  passed 22,922  failed 0  skipped 14,578")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Other corpus files (never run before this script):")
[void]$sb.AppendLine(($new | Format-Table File, Cases, Passed, Failed, Skipped, Status -AutoSize | Out-String))
[void]$sb.AppendLine(("Other totals: cases {0:N0}  passed {1:N0}  failed {2:N0}  skipped {3:N0}" -f (Sum-Rows $new "Cases"), (Sum-Rows $new "Passed"), (Sum-Rows $new "Failed"), (Sum-Rows $new "Skipped")))
[void]$sb.AppendLine("")
$regress = @($rows | Where-Object { $_.Status -eq "REGRESSION" -or $_.Status -eq "PASSED BELOW BASELINE" })
if ($regress.Count -eq 0) { [void]$sb.AppendLine("Baseline regressions: none") }
else { [void]$sb.AppendLine("Baseline regressions: " + (($regress | ForEach-Object { $_.File }) -join ", ")) }
[void]$sb.AppendLine("Failure samples (first 25 per file) are in .\$FailureDir\")

$sb.ToString() | Tee-Object -FilePath $Summary
Write-Host ""
Write-Host "Summary written to $Summary"
