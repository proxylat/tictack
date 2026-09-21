#!/usr/bin/env pwsh
<#
TicTack diagnostics — Windows. One phased workflow, everything lives in benchmarks\.

    benchmarks\diag.ps1 [-p1] [-p2] [-p3] [-Root DIR] [-Full] [-Dump] [-Target PID|NAME] [-Published] [SCENARIOS...]
    benchmarks\diag.ps1 [PID|NAME]                positional target also works (p1)
    benchmarks\diag.ps1 test [-Filter EXPR] [-WindowsOnly] [-Coverage] [-Elevated] [-NoBuild]

No -p flag = all three phases. -p1 -p2 = first two only, -p1 alone = triage only.
Phases: p1 live triage (process + dotnet-counters + optional -Full depth),
        p2 static analysis + unit tests, p3 fixtured copy scenarios + microbenchmarks.
-Published (p3): measure the shipped artifact — dotnet publish (ReadyToRun, same
  as service\win\install-service.bat) instead of the JIT build output.

Every run writes diag-out\<timestamp>-perf\ (or -test) with a SUMMARY.md recording
the exact commands used and the file each one produced.

Cheap first, freezing last. Nothing here pauses the target process unless you
pass -Dump and confirm. PerfView recipes live in docs\windows-perf.md.
PID/NAME is automated: omitted means the running TicTackSv process.
#>
[CmdletBinding()]
param(
    [switch]$p1,
    [switch]$p2,
    [switch]$p3,
    [switch]$Published,
    [string]$Root = '',
    [switch]$Full,
    [switch]$Dump,
    [string]$Target = '',
    [switch]$WindowsOnly,
    [switch]$Coverage,
    [switch]$Elevated,
    [switch]$NoBuild,
    [string]$Configuration = 'Release',
    [string]$Filter = '',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
# Perf regression threshold, shared contract with diag.sh and documented in
# docs/linux-debug-perf.md (perf section): a scenario slower than its
# benchmarks/baseline-windows.json entry by more than this percent is reported.
$RegressionPct = 20
$repoRoot = Split-Path -Parent $PSScriptRoot
$benchRoot = $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $repoRoot '.dotnet\dotnet.exe')) { Join-Path $repoRoot '.dotnet\dotnet.exe' } else { 'dotnet' }
$toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
if (Test-Path $toolsDir) { $env:PATH = "$toolsDir;$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$haveDnx = [bool](Get-Command dnx -ErrorAction SilentlyContinue)

# Route bare positionals: 'test' = suite mode, scenario names = p3 filter,
# anything else = p1 target override.
$tokens = @($Rest | Where-Object { $_ -notmatch '^-' })
$testMode = $tokens -contains 'test'
$knownScenarios = @('cold-initial', 'warm-noop', 'hash-verify')
$scenarioFilter = @()
$posTarget = ''
foreach ($t in $tokens) {
    if ($t -eq 'test') { continue }
    elseif ($t -in $knownScenarios -or $t -match '^workers=\d+$') { $scenarioFilter += $t }
    elseif (-not $posTarget) { $posTarget = $t }
}
if (-not $Target) { $Target = $posTarget }
# Snapshot while still at script scope: only a target the user actually typed
# (via -Target or positionally) counts as explicit. Run-P1 must not test
# $Target itself — routing has filled it by the time the catch runs.
$script:explicitTarget = $PSBoundParameters.ContainsKey('Target') -or [bool]$posTarget
if (-not $scenarioFilter.Count) {
    $scenarioFilter = @('cold-initial', 'warm-noop', 'hash-verify', 'workers=1', 'workers=2', 'workers=4')
}

if ($testMode) {
    $runP1 = $false; $runP2 = $false; $runP3 = $false; $runTest = $true
    $tag = 'test'
} else {
    $anyPhase = $p1 -or $p2 -or $p3
    $runP1 = $p1 -or (-not $anyPhase)
    $runP2 = $p2 -or (-not $anyPhase)
    $runP3 = $p3 -or (-not $anyPhase)
    $runTest = $false
    $tag = 'perf'
}
if (-not $Root) { $Root = 'C:\bench' }

$out = Join-Path $repoRoot ("diag-out\{0}-{1}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $tag)
$summary = Join-Path $out 'SUMMARY.md'
$fence = '```'
# Created up front: Resolve-Tool (below) already calls Note() for missing tools.
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Note([string]$Text) {
    Add-Content -LiteralPath $summary -Value $Text
    Write-Host "  $Text"
}

# Log the command into SUMMARY.md and the step's own file, then append output.
# Native exit codes count (global:LASTEXITCODE, reset per step so a cmdlet-only
# action cannot inherit a previous failure): a failed tool prints
# 'fail (exit N)', mirroring record() in diag.sh.
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

function Get-Target([string]$Name) {
    if ($Name -match '^\d+$') { return Get-Process -Id ([int]$Name) -ErrorAction Stop }
    return Get-Process -Name $Name -ErrorAction Stop | Select-Object -First 1
}

# Resolve a dotnet-* tool to a runnable command prefix: global install first,
# dnx one-shot second (ships with the .NET 10 SDK, same as CI), skip-note last.
# Returns '' when unusable so call sites guard with: if ($P['x']) { ... }.
$P = @{}
function Resolve-Tool([string]$Name, [string]$Hint) {
    if (Get-Command $Name -ErrorAction SilentlyContinue) { $P[$Name] = "$Name "; return }
    if ($haveDnx) { $P[$Name] = "dnx $Name "; return }
    $P[$Name] = ''
    Note ("{0} not found ({1}) — skipping" -f $Name, $Hint)
}
$installHint = 'dotnet tool install -g <name>, or run via dnx (ships with the .NET 10 SDK)'
foreach ($toolName in @('dotnet-counters', 'dotnet-gcstats', 'dotnet-pstacks', 'dotnet-gcdump', 'dotnet-dstrings', 'dotnet-trace', 'dotnet-stack', 'dotnet-fullgc', 'dotnet-dump')) {
    Resolve-Tool $toolName ($installHint -replace '<name>', $toolName)
}

# Native per-process stats (no WMI): QueryProcessCycleTime for cycles delta,
# GetProcessIoCounters for I/O totals, PDH for context switches + machine CPU.
# Process Explorer reads the same sources. Loaded once per session.
$NativeStatsCs = @"
using System;
using System.Runtime.InteropServices;
public static class TicTackProcStats
{
    [DllImport("kernel32.dll")]
    public static extern bool QueryProcessCycleTime(IntPtr hProcess, out ulong cycles);
    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [DllImport("kernel32.dll")]
    public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhOpenQuery(string szDataSource, IntPtr dwUserData, out IntPtr phQuery);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);
    [DllImport("pdh.dll")]
    public static extern int PdhCollectQueryData(IntPtr hQuery);
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_RAW
    {
        public uint CStatus;
        public uint TimeLow;
        public uint TimeHigh;
        public long FirstValue;
        public long SecondValue;
        public int MultiCount;
    }
    [DllImport("pdh.dll")]
    public static extern int PdhGetRawCounterValue(IntPtr hCounter, out uint lpdwType, out PDH_RAW pValue);
    [DllImport("pdh.dll")]
    public static extern int PdhCloseQuery(IntPtr hQuery);
}
"@
function Ensure-NativeStats {
    if (([System.Management.Automation.PSTypeName]'TicTackProcStats').Type) { return $true }
    try { Add-Type -TypeDefinition $NativeStatsCs -ErrorAction Stop; return $true }
    catch { Note ("native stats unavailable: {0}" -f $_.Exception.Message); return $false }
}
function Get-Cycles([IntPtr]$Handle) {
    $c = [ulong]0
    if ([TicTackProcStats]::QueryProcessCycleTime($Handle, [ref]$c)) { return $c }
    return $null
}
function Get-IoCounters([IntPtr]$Handle) {
    $io = New-Object TicTackProcStats+IO_COUNTERS
    if ([TicTackProcStats]::GetProcessIoCounters($Handle, [ref]$io)) { return $io }
    return $null
}

# ------------------------------------------------------------------ p1 ------
# Starts a transient TicTack watcher under $out when p1 has no resident
# target (default miss only — an explicit -Target miss stays skip-and-name).
# Returns the Process, or $null when no target could be started. The caller
# owns the process afterwards: stop it at the end of p1 and delete $tRoot.
# Everything lives under $out/p1-target — never a real watched folder.
function Ensure-P1Target {
    $exe = Join-Path $repoRoot 'src\bin\Release\net10.0-windows\TicTackSv.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        Step (Join-Path $out 'p1-build.txt') 'build Release (p1 needs a live target)' 'dotnet build src\TicTack.csproj -c Release' {
            & dotnet build (Join-Path $repoRoot 'src\TicTack.csproj') -c Release --nologo -v q
        }
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        Note 'p1: build produced no TicTackSv.exe — skipping live triage'
        return $null
    }
    $tRoot = Join-Path $out 'p1-target'
    $tSrc = Join-Path $tRoot 'src'
    $tDst = Join-Path $tRoot 'dst'
    New-Item -ItemType Directory -Force -Path $tSrc, $tDst | Out-Null
    $tCfg = Join-Path $tRoot 'config.yaml'
    $q = { param($p) return "'$($p.Replace('\', '/'))'" }
    $cfg = @"
