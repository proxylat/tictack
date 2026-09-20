#!/usr/bin/env pwsh
<#
TicTack diagnostics / test / perf workflow — Windows.

    tools/diag.ps1 debug [-Full] [-Dump] [PID|NAME]   live triage
    tools/diag.ps1 test  [-Filter EXPR]               run the xUnit suite
    tools/diag.ps1 perf  [-Root DIR]                  fixtured copy scenarios
    tools/diag.ps1 static                             semgrep + ast-grep

Every run writes diag-out\<timestamp>-<mode>\ with a SUMMARY.md recording the
exact commands used and the file each one produced.

Cheap first, freezing last. Nothing here pauses the target process unless you
pass -Dump and confirm. PerfView recipes live in docs\windows-perf.md.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Mode = 'help',
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
# Perf regression threshold, shared contract with diag.sh and documented in
# docs/linux-debug-perf.md (perf section): a scenario slower than its
# benchmarks/baseline.json entry by more than this percent is reported.
$RegressionPct = 20
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $repoRoot '.dotnet\dotnet.exe')) { Join-Path $repoRoot '.dotnet\dotnet.exe' } else { 'dotnet' }
$toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
if (Test-Path $toolsDir) { $env:PATH = "$toolsDir;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$out = Join-Path $repoRoot ("diag-out\{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Mode)
$summary = Join-Path $out 'SUMMARY.md'
$fence = '```'

function Note([string]$Text) {
    Add-Content -LiteralPath $summary -Value $Text
    Write-Host "  $Text"
}

# Log the command into SUMMARY.md and the step's own file, then append output.
# Native exit codes count (global:LASTEXITCODE, reset per step so a cmdlet-only
# action cannot inherit a previous failure): a failed tool prints
# 'fail (exit N)', mirroring record() in tools/diag.sh.
function Step {
    param([string]$File, [string]$Label, [string]$Show, [scriptblock]$Action)
    Add-Content -LiteralPath $summary -Value ("`n### " + $Label + "`n`n" + $fence + "`nPS> " + $Show + "`n" + $fence)
    $body = "### " + $Label + "`n`nPS> " + $Show + "`n`n"
    $rc = 0
    try {
        $global:LASTEXITCODE = 0
        $body += ((& $Action) 2>&1 | Out-String)
        if ($global:LASTEXITCODE) { $rc = $global:LASTEXITCODE }
    }
    catch { $body += "ERROR: $_"; $rc = 1 }
    Set-Content -LiteralPath $File -Value $body
    if ($rc -eq 0) { Write-Host ("  ok    {0}  -> {1}" -f $Label, (Split-Path $File -Leaf)) }
    else { Write-Host ("  fail  {0} (exit {1}) -> {2}" -f $Label, $rc, (Split-Path $File -Leaf)) }
}

function Confirm-Step([string]$Prompt) {
    if ($env:ASSUME_YES -eq '1') { return $true }
    return (Read-Host "  $Prompt [y/N]") -match '^[yY]'
}

function Get-Target([string]$Target) {
    if ($Target -match '^\d+$') { return Get-Process -Id ([int]$Target) -ErrorAction Stop }
    return Get-Process -Name $Target -ErrorAction Stop | Select-Object -First 1
}

# ---------------------------------------------------------------- debug ------
function Run-Debug([string[]]$Args) {
    $deep = $Args -contains '-Full'
    $dump = $Args -contains '-Dump'
    $target = ($Args | Where-Object { $_ -notmatch '^-' } | Select-Object -First 1)

    New-Item -ItemType Directory -Force -Path $out | Out-Null
    if (-not $target) { $target = 'TicTackSv' }
    $proc = Get-Target $target
    $pid2 = $proc.Id

    ("# TicTack diagnostic summary — {0}`n`n- host: {1}`n- pid: {2}`n- target: {3}`n- mode: debug{4}{5}`n" -f `
        (Get-Date -Format o), $env:COMPUTERNAME, $pid2, $target, $(if ($deep) { ' -Full' } else { '' }), $(if ($dump) { ' -Dump' } else { '' })) |
        Set-Content -LiteralPath $summary
    Write-Host "=== pid $pid2"

    # Stuck-vs-slow: CPU consumed over a fixed window. ~0% = blocked (lock/await
    # or a kernel wait); busy = it is progressing, so profile instead of dumping.
    $c0 = $proc.TotalProcessorTime.TotalSeconds
    Start-Sleep -Seconds 3
    $proc.Refresh()
    $c1 = $proc.TotalProcessorTime.TotalSeconds
    $pct = ($c1 - $c0) / 3 * 100

    $kernelWait = 0
    try {
        $threads = $proc.Threads
        $kernelWait = ($threads | Where-Object { $_.ThreadState -eq 'Wait' -and $_.WaitReason -match 'LpcReceive|PageIn|VirtualMemory|FreePage' }).Count
    } catch { $threads = @() }

    $proc | Select-Object Id, ProcessName, StartTime, TotalProcessorTime, WorkingSet64, PrivateMemorySize64, HandleCount, Threads |
        Format-List | Out-String | ForEach-Object { Add-Content -LiteralPath (Join-Path $out 'process.txt') -Value $_ }
    if ($threads.Count) {
        $threads | Select-Object Id, ThreadState, WaitReason, StartTime | Format-Table -AutoSize |
            Out-String | Set-Content -LiteralPath (Join-Path $out 'threads.txt')
    }
    Note ("cpu: {0:N2}% of one core over 3s (threads: {1}, kernel-wait: {2})" -f $pct, $threads.Count, $kernelWait)
    Note ("resources: RSS {0:N1} MB, private {1:N1} MB, {2} handles, {3} threads" -f `
        ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB), $proc.HandleCount, $proc.Threads.Count)

    if ($kernelWait -gt 0) {
        Note "VERDICT: $kernelWait thread(s) in a kernel wait — check the destination"
        Note "         filesystem (USB/network), Defender, and Event Viewer first."
    } elseif ($pct -lt 1) {
        Note "VERDICT: idle CPU — blocked on a lock or an await, not spinning."
        Note "         Escalate to -Full (parallel stacks) next."
    } else {
        Note "VERDICT: busy — it is making progress; get a profile, not a hang dump."
    }

    Step (Join-Path $out 'counters.json') 'dotnet-counters 30s' `
        "dotnet-counters collect -p $pid2 --counters System.Runtime,TicTack --format json --duration 00:00:30 -o counters.json" {
        & dotnet-counters collect -p $pid2 --counters System.Runtime,TicTack --format json --duration 00:00:30 -o (Join-Path $out 'counters.json')
    }
    Note "counters: watch gc-heap-size, gen-2-gc-count, threadpool-queue-length,"
    Note "          monitor-lock-contention-count (compare first vs last sample),"
    Note "          plus the TicTack/* in-app sync counters"

    if ($deep) {
        Step (Join-Path $out 'gcstats.txt') 'dotnet-gcstats (60s)' "dotnet-gcstats $pid2" { & dotnet-gcstats $pid2 }
        Step (Join-Path $out 'pstacks.txt') 'dotnet-pstacks' "dotnet-pstacks -p $pid2" { & dotnet-pstacks -p $pid2 }
        Step (Join-Path $out 'gcdump-baseline.gcdump') 'gcdump baseline' "dotnet-gcdump collect -p $pid2 -o gcdump-baseline.gcdump" {
            & dotnet-gcdump collect -p $pid2 -o (Join-Path $out 'gcdump-baseline.gcdump')
        }
        Step (Join-Path $out 'gcdump-after.gcdump') 'gcdump after 10s' "dotnet-gcdump collect -p $pid2 -o gcdump-after.gcdump" {
            Start-Sleep -Seconds 10; & dotnet-gcdump collect -p $pid2 -o (Join-Path $out 'gcdump-after.gcdump')
        }
        Step (Join-Path $out 'gcdump-report.txt') 'gcdump report + diff' 'dotnet-gcdump report ×2, diffed' {
            & dotnet-gcdump report (Join-Path $out 'gcdump-baseline.gcdump') > (Join-Path $out 'gcdump-report-base.txt')
            & dotnet-gcdump report (Join-Path $out 'gcdump-after.gcdump') > (Join-Path $out 'gcdump-report-after.txt')
            Compare-Object (Get-Content -LiteralPath (Join-Path $out 'gcdump-report-base.txt')) `
                (Get-Content -LiteralPath (Join-Path $out 'gcdump-report-after.txt')) |
                Out-File -LiteralPath (Join-Path $out 'gcdump-report.txt') -Append
        }
        Note 'gcdump: baseline/after reports + diff in gcdump-report*.txt (empty diff = flat heap)'
        Step (Join-Path $out 'dstrings.txt') 'dotnet-dstrings' "dotnet-dstrings -p $pid2" { & dotnet-dstrings -p $pid2 }
        $ds = Join-Path $out 'dstrings.txt'
        if ((Test-Path $ds) -and -not (Get-Item -LiteralPath $ds).Length) {
            Note "empty dstrings? retry with 'dotnet-dstrings -p $pid2 -c 32 -s 10'"
        }

        Step (Join-Path $out 'trace.nettrace') 'dotnet-trace 30s' "dotnet-trace collect -p $pid2 --duration 00:00:30 -o trace.nettrace" {
            & dotnet-trace collect -p $pid2 --duration 00:00:30 -o (Join-Path $out 'trace.nettrace')
        }
        Note "trace: open in PerfView, or 'dotnet-trace convert trace.nettrace --format Speedscope'"
        $nt = Join-Path $out 'trace.nettrace'
        if ((Test-Path -LiteralPath $nt) -and (Get-Item -LiteralPath $nt).Length) {
            # JSON straight to its own file: Step appends action output to its
            # log file, which would corrupt JSON parsing (convert is silent
            # today, but do not rely on that).
            Step (Join-Path $out 'trace.speedscope.log') 'Speedscope flame graph' "dotnet-trace convert trace.nettrace --format Speedscope -o trace" {
                & dotnet-trace convert $nt --format Speedscope -o (Join-Path $out 'trace')
            }
            Note 'trace: flame graph in trace.speedscope.json (open in Speedscope, or PerfView on Windows)'
        } else {
            Note 'trace: no .nettrace collected — skipping Speedscope conversion'
        }
        Step (Join-Path $out 'stack.txt') 'dotnet-stack report' "dotnet-stack report -p $pid2" { & dotnet-stack report -p $pid2 }
        Note "stack: no-dump managed stacks — use when a thread holds a lock in a native call (CloseHandle/ReadFile)"
        Step (Join-Path $out 'fullgc.txt') 'dotnet-fullgc' "dotnet-fullgc $pid2 -csn 0" { & dotnet-fullgc $pid2 -csn 0 }
        Note "fullgc: forced full GC — if the heap does not shrink, the retention is real (see dotnet-memory-analysis skill)"

        Step (Join-Path $out 'eventlog.txt') 'Application event log (TicTackSv)' `
            "Get-WinEvent -LogName Application -ProviderName TicTackSv -MaxEvents 20" {
            Get-WinEvent -LogName Application -ProviderName TicTackSv -MaxEvents 20 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated, LevelDisplayName, Message | Format-List
        }
        Note "long real-time stalls: check Defender (MsMpEng.exe) scanning the destination"
        Note "and .tictack.tmp before blaming the pipeline. Windows GC deep-dive: docs\windows-perf.md"
    }

    # Same-user dump + offline analysis (Option 2). The ClrMD tools that cannot
    # attach here work fine against a dump file, so pstacks/dstrings are pointed
    # at the dump. A .dmp holds process memory: it goes to %TEMP% and is deleted
    # right after the analysis text is written to the artifact dir.
    if ($dump) {
        Note "WARNING: taking a full dump pauses the process until collection finishes."
        $dmp = Join-Path $env:TEMP ("tictack-$pid2-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dmp')
        Note "dump: target $dmp (deleted after analysis)"
        if (Confirm-Step "Freeze pid $pid2 to take a full dump into %TEMP%?") {
            # dotnet-dump first (SOS-grade managed dumps); procdump as fallback
            # when the dotnet tools are not installed on the box.
            if (Get-Command dotnet-dump -ErrorAction SilentlyContinue) {
                Step (Join-Path $out 'dump-collect.txt') 'dotnet-dump collect (FREEZES)' "dotnet-dump collect -p $pid2 -o $dmp" {
                    & dotnet-dump collect -p $pid2 -o $dmp
                }
            } elseif (Get-Command procdump -ErrorAction SilentlyContinue) {
                Step (Join-Path $out 'dump-collect.txt') 'procdump full dump (FREEZES, fallback)' "procdump -ma -accepteula $pid2 $dmp" {
                    & procdump -ma -accepteula $pid2 $dmp
                }
                Note 'dump: procdump fallback — prefer dotnet-dump for SOS-grade dumps when available'
            } else {
                Note 'dump: no collector found — `dotnet tool install -g dotnet-dump`, or procdump (Sysinternals) as fallback; skipping'
            }
            if ((Test-Path $dmp) -and (Get-Item -LiteralPath $dmp).Length) {
                Note ("dump: {0:N0} MB collected" -f ((Get-Item -LiteralPath $dmp).Length / 1MB))
                Step (Join-Path $out 'dump-heap.txt') 'SOS: eeheap + dumpheap' "dotnet-dump analyze $dmp -c 'eeheap -gc' -c 'dumpheap -stat' -c exit" {
                    & dotnet-dump analyze $dmp -c 'eeheap -gc' -c 'dumpheap -stat' -c exit
                }
                # Leak-triage chain: biggest live type -> 2 instances -> gcroot +
                # objsize each, plus finalizequeue. Bounded: 80 lines per call.
                $mt = Select-String -LiteralPath (Join-Path $out 'dump-heap.txt') -Pattern '^([0-9a-fA-F]+)\s+(\d+)\s+(\d+)\s+(\S.*)$' |
                    Where-Object { $_.Matches.Groups[4].Value -ne 'Free' } |
                    Sort-Object { [long]$_.Matches.Groups[3].Value } -Descending |
                    Select-Object -First 1
                if ($mt) {
                    $mtHex = $mt.Matches.Groups[1].Value
                    Step (Join-Path $out 'dump-leak.txt') "SOS leak chain ($mtHex)" "gcroot x2 + objsize + finalizequeue for top MT" {
                        # Match instance rows as `$2 == MT`: a bare `^0000`
                        # address match catches SOS preamble on some runtimes.
                        $addrs = & dotnet-dump analyze $dmp -c "dumpheap -mt $mtHex" -c exit 2>&1 |
                            Where-Object { $_ -match '^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+\d+' -and $Matches[2] -eq $mtHex } |
                            ForEach-Object { $Matches[1] } |
                            Select-Object -First 2
                        foreach ($a in $addrs) { & dotnet-dump analyze $dmp -c "gcroot $a" -c "objsize $a" -c exit 2>&1 | Select-Object -First 80 }
                        & dotnet-dump analyze $dmp -c 'finalizequeue' -c exit 2>&1
                    }
                } else {
                    Note 'dump: no MT parsed from dump-heap.txt — skipping leak chain'
                }
                Step (Join-Path $out 'dump-pstacks.txt') 'dotnet-pstacks <dump>' "dotnet-pstacks $dmp" { & dotnet-pstacks $dmp }
                Step (Join-Path $out 'dump-dstrings.txt') 'dotnet-dstrings <dump>' "dotnet-dstrings $dmp" { & dotnet-dstrings $dmp }
                Remove-Item -LiteralPath $dmp -Force -ErrorAction SilentlyContinue
                if (Test-Path $dmp) {
                    Note "dump: COULD NOT delete $dmp — remove it by hand (holds process memory)"
                } else {
                    Note "dump: deleted $dmp — analysis text kept in the artifact dir"
                }
            } else {
                Note "dump: collect produced no file — skipping offline analysis"
            }
        }
    }
    Write-Host "`n=== artifacts in diag-out\$([IO.Path]::GetFileName($out))\"
}