sources:
  - paths:
      - $($q.Invoke($tSrc))
    destination: $($q.Invoke($tDst))
    state_db_path: $($q.Invoke((Join-Path $tRoot 'state')))
    debounce_seconds: 20
    filter:
      max_file_size_mb: no-limit
      exclude: []
    sync:
      verification: date_and_size
      durability: full
      drain_strategy: scan
      dir_sync: per-file
      complete_mode: inline
      initial_sync_workers: 2
      lock_handling: retry
      retry_lock_minutes: 10
      delete_threshold_count: 1000
      delete_threshold_size_gb: 50
      delete_threshold_percent: 50
      delete_hold_days: 7
      versioning:
        max_versions: 10
      deletion:
        mode: archive
monitor:
  type: watcher
logging:
  level: warning
  path: $($q.Invoke((Join-Path $tRoot 'tictack.log')))
watchdog:
  enabled: false
"@
    Set-Content -LiteralPath $tCfg -Value $cfg -Encoding UTF8
    & $exe --validate --config $tCfg > (Join-Path $out 'p1-target-validate.log') 2>&1
    if ($LASTEXITCODE -ne 0) {
        Note 'p1: transient config failed --validate — skipping live triage'
        return $null
    }
    $tp = Start-Process -FilePath $exe -ArgumentList @('--cli', '--config', $tCfg) -PassThru -WindowStyle Hidden
    $alive = $false
    for ($i = 0; $i -lt 50 -and -not $alive; $i++) {
        Start-Sleep -Milliseconds 200
        try { $tp.Refresh(); $alive = -not $tp.HasExited } catch { break }
    }
    if (-not $alive) {
        Note 'p1: transient target exited during startup — skipping live triage'
        return $null
    }
    Note ("p1: no resident target — auto-started transient watcher (pid {0}, cleaned up at end)" -f $tp.Id)
    return $tp
}
# Stops the transient p1 target (if any) and deletes its dir. Safe to call
# twice; tolerates an already-dead process and locked files (notes residue).
function Remove-P1Target {
    if (-not $script:autoP1) { return }
    $id = $script:autoP1.Id
    try { Stop-Process -Id $id -Force -ErrorAction Stop } catch { }
    Start-Sleep -Seconds 1
    $tRoot = Join-Path $out 'p1-target'
    Remove-Item -LiteralPath $tRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tRoot) {
        Note ("p1: transient target (pid {0}) stopped but {1} could not be fully removed (locked files?) — remove it by hand" -f $id, $tRoot)
    } else {
        Note ("p1: transient target (pid {0}) stopped and removed" -f $id)
    }
    $script:autoP1 = $null
}
function Run-P1 {
    # A terminating error anywhere below (or Ctrl+C) must not orphan the
    # transient target: clean up, then rethrow (break) so the failure stays visible.
    trap { Remove-P1Target; break }
    $target = $Target
    if (-not $target) { $target = 'TicTackSv' }
    try { $proc = Get-Target $target }
    catch {
        # $script:explicitTarget (snapshot at script scope) decides the branch:
        # only a target the user actually typed counts as explicit.
        if ($script:explicitTarget) {
            # Explicit -Target miss: say what was passed and skip — never substitute silently.
            $msg = "p1 skipped: no '$target' process (you passed -Target '$Target'; check the PID/name and retry)"
            if ($runP2 -or $runP3) { Note $msg; return }
            Write-Host "  $msg"
            Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
            exit 2
        }
        # Default miss: auto-start a transient watcher so p1 always has a target.
        $script:autoP1 = Ensure-P1Target
        if ($null -eq $script:autoP1) {
            $msg = "p1 skipped: no 'TicTackSv' process and no transient target could be started"
            if ($runP2 -or $runP3) { Note $msg; return }
            Write-Host "  $msg"
            Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
            exit 2
        }
        $proc = $script:autoP1
    }
    $pid2 = $proc.Id

    ("# TicTack diagnostic summary — {0}`n`n- host: {1}`n- pid: {2}`n- target: {3}`n- phase: p1 live triage{4}{5}`n" -f `
        (Get-Date -Format o), $env:COMPUTERNAME, $pid2, $target, $(if ($Full) { ' -Full' } else { '' }), $(if ($Dump) { ' -Dump' } else { '' })) |
        Set-Content -LiteralPath $summary
    Write-Host "=== p1 live triage, pid $pid2"

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
        Format-List | Out-String | ForEach-Object { Add-Content -LiteralPath (Join-Path $out 'p1-process.txt') -Value $_ }
    if ($threads.Count) {
        $threads | Select-Object Id, ThreadState, WaitReason, StartTime | Format-Table -AutoSize |
            Out-String | Set-Content -LiteralPath (Join-Path $out 'p1-threads.txt')
    }
    Note ("cpu: {0:N2}% of one core over 3s (threads: {1}, kernel-wait: {2})" -f $pct, $threads.Count, $kernelWait)
    Note ("resources: RSS {0:N1} MB, private {1:N1} MB, {2} handles, {3} threads" -f `
        ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB), $proc.HandleCount, $proc.Threads.Count)

    if ($kernelWait -gt 0) {
        Note "VERDICT: $kernelWait thread(s) in a kernel wait — check the destination"
        Note "         filesystem (USB/network), Defender, and Event Viewer first."
    } elseif ($pct -lt 1) {
        Note "VERDICT: idle CPU — blocked on a lock or an await, not spinning."
        if ($Full) {
            Note "         -Full is on: read the parallel stacks above for the blocking stack."
        } else {
            Note "         Escalate to -Full (parallel stacks) next."
        }
    } else {
        Note "VERDICT: busy — it is making progress; get a profile, not a hang dump."
    }

    if ($P['dotnet-counters']) {
        $cmd = "{0}collect -p {1} --counters System.Runtime,TicTack --format json --duration 00:00:30 -o {2}" -f $P['dotnet-counters'], $pid2, (Join-Path $out 'p1-counters.json')
        Step (Join-Path $out 'p1-counters.log') 'dotnet-counters 30s' $cmd {
            Invoke-Expression $cmd
        }
        Note "counters: watch gc-heap-size, gen-2-gc-count, threadpool-queue-length,"
        Note "          monitor-lock-contention-count (compare first vs last sample),"
        Note "          plus the TicTack/* in-app sync counters"
        # The deployed binary can predate the in-app EventSource (the provider
        # then simply never shows up) — say which case this is. Match the
        # quoted provider, not "TicTackSv" in TargetProcess.
        $countersJson = Join-Path $out 'p1-counters.json'
        if ((Test-Path -LiteralPath $countersJson) -and
            -not (Select-String -LiteralPath $countersJson -Pattern '"TicTack"' -Quiet)) {
            Note "counters: no TicTack/* series — target binary predates the in-app EventSource (rebuild/redeploy), or the provider is off"
        }
    }

    if ($Full) {
        if ($P['dotnet-gcstats']) {
            $cmd = "{0}{1}" -f $P['dotnet-gcstats'], $pid2
            Step (Join-Path $out 'p1-gcstats.txt') 'dotnet-gcstats (60s)' $cmd {
                # gcstats monitors until interrupted — bound it via a Job:
                # 65s of samples, then stop (mimics Ctrl-C).
                $parts = @($P['dotnet-gcstats'].Trim() -split '\s+') + @("$pid2")
                $job = Start-Job -ScriptBlock { & $args[0] @($args[1..($args.Count - 1)]) } -ArgumentList $parts
                Wait-Job $job -Timeout 65 | Out-Null
                Stop-Job $job | Out-Null
                Receive-Job $job 2>&1
                Remove-Job $job -Force
            }
            # gcstats logs one block per GC event; an idle target can print
            # nothing — say so rather than leaving an unexplained empty file.
            $gcstatsFile = Join-Path $out 'p1-gcstats.txt'
            $gcEvents = if (Test-Path -LiteralPath $gcstatsFile) {
                @(Select-String -LiteralPath $gcstatsFile -Pattern '^_______#' -AllMatches).Count
            } else { 0 }
            if ($gcEvents -gt 0) {
                Note "gcstats: $gcEvents GC event(s) captured — see p1-gcstats.txt"
            } else {
                Note "gcstats: no GC events in the 65s window (it logs per event, not per second — an idle target prints nothing)"
                Note "         GC heap/collection telemetry is covered by counters (gc-heap-size, gen-2-gc-count)"
            }
        }
        if ($P['dotnet-pstacks']) {
            $cmd = "{0}-p {1}" -f $P['dotnet-pstacks'], $pid2
            Step (Join-Path $out 'p1-pstacks.txt') 'dotnet-pstacks' $cmd { Invoke-Expression $cmd }
        }
        if ($P['dotnet-gcdump']) {
            $base = Join-Path $out 'p1-gcdump-baseline.gcdump'
            $after = Join-Path $out 'p1-gcdump-after.gcdump'
            Step (Join-Path $out 'p1-gcdump-baseline.log') 'gcdump baseline' ("{0}collect -p {1} -o {2}" -f $P['dotnet-gcdump'], $pid2, $base) {
                Invoke-Expression ("{0}collect -p {1} -o {2}" -f $P['dotnet-gcdump'], $pid2, $base)
            }
            Step (Join-Path $out 'p1-gcdump-after.log') 'gcdump after 10s' ("{0}collect -p {1} -o {2}" -f $P['dotnet-gcdump'], $pid2, $after) {
                Start-Sleep -Seconds 10
                Invoke-Expression ("{0}collect -p {1} -o {2}" -f $P['dotnet-gcdump'], $pid2, $after)
            }
            # A real .gcdump is a FastSerialization binary: length-prefixed
            # '!FastSerialization.1' at offset 4 (od-proofed on real dumps).
            # A failed collect leaves stderr text in a non-empty file, so
            # gate the report on the plain token (no sigil — it is '!', not '$').
            $gcdumpsValid = $true
            foreach ($f in @($base, $after)) {
                $ok = $false
                if (Test-Path -LiteralPath $f) {
                    $bytes = [System.IO.File]::ReadAllBytes($f)
                    $n = [Math]::Min(64, $bytes.Length)
                    $ok = [System.Text.Encoding]::ASCII.GetString($bytes, 0, $n).Contains('FastSerialization')
                }
                if (-not $ok) { $gcdumpsValid = $false }
            }
            if ($gcdumpsValid) {
                Step (Join-Path $out 'p1-gcdump-report.txt') 'gcdump report + diff' 'dotnet-gcdump report x2, diffed' {
                    Invoke-Expression ("{0}report {1}" -f $P['dotnet-gcdump'], $base) > (Join-Path $out 'p1-gcdump-report-base.txt')
                    Invoke-Expression ("{0}report {1}" -f $P['dotnet-gcdump'], $after) > (Join-Path $out 'p1-gcdump-report-after.txt')
                    Compare-Object (Get-Content -LiteralPath (Join-Path $out 'p1-gcdump-report-base.txt')) `
                        (Get-Content -LiteralPath (Join-Path $out 'p1-gcdump-report-after.txt')) |
                        Out-File -LiteralPath (Join-Path $out 'p1-gcdump-report.txt') -Append
                }
                Note 'gcdump: baseline/after reports + diff in p1-gcdump-report*.txt (empty diff = flat heap)'
            } else {
                Note 'gcdump: no valid .gcdump pair collected — skipping report+diff'
            }
        }
        if ($P['dotnet-dstrings']) {
            $cmd = "{0}-p {1}" -f $P['dotnet-dstrings'], $pid2
            Step (Join-Path $out 'p1-dstrings.txt') 'dotnet-dstrings' $cmd { Invoke-Expression $cmd }
            $ds = Join-Path $out 'p1-dstrings.txt'
            if ((Test-Path $ds) -and -not (Get-Item -LiteralPath $ds).Length) {
                Note ("empty dstrings? retry with '{0}-p {1} -c 32 -s 10'" -f $P['dotnet-dstrings'], $pid2)
            }
        }
        if ($P['dotnet-trace']) {
            $nt = Join-Path $out 'p1-trace.nettrace'
            Step (Join-Path $out 'p1-trace.log') 'dotnet-trace 30s' ("{0}collect -p {1} --duration 00:00:30 -o {2}" -f $P['dotnet-trace'], $pid2, $nt) {
                Invoke-Expression ("{0}collect -p {1} --duration 00:00:30 -o {2}" -f $P['dotnet-trace'], $pid2, $nt)
            }
            Note "trace: open in PerfView, or convert to a Speedscope flame graph (next step)"
            if ((Test-Path -LiteralPath $nt) -and (Get-Item -LiteralPath $nt).Length) {
                # JSON straight to its own file: Step appends action output to its
                # log file, which would corrupt JSON parsing (convert is silent
                # today, but do not rely on that).
                Step (Join-Path $out 'p1-trace.speedscope.log') 'Speedscope flame graph' ("{0}convert {1} --format Speedscope -o {2}" -f $P['dotnet-trace'], $nt, (Join-Path $out 'p1-trace')) {
                    Invoke-Expression ("{0}convert {1} --format Speedscope -o {2}" -f $P['dotnet-trace'], $nt, (Join-Path $out 'p1-trace'))
                }
                Note 'trace: flame graph in p1-trace.speedscope.json (open in Speedscope, or PerfView on Windows)'
            } else {
                Note 'trace: no .nettrace collected — skipping Speedscope conversion'
            }
        }
        if ($P['dotnet-stack']) {
            $cmd = "{0}report -p {1}" -f $P['dotnet-stack'], $pid2
            Step (Join-Path $out 'p1-stack.txt') 'dotnet-stack report' $cmd { Invoke-Expression $cmd }
            Note "stack: no-dump managed stacks — use when a thread holds a lock in a native call (CloseHandle/ReadFile)"
            # symbolicate resolves tokens to line numbers; probe first so an older
            # dotnet-stack without the subcommand degrades to a note, not a fail.
            $probe = ("{0}symbolicate --help" -f $P['dotnet-stack'])
            try {
                Invoke-Expression $probe > $null 2>&1
                if ($global:LASTEXITCODE -eq 0 -and (Get-Item -LiteralPath (Join-Path $out 'p1-stack.txt')).Length) {
                    Step (Join-Path $out 'p1-stack.sym.log') 'dotnet-stack symbolicate' ("{0}symbolicate {1}" -f $P['dotnet-stack'], (Join-Path $out 'p1-stack.txt')) {
                        Invoke-Expression ("{0}symbolicate {1}" -f $P['dotnet-stack'], (Join-Path $out 'p1-stack.txt'))
                    }
                }
            } catch { Note 'stack: symbolicate not supported by the installed dotnet-stack — skipping' }
        }
        if ($P['dotnet-fullgc']) {
            $cmd = "{0}{1} -csn 0" -f $P['dotnet-fullgc'], $pid2
            Step (Join-Path $out 'p1-fullgc.txt') 'dotnet-fullgc' $cmd { Invoke-Expression $cmd }
            Note "fullgc: forced full GC — if the heap does not shrink, the retention is real (see dotnet-memory-analysis skill)"
        }

        # PerfView GC capture (Windows-only): bounded 60s CLI collect, no GUI.
        # Recipes for deeper captures live in docs\windows-perf.md.
        $pv = 'C:\tools\PerfView.exe'
        if (Test-Path -LiteralPath $pv) {
            $pvOut = Join-Path $out 'p1-perfview-gc.etl.zip'
            Step (Join-Path $out 'p1-perfview.log') 'PerfView GCCollectOnly 60s' "$pv /GCCollectOnly /AcceptEULA /nogui /MaxCollectSec:60 /DataFile:$pvOut collect" {
                Push-Location -LiteralPath $out
                try { & $pv /GCCollectOnly /AcceptEULA /nogui /MaxCollectSec:60 "/DataFile:$pvOut" collect }
                finally { Pop-Location }
            }
            Note 'perfview: open p1-perfview-gc.etl.zip -> Memory Group > GCStats (more recipes: docs\windows-perf.md)'
        } else {
            Note 'PerfView not found (C:\tools\PerfView.exe) — single exe from github.com/microsoft/perfview;'
            Note '         the .nettrace above still opens in PerfView by hand'
        }

        $logSource = $false
        try { $logSource = [System.Diagnostics.EventLog]::SourceExists('TicTackSv') } catch { $logSource = $true }
        if ($logSource) {
            Step (Join-Path $out 'p1-eventlog.txt') 'Application event log (TicTackSv)' `
                "Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='TicTackSv'} -MaxEvents 20" {
                Get-WinEvent -FilterHashtable @{LogName = 'Application'; ProviderName = 'TicTackSv'} -MaxEvents 20 -ErrorAction SilentlyContinue |
                    Select-Object TimeCreated, LevelDisplayName, Message | Format-List
            }
        } else {
            Note 'event log: TicTackSv source not registered (service never installed here) — skipping event log read'
        }
        Note "long real-time stalls: check Defender (MsMpEng.exe) scanning the destination"
        Note "and .tictack.tmp before blaming the pipeline. Windows GC deep-dive: docs\windows-perf.md"
    }

    # Same-user dump + offline analysis. The ClrMD tools that cannot
    # attach here work fine against a dump file, so pstacks/dstrings are pointed
    # at the dump. A .dmp holds process memory: it goes to %TEMP% and is deleted
    # right after the analysis text is written to the artifact dir.
    if ($Dump) {
        Note "WARNING: taking a full dump pauses the process until collection finishes."
        $dmp = Join-Path $env:TEMP ("tictack-$pid2-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.dmp')
        Note "dump: target $dmp (deleted after analysis)"
        if (Confirm-Step "Freeze pid $pid2 to take a full dump into %TEMP%?") {
            # dotnet-dump first (SOS-grade managed dumps); procdump as fallback
            # when the dotnet tools are not installed on the box.
            if ($P['dotnet-dump']) {
                Step (Join-Path $out 'p1-dump-collect.txt') 'dotnet-dump collect (FREEZES)' ("{0}collect -p {1} -o {2}" -f $P['dotnet-dump'], $pid2, $dmp) {
                    Invoke-Expression ("{0}collect -p {1} -o {2}" -f $P['dotnet-dump'], $pid2, $dmp)
                }
            } elseif (Get-Command procdump -ErrorAction SilentlyContinue) {
                Step (Join-Path $out 'p1-dump-collect.txt') 'procdump full dump (FREEZES, fallback)' "procdump -ma -accepteula $pid2 $dmp" {
                    & procdump -ma -accepteula $pid2 $dmp
                }
                Note 'dump: procdump fallback — prefer dotnet-dump for SOS-grade dumps when available'
            } else {
                Note 'dump: no collector found — `dotnet tool install -g dotnet-dump`, or procdump (Sysinternals) as fallback; skipping'
            }
            if ((Test-Path $dmp) -and (Get-Item -LiteralPath $dmp).Length) {
                Note ("dump: {0:N0} MB collected" -f ((Get-Item -LiteralPath $dmp).Length / 1MB))
                if ($P['dotnet-dump']) {
                    $an = $P['dotnet-dump'] -replace ' $', ''
                    Step (Join-Path $out 'p1-dump-heap.txt') 'SOS: eeheap + dumpheap' "dotnet-dump analyze $dmp -c 'eeheap -gc' -c 'dumpheap -stat' -c exit" {
                        if ($an -eq 'dotnet-dump') { & dotnet-dump analyze $dmp -c 'eeheap -gc' -c 'dumpheap -stat' -c exit }
                        else { & dnx dotnet-dump analyze $dmp -c 'eeheap -gc' -c 'dumpheap -stat' -c exit }
                    }
                    # Leak-triage chain: biggest live type -> 2 instances -> gcroot +
                    # objsize each, plus finalizequeue. Bounded: 80 lines per call.
                    $mt = Select-String -LiteralPath (Join-Path $out 'p1-dump-heap.txt') -Pattern '^([0-9a-fA-F]+)\s+(\d+)\s+(\d+)\s+(\S.*)$' |
                        Where-Object { $_.Matches.Groups[4].Value -ne 'Free' } |
                        Sort-Object { [long]$_.Matches.Groups[3].Value } -Descending |
                        Select-Object -First 1
                    if ($mt) {
                        $mtHex = $mt.Matches.Groups[1].Value
                        Step (Join-Path $out 'p1-dump-leak.txt') "SOS leak chain ($mtHex)" "gcroot x2 + objsize + finalizequeue for top MT" {
                            # Match instance rows as `$2 == MT`: a bare `^0000`
                            # address match catches SOS preamble on some runtimes.
                            if ($an -eq 'dotnet-dump') {
                                $addrs = & dotnet-dump analyze $dmp -c "dumpheap -mt $mtHex" -c exit 2>&1 |
                                    Where-Object { $_ -match '^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+\d+' -and $Matches[2] -eq $mtHex } |
                                    ForEach-Object { $Matches[1] } |
                                    Select-Object -First 2
                                foreach ($a in $addrs) { & dotnet-dump analyze $dmp -c "gcroot $a" -c "objsize $a" -c exit 2>&1 | Select-Object -First 80 }
                                & dotnet-dump analyze $dmp -c 'finalizequeue' -c exit 2>&1
                            } else {
                                $addrs = & dnx dotnet-dump analyze $dmp -c "dumpheap -mt $mtHex" -c exit 2>&1 |
                                    Where-Object { $_ -match '^([0-9a-fA-F]+)\s+([0-9a-fA-F]+)\s+\d+' -and $Matches[2] -eq $mtHex } |
                                    ForEach-Object { $Matches[1] } |
                                    Select-Object -First 2
                                foreach ($a in $addrs) { & dnx dotnet-dump analyze $dmp -c "gcroot $a" -c "objsize $a" -c exit 2>&1 | Select-Object -First 80 }
                                & dnx dotnet-dump analyze $dmp -c 'finalizequeue' -c exit 2>&1
                            }
                        }
                    } else {
                        Note 'dump: no MT parsed from p1-dump-heap.txt — skipping leak chain'
                    }
                    if ($P['dotnet-pstacks']) {
                        Step (Join-Path $out 'p1-dump-pstacks.txt') 'dotnet-pstacks <dump>' ("{0}{1}" -f $P['dotnet-pstacks'], $dmp) {
                            Invoke-Expression ("{0}{1}" -f $P['dotnet-pstacks'], $dmp)
                        }
                    }
                    if ($P['dotnet-dstrings']) {
                        Step (Join-Path $out 'p1-dump-dstrings.txt') 'dotnet-dstrings <dump>' ("{0}{1}" -f $P['dotnet-dstrings'], $dmp) {
                            Invoke-Expression ("{0}{1}" -f $P['dotnet-dstrings'], $dmp)
                        }
                    }
                } else {
                    Note 'dump: collected via procdump — offline ClrMD analysis needs dotnet-dump; skipping'
                }
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
    Remove-P1Target
}

# ------------------------------------------------- p2: static + tests ------
# Returns $true when a semgrep run should proceed. Auto-installs via
# `py -m pip install semgrep` when missing, and ensures the afunix driver
# is running (semgrep-core opens its scan RPC over an AF_UNIX socketpair,
# which dies with WinError 10050 when afunix.sys is stopped). Every skip
# path writes its own Note; callers need no else branch.
function Ensure-Semgrep {
    if (-not (Get-Command semgrep -ErrorAction SilentlyContinue)) {
        Note 'semgrep not found — installing via py -m pip install semgrep'
        try {
            & py -m pip install semgrep --quiet > (Join-Path $out 'p2-semgrep-install.log') 2>&1
            $env:PATH = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('PATH', 'User')
        } catch { Note "semgrep: pip install failed ($_)" }
        if (-not (Get-Command semgrep -ErrorAction SilentlyContinue)) {
            Note 'semgrep: install failed — skipping semgrep r/csharp'
            return $false
        }
        Note 'semgrep auto-installed (py -m pip install semgrep)'
    }
    $afunixRunning = ((sc.exe query afunix) | Select-String 'STATE') -match 'RUNNING'
    if (-not $afunixRunning) {
        $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($admin) {
            sc.exe config afunix start= demand > $null
            sc.exe start afunix > (Join-Path $out 'p2-afunix-start.log') 2>&1
            $afunixRunning = ((sc.exe query afunix) | Select-String 'STATE') -match 'RUNNING'
            if ($afunixRunning) { Note 'afunix driver was stopped — started it (was blocking semgrep)' }
        }
    }
    if (-not $afunixRunning) {
        Note 'semgrep: afunix driver not running (needs admin to start it) — skipping semgrep r/csharp'
        return $false
    }
    return $true
}
function Run-StaticBody {
    Add-Content -LiteralPath $summary -Value "`n## p2 static analysis`n"
    Write-Host '=== p2 static analysis'
    if (Ensure-Semgrep) {
        # -o writes pure JSON, so run it directly: Step() would prepend a
        # header into the file and break JSON parsing.
        $jsonFile = Join-Path $out 'p2-semgrep.json'
        Add-Content -LiteralPath $summary -Value ("`n### semgrep r/csharp`n`n" + $fence + "`nPS> semgrep --config r/csharp --metrics off --json -o p2-semgrep.json src`n" + $fence)
        try {
            & semgrep --config r/csharp --metrics off --json -o $jsonFile (Join-Path $repoRoot 'src') > (Join-Path $out 'p2-semgrep.log') 2>&1
            $n = (Get-Content -Raw -LiteralPath $jsonFile | ConvertFrom-Json).results.Count
            Note ("semgrep: {0} findings (r/csharp) -> {1}" -f $n, [IO.Path]::GetFileName($jsonFile))
        } catch { Note "semgrep: ERROR $_" }
    }
    if (Get-Command ast-grep -ErrorAction SilentlyContinue) {
        Step (Join-Path $out 'p2-ast-grep.txt') 'ast-grep scan (rules/ bank)' 'cd <repo> ; ast-grep scan' {
            Push-Location -LiteralPath $repoRoot
            try { & ast-grep scan } finally { Pop-Location }
        }
    } else {
        Note 'ast-grep not found (npm i -g @ast-grep/cli) — skipping banked rules scan (rules/)'
    }
}

# Folded in from test-windows.ps1 (deleted): the Windows xUnit runner with its
# SDK-10 check, admin warning, and -WindowsOnly/-Coverage/-Elevated/-NoBuild
# switches. Same code path serves standalone `test` mode and p2.
function Invoke-Tests {
    Add-Content -LiteralPath $summary -Value "`n## p2 unit tests`n"
    Write-Host '=== p2 unit tests (xUnit suite)'
    $testProj = Join-Path $repoRoot 'tests\TicTack.Tests.csproj'
    $runSettings = Join-Path $repoRoot 'tests\coverlet.runsettings'
    if (-not (Test-Path -LiteralPath $testProj)) { Note "test project not found: $testProj"; return }

    $sdks = & $dotnet --list-sdks 2>&1
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        Note ("A .NET 10 SDK is required. Installed: {0}" -f ($sdks -join '; '))
        return
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal] `
        [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($Elevated -and -not $isAdmin) {
        Note "-Elevated was requested but this shell is not elevated; the admin-gated tests will skip themselves."
    }
    if ($Elevated) { $env:TICTACK_ELEVATED_TESTS = '1' }

    $filterArgs = @()
    if ($WindowsOnly) { $filterArgs = @('--filter', 'Category=Windows') }
    elseif ($Filter) { $filterArgs = @('--filter', $Filter) }
    $showFilter = if ($filterArgs) { $filterArgs -join ' ' } else { '<none>' }
    $covArgs = @()
    if ($Coverage) { $covArgs = @('--settings', $runSettings, '--collect', 'XPlat Code Coverage') }
    $nbArgs = @()
    if ($NoBuild) { $nbArgs = @('--no-build') }

    $show = "dotnet test tests\TicTack.Tests.csproj -c $Configuration $showFilter"
    Step (Join-Path $out 'p2-test.log') 'dotnet test' $show {
        & $dotnet test $testProj -c $Configuration @nbArgs @filterArgs @covArgs --blame-hang --blame-hang-timeout 180000 --logger 'trx;LogFileName=test-results.trx'
    }
    Select-String -LiteralPath (Join-Path $out 'p2-test.log') -Pattern '^(Passed!|Failed!|error)' | Select-Object -Last 3 | ForEach-Object { Note $_.Line }
}

function Run-P2 {
    Run-StaticBody
    Invoke-Tests
}

# ------------------------------------------------- fixture (inlined) -------
# Folded in from benchmarks/create-fixture.ps1 (deleted): same tree
# (9000 small + 1000x1MB + 10x100MB), same names, same unsafe-root refusal.
function New-Fixture([string]$RootPath) {
    $full = [IO.Path]::GetFullPath($RootPath)
    $rootOfDrive = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    $tempRoot = [IO.Path]::GetTempPath().TrimEnd('\')
    if ($full.TrimEnd('\') -in @($rootOfDrive, $env:USERPROFILE, $tempRoot)) {
        throw "Refusing unsafe fixture root: $full"
    }
    foreach ($d in 'src', 'dst', 'state', 'archive') {
        $p = Join-Path $full $d
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    Remove-Item -LiteralPath (Join-Path $full 'tictack.log') -Force -ErrorAction SilentlyContinue
    foreach ($d in 'src\small', 'src\medium', 'src\large', 'dst') {
        New-Item -ItemType Directory -Force -Path (Join-Path $full $d) | Out-Null
    }
    $buf = New-Object byte[] (1MB)
    for ($i = 1; $i -le 9000; $i++) {
        [IO.File]::WriteAllText((Join-Path $full ('src\small\{0:D5}.txt' -f $i)), "TicTack benchmark file $i`n")
    }
    for ($i = 1; $i -le 1000; $i++) {
        $fs = [IO.File]::Create((Join-Path $full ('src\medium\{0:D4}.bin' -f $i)))
        try { for ($c = 0; $c -lt 1; $c++) { $fs.Write($buf, 0, $buf.Length) } }
        finally { $fs.Dispose() }
    }
    for ($i = 1; $i -le 10; $i++) {
        $fs = [IO.File]::Create((Join-Path $full ('src\large\{0:D2}.bin' -f $i)))
        try { for ($c = 0; $c -lt 100; $c++) { $fs.Write($buf, 0, $buf.Length) } }
        finally { $fs.Dispose() }
    }
    $src = Join-Path $full 'src'
    $files = (Get-ChildItem -LiteralPath $src -Recurse -File).Count
    $bytes = (Get-ChildItem -LiteralPath $src -Recurse -File | Measure-Object -Property Length -Sum).Sum
    return @("Fixture ready: $src", "Files: $files", "Bytes: $bytes")
}

# Content compare: names + sizes first, SHA-256 on size-matching pairs.
# (The old Compare-Tree checked sizes only — silent corruption passed.)
function Compare-TreeContent([string]$Source, [string]$Destination) {
    $map = @{}
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object { $map[$_.FullName.Substring($Source.Length)] = $_.Length }
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    Get-ChildItem -LiteralPath $Destination -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($Destination.Length)
        $seen[$rel] = $true
        if (-not $map.ContainsKey($rel)) { $problems.Add("extra: $rel") }
        elseif ($map[$rel] -ne $_.Length) { $problems.Add(("size: {0} (src {1}, dst {2})" -f $rel, $map[$rel], $_.Length)) }
        else {
            $hs = (Get-FileHash -LiteralPath (Join-Path $Source $rel.TrimStart('\', '/')) -Algorithm SHA256).Hash
            $hd = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            if ($hs -ne $hd) { $problems.Add("content: $rel") }
        }
    }
    foreach ($rel in $map.Keys) { if (-not $seen.ContainsKey($rel)) { $problems.Add("missing: $rel") } }
    return $problems
}

# ------------------------------------------------- p3: perf ---------------
function Invoke-Scenario([string]$Name, [int]$Workers, [string]$Verification, [string]$Durability, [string]$ExePath, [bool]$NoWipe) {
    $cfg = Join-Path $out "p3-bench-$Name.yaml"
    @"
sources:
  - path: '$Root\src'
    destination: '$Root\dst'
    state_db_path: '$Root\state'
    debounce_seconds: 0
    filter:
      max_file_size_mb: no-limit
    sync:
      verification: $Verification
      durability: $Durability
      initial_sync_workers: $Workers
logging:
  level: info
  path: '$Root\tictack.log'
  max_size_mb: 10
  max_files: 2
  console: true
  alert_path: '$out'
"@ | Set-Content -LiteralPath $cfg

    Write-Host "=== scenario $Name (workers=$Workers, verification=$Verification)"
    if (-not $NoWipe) {
        Remove-Item -LiteralPath (Join-Path $Root 'dst') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $Root 'state') -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($NoWipe) {
        # warm-noop: state must already exist (previous scenario left it, or the
        # warmup below creates it) — never wipe, or every run is cold again.
        Step (Join-Path $out "p3-run-$Name-warmup.log") "warmup $Name" "TicTackSv.exe --once --config p3-bench-$Name.yaml" {
            & $ExePath --once --config $cfg
        }
    } else {
        # Honest first pass: drop the page cache so the timed run is not
        # riding the previous scenario's warmth. Needs admin + Sysinternals
        # RAMMap; otherwise the run stays warm and says so.
        if (Get-Command RAMMap -ErrorAction SilentlyContinue) {
            Step (Join-Path $out "p3-run-$Name-cache.log") "drop page cache ($Name)" "RAMMap -Et (empty standby list)" {
                & RAMMap -Et
            }
            Note ("cache: dropped via RAMMap before {0}" -f $Name)
        } else {
            Note ("cache: RAMMap not found (Sysinternals, needs admin) — {0} ran cache-warm, treat hash-verify comparison as approximate" -f $Name)
        }
    }

    $nativeOk = Ensure-NativeStats
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $stdOutFile = Join-Path $out "p3-run-$Name.stdout.txt"
    $stdErrFile = Join-Path $out "p3-run-$Name.stderr.txt"

    # Sampled launch: only TicTackSv, stdout kept for the 'N copied' parse.
    $c0 = $null; $cpu0 = $null; $io0 = $null
    $q = [IntPtr]::Zero; $cCtx = [IntPtr]::Zero; $cCpu = [IntPtr]::Zero
    $pdhOk = $false; $rawCtx0 = $null; $rawCpu0 = $null; $rawCpuT0 = $null; $rawCpuT1 = $null
    $proc = Start-Process -FilePath $ExePath -ArgumentList @('--once', '--config', $cfg) `
        -RedirectStandardOutput $stdOutFile -RedirectStandardError $stdErrFile -PassThru -NoNewWindow
    try { $cpu0 = $proc.TotalProcessorTime } catch { $cpu0 = $null }
    if ($nativeOk) {
        try { $c0 = Get-Cycles $proc.Handle } catch { $c0 = $null }
        try { $io0 = Get-IoCounters $proc.Handle } catch { $io0 = $null }
        try {
            $procName = $proc.ProcessName
            if ([TicTackProcStats]::PdhOpenQuery($null, [IntPtr]::Zero, [ref]$q) -eq 0) {
                # NOTE: the \Process(<name>) instance is first-match — with two
                # TicTackSv processes alive the counter may attach to the other one.
                $r1 = [TicTackProcStats]::PdhAddEnglishCounter($q, "\Process($procName)\Context Switches/sec", [IntPtr]::Zero, [ref]$cCtx)
                $r2 = [TicTackProcStats]::PdhAddEnglishCounter($q, '\Processor Information(_Total)\% Processor Utility', [IntPtr]::Zero, [ref]$cCpu)
                if ($r1 -eq 0 -and $r2 -eq 0 -and [TicTackProcStats]::PdhCollectQueryData($q) -eq 0) {
                    $t = [uint]0; $v = New-Object TicTackProcStats+PDH_RAW
                    if ([TicTackProcStats]::PdhGetRawCounterValue($cCtx, [ref]$t, [ref]$v) -eq 0) { $rawCtx0 = $v.FirstValue }
                    if ([TicTackProcStats]::PdhGetRawCounterValue($cCpu, [ref]$t, [ref]$v) -eq 0) { $rawCpu0 = $v.FirstValue; $rawCpuT0 = [long]$v.TimeHigh * 4294967296 + $v.TimeLow }
                    $pdhOk = ($null -ne $rawCtx0) -and ($null -ne $rawCpu0)
                }
            }
        } catch { $pdhOk = $false }
    }

    # Peak tracking while it runs (500ms poll — cheap, answers end-vs-peak).
    $peakWs = 0; $peakThreads = 0; $peakHandles = 0
    while (-not $proc.WaitForExit(500)) {
        try {
            $proc.Refresh()
            if ($proc.WorkingSet64 -gt $peakWs) { $peakWs = $proc.WorkingSet64 }
            if ($proc.Threads.Count -gt $peakThreads) { $peakThreads = $proc.Threads.Count }
            if ($proc.HandleCount -gt $peakHandles) { $peakHandles = $proc.HandleCount }
        } catch { }
    }
    $sw.Stop()
    $secs = $sw.Elapsed.TotalSeconds
    try { $proc.Refresh() } catch { }

    $c1 = $null; $cpu1 = $null; $io1 = $null
    $endThreads = 0; $endHandles = 0; $endPriv = 0; $endWs = 0
    try { $cpu1 = $proc.TotalProcessorTime } catch { }
    # Post-mortem thread/handle reads usually fail (process exited) — fall
    # back to the live peaks tracked above, which is what gets reported.
    try { $endThreads = $proc.Threads.Count } catch { $endThreads = $peakThreads }
    try { $endHandles = $proc.HandleCount } catch { $endHandles = $peakHandles }
    # Post-mortem reads on an exited process object yield 0/$null WITHOUT
    # throwing, so the catch fallback above never engages. Force peaks back.
    if (-not $endThreads) { $endThreads = $peakThreads }
    if (-not $endHandles) { $endHandles = $peakHandles }
    try { $endPriv = $proc.PrivateMemorySize64 } catch { }
    try { $endWs = $proc.WorkingSet64 } catch { }
    if ($nativeOk) {
        try { $c1 = Get-Cycles $proc.Handle } catch { }
        try { $io1 = Get-IoCounters $proc.Handle } catch { }
    }
    $ctxDelta = $null; $machDelta = $null
    if ($pdhOk) {
        try {
            if ([TicTackProcStats]::PdhCollectQueryData($q) -eq 0) {
                $t = [uint]0; $v = New-Object TicTackProcStats+PDH_RAW
                if ([TicTackProcStats]::PdhGetRawCounterValue($cCtx, [ref]$t, [ref]$v) -eq 0) { $ctxDelta = $v.FirstValue - $rawCtx0 }
                if ([TicTackProcStats]::PdhGetRawCounterValue($cCpu, [ref]$t, [ref]$v) -eq 0) { $machDelta = $v.FirstValue - $rawCpu0; $rawCpuT1 = [long]$v.TimeHigh * 4294967296 + $v.TimeLow }
            }
        } catch { }
        try { [TicTackProcStats]::PdhCloseQuery($q) | Out-Null } catch { }
    }
    # % Processor Utility is a 100ns-idle timer: busy% = 100 * (1 - dValue/dTime).
    $machPctS = 'n/a'
    if (($null -ne $machDelta) -and ($null -ne $rawCpuT0) -and ($null -ne $rawCpuT1) -and (($rawCpuT1 - $rawCpuT0) -gt 0)) {
        $machPctS = '{0:N1}' -f (100 * (1 - $machDelta / ($rawCpuT1 - $rawCpuT0)))
    }
    $proc.Dispose()

    Step (Join-Path $out "p3-run-$Name.log") "timed $Name" "TicTackSv.exe --once --config p3-bench-$Name.yaml (sampled)" {
        Get-Content -LiteralPath $stdOutFile -ErrorAction SilentlyContinue
    }

    $m = Select-String -LiteralPath (Join-Path $out "p3-run-$Name.log") -Pattern '(\d+) copied' | Select-Object -Last 1
    $copied = if ($m) { [long]$m.Matches[0].Groups[1].Value } else { -1 }
    if ($NoWipe) { Note ("$Name copied={0} (expect 0)" -f $copied) }

    $dstFiles = @(Get-ChildItem -LiteralPath (Join-Path $Root 'dst') -Recurse -File -ErrorAction SilentlyContinue)
    $files = $dstFiles.Count
    $total = ($dstFiles | Measure-Object -Property Length -Sum).Sum
    $bytes = if ($total) { [long]$total } else { 0 }
    # Throughput is undefined when nothing was copied — printing dst-bytes/elapsed
    # here produced the false 2,936 MB/s warm-noop row. Report n/a instead.
    if ($copied -eq 0) { $mbps = 'n/a'; $fps = 'n/a' }
    else {
        $mbps = if ($secs -gt 0) { '{0:N1}' -f (($bytes / 1MB) / $secs) } else { 'n/a' }
        $fps = if ($secs -gt 0) { '{0:N1}' -f ($files / $secs) } else { 'n/a' }
    }

    # Native stats -> derived numbers (all n/a-safe).
    $cores = [Environment]::ProcessorCount
    if (($null -ne $cpu0) -and ($null -ne $cpu1) -and ($secs -gt 0)) {
        $cpuOne = ($cpu1 - $cpu0).TotalSeconds / $secs * 100
        $cpuOneS = '{0:N2}' -f $cpuOne
        $cpuAllS = '{0:N2}' -f ($cpuOne / $cores)
    } else { $cpuOne = $null; $cpuOneS = 'n/a'; $cpuAllS = 'n/a' }
    $cyclesDelta = if (($null -ne $c0) -and ($null -ne $c1)) { $c1 - $c0 } else { $null }
    $cyclesPerFile = if (($null -ne $cyclesDelta) -and ($files -gt 0)) { '{0:N0}' -f ($cyclesDelta / $files) } else { 'n/a' }
    if (($null -ne $io0) -and ($null -ne $io1)) {
        $ioBytes = ($io1.ReadTransferCount - $io0.ReadTransferCount) + ($io1.WriteTransferCount - $io0.WriteTransferCount) + ($io1.OtherTransferCount - $io0.OtherTransferCount)
        $ioRate = if ($secs -gt 0) { '{0:N1}' -f (($ioBytes / 1MB) / $secs) } else { 'n/a' }
        # Amplification = bytes the process moved / payload bytes. >1x means an
        # extra read/write pass (validation re-read, hash double-read, temp+rename).
        $ampl = if (($bytes -gt 0) -and ($copied -gt 0)) { '{0:N2}' -f ($ioBytes / $bytes) } else { 'n/a' }
    } else { $ioBytes = $null; $ioRate = 'n/a'; $ampl = 'n/a' }
    # Numeric amplifications carry the x suffix; n/a must not print as "n/ax".
    $amplS = if ($ampl -eq 'n/a') { 'n/a' } else { $ampl + 'x' }
    $ctxS = if ($null -ne $ctxDelta) { "$ctxDelta" } else { 'n/a' }
    $ctxPerFile = if (($null -ne $ctxDelta) -and ($files -gt 0)) { '{0:N1}' -f ($ctxDelta / $files) } else { 'n/a' }

    $mismatch = Compare-TreeContent (Join-Path $Root 'src') (Join-Path $Root 'dst')
    Set-Content -LiteralPath (Join-Path $out "p3-diff-$Name.txt") -Value ($mismatch -join [Environment]::NewLine)
    $diffv = if ($mismatch) { 'mismatch' } else { 'match' }

    Note ("{0}: {1:N3}s, {2} files, {3} MB/s, {4} files/s, cpu {5}% (1 core) / {6}% (all), {7}" -f $Name, $secs, $files, $mbps, $fps, $cpuOneS, $cpuAllS, $diffv)
    Note ("  cycles/file {0}, io {1} MB/s total, amplification {2}, ctx {3} ({4}/file), machine {5}%, handles {6}, threads {7}, peak RSS {8:N1} MB" -f $cyclesPerFile, $ioRate, $amplS, $ctxS, $ctxPerFile, $machPctS, $endHandles, $endThreads, ($peakWs / 1MB))
    Add-Content -LiteralPath $summary -Value ("| {0} | {1:N3} | {2} | {3} | {4} | {5} / {6} | {7} |" -f $Name, $secs, $files, $mbps, $fps, $cpuOneS, $cpuAllS, $diffv)
    Add-Content -LiteralPath $summary -Value ("| {0} stats | cycles/file {1} | io {2} MB/s | ampl {3} | ctx/file {4} | handles {5} | threads {6} |" -f $Name, $cyclesPerFile, $ioRate, $amplS, $ctxPerFile, $endHandles, $endThreads)

    # hyperfine: statistically rigorous repeats. Additive — its mean is the
    # baseline input when present (the sampled run above is the fallback).
    # JSON straight to its own file: Step appends
    # action output to its log file, which would corrupt JSON parsing.
    # warm-noop runs WITHOUT --prepare: wiping dst+state makes every repeat cold.
    if (Get-Command hyperfine -ErrorAction SilentlyContinue) {
        $jf = Join-Path $out "p3-run-$Name-hyperfine.json"
        $dstDir = Join-Path $Root 'dst'
        $stateDir = Join-Path $Root 'state'
        $prepArgs = @()
        $prepShow = '<none: warm-noop keeps state>'
        if (-not $NoWipe) {
            $prepArgs = @('--prepare', "Remove-Item -Recurse -Force '$dstDir','$stateDir'")
            $prepShow = "Remove-Item -Recurse -Force dst,state"
        }
        Step (Join-Path $out "p3-run-$Name-hyperfine.log") "hyperfine $Name (1 warmup + 3 runs)" "hyperfine --warmup 1 --runs 3 --prepare [$prepShow] --shell powershell" {
            & hyperfine --warmup 1 --runs 3 --export-json $jf @prepArgs `
                --shell powershell `
                "$ExePath --once --config $cfg"
        }
        try {
            $hm = (Get-Content -LiteralPath $jf -Raw | ConvertFrom-Json).results[0].mean
            Note ("hyperfine {0}: mean {1:N3}s over 3 runs" -f $Name, $hm)
        } catch {
            Note ("hyperfine {0}: could not parse {1}" -f $Name, $jf)
        }
    } else {
        Note 'hyperfine not found (winget install hyperfine) — skipping rigorous repeats'
    }

    return [pscustomobject]@{
        scenario     = $Name
        seconds      = [math]::Round($secs, 3)
        files        = $files
        bytes        = $bytes
        copied       = $copied
        mbps         = "$mbps"
        files_per_s  = "$fps"
        cpu_one      = "$cpuOneS"
        cpu_all      = "$cpuAllS"
        cycles_file  = "$cyclesPerFile"
        io_rate      = "$ioRate"
        ampl         = "$ampl"
        ctx          = "$ctxS"
        ctx_file     = "$ctxPerFile"
        mach         = "$machPctS"
        peak_ws_mb   = [math]::Round($peakWs / 1MB, 1)
        handles      = $endHandles
        threads      = $endThreads
        diff         = $diffv
        hf_mean      = if ($hm -gt 0) { [math]::Round($hm, 3) } else { $null }
    }
}

function Run-P3 {
    $dur = if ($env:DURABILITY) { $env:DURABILITY } else { 'full' }
    $binaryKind = if ($Published) { 'publish (ReadyToRun, shipped artifact)' } else { 'build (JIT)' }

    ("# TicTack perf run — {0}`n`n- fixture: {1}`n- durability: {2}`n- binary: {3}`n- scenarios: {4}`n" -f (Get-Date -Format o), $Root, $dur, $binaryKind, ($scenarioFilter -join ', ')) |
        Add-Content -LiteralPath $summary
    Write-Host "=== p3 perf @ $Root (durability=$dur, binary=$binaryKind)"

    try {
        New-Fixture $Root | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
    } catch { Note "fixture error: $_ — aborting"; return }

    # diskspd storage baseline (Windows counterpart of fio): is the disk the
    # bottleneck? Bounded write test on the fixture filesystem; skip if missing.
    if (Get-Command diskspd -ErrorAction SilentlyContinue) {
        $spdDir = Join-Path $Root 'diskspd'
        New-Item -ItemType Directory -Force -Path $spdDir | Out-Null
        Step (Join-Path $out 'p3-diskspd.log') 'diskspd write-latency baseline (256MB)' "diskspd -c256M -b64K -w100 -d15 -o8 -t2 -W0 $spdDir\io.dat" {
            & diskspd -c256M -b64K -w100 -d15 -o8 -t2 -W0 (Join-Path $spdDir 'io.dat')
        }
        try {
            $lat = Select-String -LiteralPath (Join-Path $out 'p3-diskspd.log') -Pattern 'avg\.\s*\|\s*([\d.]+)' | Select-Object -First 1
            if ($lat) { Note ("diskspd: mean write latency {0} ms on the fixture filesystem" -f $lat.Matches[0].Groups[1].Value) }
            else { Note 'diskspd: see p3-diskspd.log for the latency table' }
        } catch { Note 'diskspd: could not parse latency — see p3-diskspd.log' }
        Remove-Item -LiteralPath $spdDir -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Note 'diskspd not found (winget install diskspd) — skipping storage baseline'
    }

    if ($Published) {
        # Shipped artifact: dotnet publish with ReadyToRun (from the csproj),
        # same as service\win\install-service.bat. Published into the artifact
        # dir so service\ stays untouched.
        $pubDir = Join-Path $out 'p3-publish'
        Step (Join-Path $out 'p3-build.log') 'publish Release (R2R)' 'dotnet publish src\TicTack.csproj -c Release -o <artifact>\p3-publish' {
            & $dotnet publish (Join-Path $repoRoot 'src\TicTack.csproj') -c Release -o $pubDir -v q --nologo
        }
        if (Select-String -LiteralPath (Join-Path $out 'p3-build.log') -Pattern 'Build FAILED|error CS|error MSB' -Quiet) {
            Note 'publish error — see p3-build.log, aborting'
            return
        }
        $exe = Join-Path $pubDir 'TicTackSv.exe'
    } else {
        Step (Join-Path $out 'p3-build.log') 'build Release' 'dotnet build src\TicTack.csproj -c Release' {
            & $dotnet build (Join-Path $repoRoot 'src\TicTack.csproj') -c Release -v q --nologo
        }
        if (Select-String -LiteralPath (Join-Path $out 'p3-build.log') -Pattern 'Build FAILED|error CS|error MSB' -Quiet) {
            Note 'build error — see p3-build.log, aborting'
            return
        }
        $exe = Join-Path $repoRoot 'src\bin\Release\net10.0-windows\TicTackSv.exe'
    }
    if (-not (Test-Path -LiteralPath $exe)) { Note "binary missing: $exe — aborting"; return }

    # No upfront page-cache warming: each non-warm scenario drops the cache
    # itself right before its timed run (see Invoke-Scenario), so a warmup
    # here would only burn a 2 GB read for no benefit.

    $baselinePath = Join-Path $benchRoot 'baseline-windows.json'
    $baseline = if (Test-Path -LiteralPath $baselinePath) { Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json } else { $null }

    $results = New-Object System.Collections.Generic.List[object]

    Add-Content -LiteralPath $summary -Value ''
    Add-Content -LiteralPath $summary -Value '| scenario | seconds | files | MB/s | files/s | cpu% 1c/all | diff |'
    Add-Content -LiteralPath $summary -Value '|---|---|---|---|---|---|---|'

    foreach ($scenario in $scenarioFilter) {
        if ($scenario -eq 'cold-initial') { $r = Invoke-Scenario 'cold-initial' 2 'date_and_size' $dur $exe $false }
        elseif ($scenario -eq 'warm-noop') { $r = Invoke-Scenario 'warm-noop' 2 'date_and_size' $dur $exe $true }
        elseif ($scenario -eq 'hash-verify') { $r = Invoke-Scenario 'hash-verify' 2 'hash' $dur $exe $false }
        elseif ($scenario -like 'workers=*') {
            $w = [int]$scenario.Substring(8)
            $r = Invoke-Scenario "workers-$w" $w 'date_and_size' $dur $exe $false
        }
        else { Note "unknown scenario: $scenario"; continue }
        $results.Add($r)

        if ($baseline) {
            $b = $baseline | Where-Object { $_.scenario -eq $r.scenario } | Select-Object -First 1
            # Prefer hyperfine means (stable) over single timed runs (noisy);
            # either side falls back to timed seconds when no mean is present.
            $bSec = if ($b.hf_mean -gt 0) { $b.hf_mean } else { $b.seconds }
            $rSec = if ($r.hf_mean -gt 0) { $r.hf_mean } else { $r.seconds }
            if ($b -and $bSec -gt 0 -and $rSec -gt 0) {
                $d = ($rSec - $bSec) / $bSec * 100
                if ($d -gt $RegressionPct) { Note ("REGRESSION: {0} is {1:N0}% slower than baseline ({2:N3}s -> {3:N3}s)" -f $r.scenario, $d, $bSec, $rSec) }
            } elseif (-not $b) {
                Note ("no baseline entry for {0} — comparison skipped" -f $r.scenario)
            }
        }
    }

    # Microbenchmarks (BenchmarkDotNet hot paths): filter matching, arg
    # construction, hex formatting, FileSnapshot equality, StateDb batch ops.
    # ShortRunJob is set in the project — minutes, not tens of minutes.
    $benchProj = Join-Path $benchRoot 'TicTack.Benchmarks.csproj'
    Step (Join-Path $out 'p3-bdn.log') 'BenchmarkDotNet microbenchmarks' "dotnet run -c Release --project TicTack.Benchmarks.csproj -- --filter '*'" {
        & $dotnet run -c Release --project $benchProj -- --filter '*'
    }

    # Verdicts: conclusions, not a raw dump.
    Add-Content -LiteralPath $summary -Value "`n## verdicts`n"
    $cold = $results | Where-Object { $_.scenario -eq 'cold-initial' } | Select-Object -First 1
    $warm = $results | Where-Object { $_.scenario -eq 'warm-noop' } | Select-Object -First 1
    $w1 = $results | Where-Object { $_.scenario -eq 'workers-1' } | Select-Object -First 1
    $w2 = $results | Where-Object { $_.scenario -eq 'workers-2' } | Select-Object -First 1
    $w4 = $results | Where-Object { $_.scenario -eq 'workers-4' } | Select-Object -First 1
    if ($cold) { Note ("cold: {0:N3}s for {1} files ({2} MB/s, {3} files/s)" -f $cold.seconds, $cold.files, $cold.mbps, $cold.files_per_s) }
    if ($cold -and $warm -and ($warm.copied -eq 0) -and ($warm.seconds -gt 0)) {
        Note ("cache: state DB skips everything — {0:N1}x faster (cold {1:N3}s -> warm {2:N3}s)" -f ($cold.seconds / $warm.seconds), $cold.seconds, $warm.seconds)
    }
    if ($w1 -and $w2 -and ($w1.seconds -gt 0) -and ($w2.seconds -gt 0)) {
        $ratio = $w1.seconds / $w2.seconds
        if ($ratio -ge 1.5) { Note ("workers: 2 beats 1 by {0:N2}x — scales" -f $ratio) }
        else { Note ("workers: 2 beats 1 by only {0:N2}x — barely scales, likely I/O-bound" -f $ratio) }
    }
    if ($w2 -and $w4 -and ($w2.seconds -gt 0) -and ($w4.seconds -gt 0)) {
        $ratio24 = $w2.seconds / $w4.seconds
        if ($ratio24 -lt 1.1) { Note ("workers: 4 gains nothing over 2 ({0:N2}x) — I/O-bound, keep default 2" -f $ratio24) }
        else { Note ("workers: 4 beats 2 by {0:N2}x" -f $ratio24) }
    }
    $boundSrc = if ($w2) { $w2 } else { $cold }
    if ($boundSrc -and ($boundSrc.cpu_one -ne 'n/a') -and ($boundSrc.ampl -ne 'n/a')) {
        $cpuN = [double]$boundSrc.cpu_one
        $amplN = [double]$boundSrc.ampl
        if (($cpuN -lt 50) -and ($amplN -ge 1.5)) {
            Note ("bound: disk/fsync-bound — low CPU ({0}% of one core) with {1}x I/O amplification (validation re-reads + temp+rename)" -f $boundSrc.cpu_one, $boundSrc.ampl)
        } elseif ($cpuN -ge 80) {
            Note ("bound: CPU-bound ({0}% of one core) — profile before tuning" -f $boundSrc.cpu_one)
        } else {
            Note ("bound: mixed — CPU {0}% of one core, I/O amplification {1}x" -f $boundSrc.cpu_one, $boundSrc.ampl)
        }
    }
    foreach ($r in $results) {
        Note ("resources {0}: cycles/file {1}, handles {2}, threads {3}, ctx/file {4}, machine {5}%" -f $r.scenario, $r.cycles_file, $r.handles, $r.threads, $r.ctx_file, $r.mach)
    }

    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'p3-results.json')
    if (-not $baseline) { Note 'no benchmarks\baseline-windows.json — adopt this quiet run: Copy-Item diag-out\<ts>\p3-results.json benchmarks\baseline-windows.json' }

    Write-Host ("`n=== summary: diag-out\{0}\SUMMARY.md" -f [IO.Path]::GetFileName($out))
    Note ("results: diag-out\{0}\p3-results.json" -f [IO.Path]::GetFileName($out))
}

# ---------------------------------------------------------------- main ------
if ($runTest) {
    ("# TicTack test run — {0}`n`n- filter: {1}`n" -f (Get-Date -Format o), $(if ($WindowsOnly) { 'Category=Windows' } elseif ($Filter) { $Filter } else { '<none>' })) |
        Set-Content -LiteralPath $summary
    Invoke-Tests
} else {
    if ($runP1) { Run-P1 }
    if ($runP2) { Run-P2 }
    if ($runP3) { Run-P3 }
}
Write-Host ("`n=== artifacts in diag-out\{0}" -f [IO.Path]::GetFileName($out))