# ----------------------------------------------------------------- test ------
function Run-Test([string[]]$Args) {
    $filter = ''
    for ($i = 0; $i -lt $Args.Count; $i++) { if ($Args[$i] -eq '-Filter') { $filter = $Args[$i + 1] } }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    ("# TicTack test run — {0}`n`n- filter: {1}`n" -f (Get-Date -Format o), $(if ($filter) { $filter } else { '<none>' })) |
        Set-Content -LiteralPath $summary
    Write-Host '=== xUnit suite'
    $fa = if ($filter) { @('--filter', $filter) } else { @() }
    Step (Join-Path $out 'test.log') 'dotnet test' "dotnet test tests\TicTack.Tests.csproj -c Release $filter" {
        & $dotnet test (Join-Path $repoRoot 'tests\TicTack.Tests.csproj') -c Release @fa --logger 'trx;LogFileName=test-results.trx'
    }
    Select-String -LiteralPath (Join-Path $out 'test.log') -Pattern '^(Passed!|Failed!|error)' | Select-Object -Last 3 | ForEach-Object { Note $_.Line }
    Write-Host "`n=== summary: diag-out\$([IO.Path]::GetFileName($out))\SUMMARY.md"
}

# ----------------------------------------------------------------- perf ------
function Invoke-Scenario([string]$Name, [int]$Workers, [string]$Verification, [bool]$Warm) {
    $cfg = Join-Path $out "bench-$Name.yaml"
    @"
sources:
  - path: '$root\src'
    destination: '$root\dst'
    state_db_path: '$root\state'
    debounce_seconds: 0
    filter:
      max_file_size_mb: no-limit
    sync:
      verification: $Verification
      durability: $dur
      initial_sync_workers: $Workers
logging:
  level: info
  path: '$root\tictack.log'
  max_size_mb: 10
  max_files: 2
  console: true
  alert_path: '$out'
"@ | Set-Content -LiteralPath $cfg

    Write-Host "=== scenario $Name (workers=$Workers, verification=$Verification)"
    Remove-Item -LiteralPath (Join-Path $root 'dst') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $root 'state') -Recurse -Force -ErrorAction SilentlyContinue

    if ($Warm) {
        Step (Join-Path $out "run-$Name-warmup.log") "warmup $Name" "TicTackSv.exe --once --config bench-$Name.yaml" {
            & $exe --once --config $cfg
        }
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    Step (Join-Path $out "run-$Name.log") "timed $Name" "TicTackSv.exe --once --config bench-$Name.yaml" {
        & $exe --once --config $cfg
    }
    $sw.Stop()
    $secs = $sw.Elapsed.TotalSeconds

    if ($Warm) {
        $m = Select-String -LiteralPath (Join-Path $out "run-$Name.log") -Pattern '(\d+) copied' | Select-Object -Last 1
        if ($m) { Note ("$Name copied={0} (expect 0)" -f $m.Matches[0].Groups[1].Value) }
    }

    $dstFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'dst') -Recurse -File -ErrorAction SilentlyContinue)
    $files = $dstFiles.Count
    $total = ($dstFiles | Measure-Object -Property Length -Sum).Sum
    $bytes = if ($total) { [long]$total } else { 0 }
    $mbps = if ($secs -gt 0) { ($bytes / 1MB) / $secs } else { 0 }
    $fps = if ($secs -gt 0) { $files / $secs } else { 0 }
    $load = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average

    $mismatch = Compare-Tree (Join-Path $root 'src') (Join-Path $root 'dst')
    Set-Content -LiteralPath (Join-Path $out "diff-$Name.txt") -Value ($mismatch -join [Environment]::NewLine)
    $diffv = if ($mismatch) { 'mismatch' } else { 'match' }

    Note ("{0}: {1:N3}s, {2} files, {3:N1} MB/s, {4:N1} files/s, cpu {5}%, {6}" -f $Name, $secs, $files, $mbps, $fps, $load, $diffv)
    Add-Content -LiteralPath $summary -Value ("| {0} | {1:N3} | {2} | {3:N1} | {4:N1} | {5} | {6} |" -f $Name, $secs, $files, $mbps, $fps, $load, $diffv)

    # hyperfine: statistically rigorous repeats with prepare-hook cleanup.
    # Additive — the Stopwatch run above stays the baseline input.
    # JSON straight to its own file: Step appends action output to its log
    # file, which would corrupt JSON parsing.
    if (Get-Command hyperfine -ErrorAction SilentlyContinue) {
        $jf = Join-Path $out "run-$Name-hyperfine.json"
        $dstDir = Join-Path $root 'dst'
        $stateDir = Join-Path $root 'state'
        Step (Join-Path $out "run-$Name-hyperfine.log") "hyperfine $Name (1 warmup + 3 runs)" "hyperfine --warmup 1 --runs 3 --prepare <clean dst+state> --shell powershell" {
            & hyperfine --warmup 1 --runs 3 --export-json $jf `
                --prepare "Remove-Item -Recurse -Force '$dstDir','$stateDir'" `
                --shell powershell `
                "$exe --once --config $cfg"
        }
        try {
            $hm = (Get-Content -LiteralPath $jf -Raw | ConvertFrom-Json).results[0].mean
            Note ("hyperfine {0}: mean {1:N3}s over 3 runs" -f $Name, $hm)
        } catch {
            Note "hyperfine $Name: could not parse $jf"
        }
    } else {
        Note 'hyperfine not found — skipping rigorous repeats (winget install hyperfine)'
    }

    return [pscustomobject]@{
        scenario    = $Name
        seconds     = [math]::Round($secs, 3)
        files       = $files
        bytes       = $bytes
        mbps        = [math]::Round($mbps, 1)
        files_per_s = [math]::Round($fps, 1)
        load        = "$load"
        diff        = $diffv
    }
}

function Run-Perf([string[]]$Args) {
    $positional = @($Args | Where-Object { $_ -notmatch '^-' })
    $root = if ($positional.Count -ge 1) { $positional[0] } else { Join-Path $env:USERPROFILE 'sync-bench' }
    $scenarios = if ($positional.Count -ge 2) { $positional[1..($positional.Count - 1)] } else { @('cold-initial', 'warm-noop', 'hash-verify', 'workers=1', 'workers=2', 'workers=4') }
    $dur = if ($env:DURABILITY) { $env:DURABILITY } else { 'full' }

    New-Item -ItemType Directory -Force -Path $out | Out-Null
    ("# TicTack perf run — {0}`n`n- fixture: {1}`n- durability: {2}`n- scenarios: {3}`n" -f (Get-Date -Format o), $root, $dur, ($scenarios -join ', ')) |
        Set-Content -LiteralPath $summary
    Write-Host "=== perf @ $root (durability=$dur)"

    & (Join-Path $repoRoot 'benchmarks\create-fixture.ps1') -Root $root | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }

    Step (Join-Path $out 'build.log') 'build Release' 'dotnet build src\TicTack.csproj -c Release' {
        & $dotnet build (Join-Path $repoRoot 'src\TicTack.csproj') -c Release -v q --nologo
    }
    if (Select-String -LiteralPath (Join-Path $out 'build.log') -Pattern 'Build FAILED|error CS|error MSB' -Quiet) {
        Note 'build error — see build.log, aborting'
        return
    }

    # Match the documented protocol: warm the page cache once, then let each
    # scenario wipe dst/state and time a genuine cold-start initial sync.
    Write-Host '=== warming page cache'
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File | ForEach-Object {
        $fs = [IO.File]::OpenRead($_.FullName); try { $fs.CopyTo([IO.Stream]::Null) } finally { $fs.Dispose() }
    }

    $baselinePath = Join-Path $repoRoot 'benchmarks\baseline.json'
    $baseline = if (Test-Path -LiteralPath $baselinePath) { Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json } else { $null }

    $exe = Join-Path $repoRoot 'src\bin\Release\net10.0-windows\TicTackSv.exe'
    $results = New-Object System.Collections.Generic.List[object]

    Add-Content -LiteralPath $summary -Value ''
    Add-Content -LiteralPath $summary -Value '| scenario | seconds | files | MB/s | files/s | cpu% | diff |'
    Add-Content -LiteralPath $summary -Value '|---|---|---|---|---|---|---|'

    foreach ($scenario in $scenarios) {
        if ($scenario -eq 'cold-initial') { $r = Invoke-Scenario 'cold-initial' 2 'date_and_size' $false }
        elseif ($scenario -eq 'warm-noop') { $r = Invoke-Scenario 'warm-noop' 2 'date_and_size' $true }
        elseif ($scenario -eq 'hash-verify') { $r = Invoke-Scenario 'hash-verify' 2 'hash' $false }
        elseif ($scenario -like 'workers=*') {
            $w = [int]$scenario.Substring(8)
            $r = Invoke-Scenario "workers-$w" $w 'date_and_size' $false
        }
        else { Note "unknown scenario: $scenario"; continue }
        $results.Add($r)

        if ($baseline) {
            $b = $baseline | Where-Object { $_.scenario -eq $r.scenario } | Select-Object -First 1
            if ($b -and $b.seconds -gt 0) {
                $d = ($r.seconds - $b.seconds) / $b.seconds * 100
                if ($d -gt $RegressionPct) { Note ("REGRESSION: {0} is {1:N0}% slower than baseline ({2:N3}s -> {3:N3}s)" -f $r.scenario, $d, $b.seconds, $r.seconds) }
            }
        }
    }

    $results | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'results.json')
    if (-not $baseline) { Note 'no benchmarks\baseline.json — regression comparison skipped' }

    Write-Host "`n=== summary: diag-out\$([IO.Path]::GetFileName($out))\SUMMARY.md"
    Note ("results: diag-out\{0}\results.json" -f [IO.Path]::GetFileName($out))
}

function Compare-Tree([string]$Source, [string]$Destination) {
    $map = @{}
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object { $map[$_.FullName.Substring($Source.Length)] = $_.Length }
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    Get-ChildItem -LiteralPath $Destination -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Destination.Length)
        $seen[$rel] = $true
        if (-not $map.ContainsKey($rel)) { $problems.Add("extra: $rel") }
        elseif ($map[$rel] -ne $_.Length) { $problems.Add(("size: {0} (src {1}, dst {2})" -f $rel, $map[$rel], $_.Length)) }
    }
    foreach ($rel in $map.Keys) { if (-not $seen.ContainsKey($rel)) { $problems.Add("missing: $rel") } }
    return $problems
}

# Local static analysis gate. Mirrored by the `static` CI job
# (.github/workflows/ci.yml), which installs both tools on the runner.
function Run-Static {
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    ("# TicTack static analysis — {0}" -f (Get-Date -Format o)) |
        Set-Content -LiteralPath $summary
    Write-Host '=== static analysis'
    if (Get-Command semgrep -ErrorAction SilentlyContinue) {
        # -o writes pure JSON, so run it directly: Step() would prepend a
        # header into the file and break JSON parsing.
        $jsonFile = Join-Path $out 'semgrep.json'
        Add-Content -LiteralPath $summary -Value ("`n### semgrep r/csharp`n`n" + $fence + "`nPS> semgrep --config r/csharp --metrics off --json -o semgrep.json src`n" + $fence)
        try {
            & semgrep --config r/csharp --metrics off --json -o $jsonFile (Join-Path $repoRoot 'src') > (Join-Path $out 'semgrep.log') 2>&1
            $n = (Get-Content -Raw -LiteralPath $jsonFile | ConvertFrom-Json).results.Count
            Note ("semgrep: {0} findings (r/csharp) -> {1}" -f $n, [IO.Path]::GetFileName($jsonFile))
        } catch { Note "semgrep: ERROR $_" }
    } else {
        Note 'semgrep not found — skipping semgrep r/csharp'
    }
    if (Get-Command ast-grep -ErrorAction SilentlyContinue) {
        Step (Join-Path $out 'ast-grep.txt') 'ast-grep scan (rules/ bank)' 'cd <repo> ; ast-grep scan' {
            Push-Location -LiteralPath $repoRoot
            try { & ast-grep scan } finally { Pop-Location }
        }
    } else {
        Note 'ast-grep not found — skipping banked rules scan (rules/)'
    }
    Write-Host "`n=== summary: diag-out\$([IO.Path]::GetFileName($out))\SUMMARY.md"
}

switch ($Mode.ToLowerInvariant()) {
    'debug' { Run-Debug $Rest }
    'test' { Run-Test $Rest }
    'perf' { Run-Perf $Rest }
    'static' { Run-Static }
    default {
        Write-Host 'TicTack diagnostics / test / perf workflow — Windows'
        Write-Host ''
        Write-Host '  tools/diag.ps1 debug [-Full] [-Dump] [PID|NAME]   live triage'
        Write-Host '  tools/diag.ps1 test  [-Filter EXPR]               run the xUnit suite'
        Write-Host '  tools/diag.ps1 perf  [-Root DIR] [SCENARIOS...]   fixtured copy scenarios'
        Write-Host '  tools/diag.ps1 static                             semgrep + ast-grep'
        Write-Host ''
        Write-Host 'Artifacts land in diag-out\<timestamp>-<mode>\. See docs\windows-debug-perf.md.'
    }
}
