#!/usr/bin/env bash
# TicTack diagnostics / test / perf workflow — Linux.
#
#   tools/diag.sh debug [--full] [--dump] [--io] [--kuni] [PID|NAME]  live triage
#   tools/diag.sh test  [--filter EXPR]               run the xUnit suite
#   tools/diag.sh perf  [FIXTURE_ROOT] [SCENARIOS...] fixtured copy scenarios
#   tools/diag.sh static                              semgrep + ast-grep
#
# Every run writes diag-out/<timestamp>-<mode>/ with a SUMMARY.md recording the
# exact commands used and the file each one produced.
#
# Cheap first, freezing last. Nothing here pauses the target process unless you
# pass --dump and confirm; a wedged (D-state) process is a kernel/filesystem
# problem and no .NET tool will help — the script says so and stops escalating.
set -uo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

if [ -x "$repo_root/.dotnet/dotnet" ]; then DOTNET="$repo_root/.dotnet/dotnet"; else DOTNET=$(command -v dotnet || true); fi
export PATH="$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# If this shell is killed (agent/CI timeout), take the children with it — a
# detached `--once` would keep writing to the destination with nobody watching.
trap 'pkill -P $$ 2>/dev/null' EXIT

mode="${1:-help}"
[ $# -gt 0 ] && shift
out="$repo_root/diag-out/$(date +%Y%m%d-%H%M%S)-$mode"
summary="$out/SUMMARY.md"

have() { command -v "$1" >/dev/null 2>&1; }
hr()   { printf '\n=== %s\n' "$*"; }
note() { printf '%s\n' "$*" >>"$summary"; printf '  %s\n' "$*"; }

# Run one step: log the command into both SUMMARY.md and the step's own file,
# then append the output. Never aborts the run on a non-zero exit.
record() { # record <outfile> <label> <shell-command>
    local f="$1" label="$2" cmd="$3" rc=0
    { printf '\n### %s\n\n```\n$ %s\n```\n' "$label" "$cmd"; } >>"$summary"
    { printf '### %s\n\n```\n$ %s\n```\n\n' "$label" "$cmd"; } >"$f"
    bash -c "$cmd" >>"$f" 2>&1 || rc=$?
    if [ $rc -eq 0 ]; then printf '  ok    %s  -> %s\n' "$label" "${f#"$repo_root"/}"
    else printf '  fail  %s (exit %d) -> %s\n' "$label" "$rc" "${f#"$repo_root"/}"; fi
    return 0
}

confirm() {
    [ "${ASSUME_YES:-0}" = 1 ] && return 0
    if [ -t 0 ]; then
        local a; read -r -p "  $1 [y/N] " a
        [ "$a" = y ] || [ "$a" = Y ]
    else
        printf '  skipped (%s) — non-interactive; re-run with ASSUME_YES=1 to allow\n' "$1"
        return 1
    fi
}

proc_cpu_ticks() { awk '{print $14+$15}' "/proc/$1/stat" 2>/dev/null; }
is_dotnet()      { grep -qa libcoreclr "/proc/$1/maps" 2>/dev/null; }

# The ClrMD-based tools (dotnet-pstacks / dotnet-dstrings / dotnet-gcstats)
# ptrace-attach the target. With yama ptrace_scope>=1 that needs root even for a
# process you own, unlike dotnet-counters/gcdump which use EventPipe.
attach_blocked() {
    [ "$(id -u)" -eq 0 ] && return 1
    local s; s=$(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null || echo 0)
    [ "${s:-0}" != 0 ]
}

# ---------------------------------------------------------------- debug ------
cmd_debug() {
    local deep=0 dump=0 io=0 kuni=0 target=""
    for a in "$@"; do case "$a" in
        --full) deep=1 ;; --dump) dump=1 ;; --io) io=1 ;; --kuni) kuni=1 ;; *) target="$a" ;;
    esac; done

    mkdir -p "$out"
    if [ -z "$target" ]; then
        # Prefer the actual .NET process — `pgrep -f` also matches the shell
        # that launched it. Newest match wins when several are running.
        target=$(for p in $(pgrep -f TicTackSv); do is_dotnet "$p" && echo "$p"; done | tail -1)
        [ -z "$target" ] && target=$(pgrep -f TicTackSv | tail -1)
        [ -z "$target" ] && { echo "No TicTackSv process found; pass a PID or name." >&2; exit 2; }
    fi
    local pid
    if [[ "$target" =~ ^[0-9]+$ ]]; then pid=$target
    else pid=$(pgrep -f "$target" | tail -1); fi
    [ -z "$pid" ] || [ ! -d "/proc/$pid" ] && { echo "No such process: $target" >&2; exit 2; }

    {
        printf '# TicTack diagnostic summary — %s\n\n' "$(date -Is)"
        printf -- '- host: `%s`\n- pid: `%s`\n- target: `%s`\n- mode: debug%s%s\n' \
            "$(uname -srm)" "$pid" "$target" "$([ $deep = 1 ] && echo ' --full')" "$([ $dump = 1 ] && echo ' --dump')"
    } >"$summary"
    hr "pid $pid"

    local pfx="" sudo_missing=0
    if [ "$(id -u)" -ne 0 ] && { [ ! -r "/proc/$pid/stat" ] || attach_blocked; }; then
        if sudo -n true 2>/dev/null; then pfx="sudo -n"; else sudo_missing=1; fi
    fi
    # Attach with a prefix when root is required; otherwise print the command.
    priv() { # priv <outfile> <label> <command>
        if [ "$sudo_missing" = 1 ]; then
            printf '  skip  %s (needs root) — run: sudo %s\n' "$2" "$3"
            printf '\n### %s — NOT RUN (needs root)\n\n```\n$ sudo %s\n```\n' "$2" "$3" >>"$summary"
            return 0
        fi
        record "$1" "$2" "${pfx:+$pfx }$3"
    }

    record "$out/process.txt" "process identity" \
        "ps -o pid,ppid,etime,time,stat,rss,nlwp,user,args -p $pid"

    local dcount
    dcount=$(ps -L -p "$pid" -o stat= 2>/dev/null | awk '$1 ~ /^D/' | wc -l)
    record "$out/threads.txt" "thread states" \
        "ps -L -p $pid -o lwp,stat,pcpu,wchan:24,comm"
    if have pidstat; then
        record "$out/pidstat.txt" "pidstat per-thread I/O (1s x3)" \
            "pidstat -p $pid 1 3"
    else
        note "pidstat not found — skipping per-thread I/O snapshot"
    fi

    # Stuck-vs-slow: CPU time consumed over a fixed window. ~0% + not D = blocked
    # on something .NET-side (lock / await); 0% + D = blocked in the kernel.
    local hz t0 t1 dcpu pct="?"
    hz=$(getconf CLK_TCK 2>/dev/null || echo 100)
    t0=$(proc_cpu_ticks "$pid"); sleep 3; t1=$(proc_cpu_ticks "$pid")
    if [ -n "$t0" ] && [ -n "$t1" ]; then
        dcpu=$((t1 - t0))
        pct=$(awk -v d="$dcpu" -v h="$hz" 'BEGIN{printf "%.1f", d/h/3*100}')
        note "cpu: ${dcpu} ticks in 3s = ${pct}% of one core"
    else
        note "cpu: /proc/$pid not readable without root — cannot measure busy vs idle"
    fi

    # Resource snapshot: what the process is holding right now.
    if [ -r "/proc/$pid/status" ]; then
        local rss swap fds
        rss=$(awk '/^VmRSS:/{print $2$3}' "/proc/$pid/status")
        swap=$(awk '/^VmSwap:/{print $2$3}' "/proc/$pid/status")
        fds=$(ls "/proc/$pid/fd" 2>/dev/null | wc -l)
        note "resources: RSS ${rss:-?}, swap ${swap:-?}, ${fds} open fds, ${dcount} D-state threads"
    fi

    if [ "$dcount" -gt 0 ]; then
        note "VERDICT: $dcount thread(s) in D state — blocked in kernel I/O."
        note "         Not a .NET problem: check the destination filesystem and"
        note "         run 'dmesg | tail -30' / 'cat /proc/$pid/task/<spid>/stack'."
    elif [ "$pct" != "?" ] && [ "${pct%%.*}" -eq 0 ]; then
        note "VERDICT: idle CPU — blocked on a lock or an await, not spinning."
        note "         Escalate to --full (parallel stacks) next."
    else
        note "VERDICT: busy — it is making progress; get a profile, not a hang dump."
    fi

    # ---- tier 1: always (cheap, never freezes) ----
    if is_dotnet "$pid"; then
        record "$out/counters.json" "dotnet-counters 30s" \
            "dotnet-counters collect -p $pid --counters System.Runtime,TicTack --format json --duration 00:00:30 -o '$out/counters.json'"
        if [ -f "$out/counters.json" ]; then
            note "counters: Events[] holds one row per counter per sample"
            note "          (name, tags, timestamp, value). Group by name+tags and"
            note "          compare first vs last: gc-heap-size, gen-2-gc-count,"
            note "          threadpool-queue-length, monitor-lock-contention-count,"
            note "          plus the TicTack/* in-app sync counters"
        fi
    else
        note "counters: /proc/$pid/maps shows no CoreCLR — not a .NET process, skipping"
    fi

    # ---- tier 2: --full (still no freeze) ----
    if [ "$deep" = 1 ]; then
        if [ "$sudo_missing" = 1 ]; then
            note "note: pstacks/dstrings/gcstats ptrace-attach the target and"
            note "      /proc/sys/kernel/yama/ptrace_scope is $(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null)"
            note "      so they need root — the sudo lines are recorded in SUMMARY.md"
        fi
        priv "$out/gcstats.txt" "dotnet-gcstats (60s)"   "dotnet-gcstats $pid"
        priv "$out/pstacks.txt" "dotnet-pstacks"         "dotnet-pstacks -p $pid > '$out/pstacks.txt'"
        # gcdump uses EventPipe, so it works without root.
        record "$out/gcdump-baseline.gcdump" "gcdump baseline" \
            "dotnet-gcdump collect -p $pid -o '$out/gcdump-baseline.gcdump'"
        sleep 10
        record "$out/gcdump-after.gcdump" "gcdump after 10s" \
            "dotnet-gcdump collect -p $pid -o '$out/gcdump-after.gcdump'"
        if [ -s "$out/gcdump-baseline.gcdump" ] && [ -s "$out/gcdump-after.gcdump" ]; then
            record "$out/gcdump-report.txt" "gcdump report + diff" \
                "dotnet-gcdump report '$out/gcdump-baseline.gcdump' > '$out/gcdump-report-base.txt'; dotnet-gcdump report '$out/gcdump-after.gcdump' > '$out/gcdump-report-after.txt'; diff '$out/gcdump-report-base.txt' '$out/gcdump-report-after.txt' || true"
            note "gcdump: baseline/after reports + diff in gcdump-report*.txt (empty diff = flat heap over 10s)"
        fi
        priv "$out/dstrings.txt" "dotnet-dstrings"       "dotnet-dstrings -p $pid > '$out/dstrings.txt'"
        [ -f "$out/dstrings.txt" ] && [ ! -s "$out/dstrings.txt" ] && \
            note "dstrings empty — retry with 'dotnet-dstrings -p $pid -c 32 -s 10'"

        # EventPipe / diagnostics-IPC tools — no ptrace, so no root needed.
        record "$out/trace.nettrace" "dotnet-trace 30s" \
            "dotnet-trace collect -p $pid --duration 00:00:30 -o '$out/trace.nettrace'"
        [ -s "$out/trace.nettrace" ] && \
            record "$out/trace.speedscope.log" "Speedscope flame graph" \
                "dotnet-trace convert '$out/trace.nettrace' --format Speedscope -o '$out/trace'"
        [ -s "$out/trace.speedscope.json" ] && \
            note "trace: flame graph in trace.speedscope.json (open in Speedscope, or PerfView on Windows)"
        [ -s "$out/trace.nettrace" ] || \
            note "trace: no .nettrace collected — skipping Speedscope conversion"
        record "$out/stack.txt" "dotnet-stack report" \
            "dotnet-stack report -p $pid"
        [ -s "$out/stack.txt" ] && \
            note "stack: no-dump managed stacks — use when a thread holds a lock in a native call (CloseHandle/ReadFile)"
        # dotnet-fullgc v1.2.0 crashes without -csn (IndexOutOfRange in its arg parsing).
        record "$out/fullgc.txt" "dotnet-fullgc" \
            "dotnet-fullgc $pid -csn 0"
        [ -s "$out/fullgc.txt" ] && \
            note "fullgc: forced full GC — if the heap does not shrink, the retention is real (see dotnet-memory-analysis skill)"
    fi

    # ---- tier 2b: --io (BPF/fs tracing, needs root, never freezes) ----
    #
    # Opt-in because it needs root + bcc-tools/bpftrace installed
    # (sudo pacman -S --needed bcc-tools bpftrace). Bounded captures only:
    # timeout-wrapped tools plus offcputime's own 20 s window. Skipped with a
    # recorded sudo line when root is unavailable (see priv()).
    if [ "$io" = 1 ]; then
        have timeout || note "timeout(1) not found — unbounded BPF tools cannot run safely, skipping --io tier"
        if have timeout; then
            priv "$out/fsync-bpftrace.txt" "bpftrace fsync latency hist (15s)" \
                "timeout 15 bpftrace -e 'tracepoint:syscalls:sys_enter_fsync,sys_enter_fdatasync{ @s[tid]=nsecs; } tracepoint:syscalls:sys_exit_fsync,sys_exit_fdatasync /@s[tid]/{ @h[probe]=hist((nsecs-@s[tid])/1000); delete(@s[tid]); }'"
            priv "$out/syncsnoop.txt" "syncsnoop 15s" \
                "timeout 15 syncsnoop -p $pid"
            # Filesystem-specific slower tool when one matches the target's
            # cwd fs (ext4/xfs/btrfs variants print per-op + filename);
            # generic fsslower otherwise.
            slower=fsslower
            fstype=$(stat -f -c %T "/proc/$pid/cwd" 2>/dev/null || echo "?")
            case "$fstype" in
                ext2|ext3|ext4) slower=ext4slower ;;
                xfs) slower=xfsslower ;;
                btrfs) slower=btrfsslower ;;
            esac
            have "$slower" || slower=fsslower
            priv "$out/fsslower.txt" "$slower 15s ($fstype)" \
                "timeout 15 $slower -p $pid 1"
            priv "$out/funclatency.txt" "funclatency vfs_fsync 15s" \
                "timeout 15 funclatency -p $pid vfs_fsync"
            priv "$out/offcpu.txt" "offcputime 20s" \
                "offcputime -p $pid 20"
        fi
    fi

    # ---- tier 2c: --kuni (unified managed+kernel+native trace, root, no freeze) ----
    #
    # dotnet-trace collect-linux streams EventPipe as kernel user_events plus
    # perf_events CPU sampling: one trace with managed stacks, kernel events
    # and native frames. Needs root, Linux >= 6.4, tracefs mounted and a
    # .NET 10+ target (check up front: `dotnet-trace collect-linux --probe`).
    # Preview-format .nettrace: PerfView reads it, other viewers may lag.
    if [ "$kuni" = 1 ]; then
        krel=$(uname -r); kmaj=${krel%%.*}; kmin=${krel#*.}; kmin=${kmin%%.*}
        if { [ "$kmaj" -gt 6 ] || { [ "$kmaj" -eq 6 ] && [ "$kmin" -ge 4 ]; }; } && [ -d /sys/kernel/tracing ]; then
            priv "$out/trace-kuni.nettrace" "dotnet-trace collect-linux 30s" \
                "dotnet-trace collect-linux -p $pid --duration 00:00:30 -o '$out/trace-kuni.nettrace'"
            [ -s "$out/trace-kuni.nettrace" ] && \
                note "kuni: unified trace — managed + kernel + native frames; open in PerfView (preview format)"
        else
            note "kuni: needs kernel >= 6.4 + tracefs (have $krel) — skipping"
        fi
        unset krel kmaj kmin
    fi

    # ---- tier 3: --dump (freezes the process) ----
    #
    # Same-user dump + offline analysis. dotnet-dump collect only needs to run as
    # the target's user, so no root. And the ClrMD tools that cannot ptrace-attach
    # here (ptrace_scope=1) CAN read a dump file, so pstacks/dstrings are pointed
    # at the dump instead of the live process. gcstats is live-only, so it has no
    # dump equivalent.
    # A .dmp contains process memory: it lives in /tmp and is deleted right after
    # the analysis text is written next to it in the artifact dir.
    if [ "$dump" = 1 ]; then
        note "WARNING: 'dotnet-dump collect' pauses the process for the whole dump."
        local dmp="/tmp/tictack-$pid-$(date +%Y%m%d-%H%M%S).dmp"
        local free_mb
        free_mb=$(df -Pm /tmp 2>/dev/null | awk 'NR==2{print $4}')
        note "dump: target $dmp (free in /tmp: ${free_mb:-?} MB); deleted after analysis"
        if confirm "Freeze pid $pid to take a full dump into /tmp?"; then
            record "$out/dump-collect.txt" "dotnet-dump collect (FREEZES)" \
                "dotnet-dump collect -p $pid -o '$dmp'"
            if [ -s "$dmp" ]; then
                note "dump: $(du -m "$dmp" | cut -f1) MB collected"
                record "$out/dump-heap.txt" "SOS: eeheap + dumpheap" \
                    "dotnet-dump analyze '$dmp' -c 'eeheap -gc' -c 'dumpheap -stat' -c exit"
                # Leak-triage chain: biggest live type -> 2 instances -> gcroot +
                # objsize each, plus finalizequeue (undisposed backlog). Bounded:
                # head -80 per analyze call so one chatty root cannot flood the log.
                # Instance rows are matched as `$2 == MT`: a bare `^0000` address
                # match catches SOS preamble (e.g. the syncblk zero row) on some
                # runtimes, and Linux addresses carry no leading zeros anyway.
                mt=$(awk 'NF>=4 && $1 ~ /^[0-9a-fA-F]+$/ && $NF != "Free" {if ($3+0>max){max=$3; mt=$1}} END{print mt}' "$out/dump-heap.txt")
                if [ -n "$mt" ]; then
                    record "$out/dump-leak.txt" "SOS leak chain ($mt)" \
                        "for a in \$(dotnet-dump analyze '$dmp' -c 'dumpheap -mt $mt' -c exit | awk -v mt=$mt 'tolower(\$2)==tolower(mt){print \$1}' | head -2); do dotnet-dump analyze '$dmp' -c \"gcroot \$a\" -c \"objsize \$a\" -c exit | head -80; done; dotnet-dump analyze '$dmp' -c finalizequeue -c exit"
                else
                    note "dump: no MT parsed from dump-heap.txt — skipping leak chain"
                fi
                record "$out/dump-pstacks.txt" "dotnet-pstacks <dump>" \
                    "dotnet-pstacks '$dmp'"
                record "$out/dump-dstrings.txt" "dotnet-dstrings <dump>" \
                    "dotnet-dstrings '$dmp'"
                [ -s "$out/dump-dstrings.txt" ] || \
                    note "dump dstrings empty — retry 'dotnet-dstrings '$dmp' -c 32 -s 10'"
                rm -f "$dmp"
                if [ ! -e "$dmp" ]; then
                    note "dump: deleted $dmp — analysis text kept in ${out#"$repo_root"/}/"
                else
                    note "dump: COULD NOT delete $dmp — remove it by hand (holds process memory)"
                fi
            else
                note "dump: collect produced no file — skipping offline analysis"
            fi
        fi
    fi

    hr "artifacts in ${out#"$repo_root"/}/"
    printf '  summary: %s\n' "${summary#"$repo_root"/}"
}

# ----------------------------------------------------------------- test ------
cmd_test() {
    local filter=""
    while [ $# -gt 0 ]; do case "$1" in
        --filter) filter="${2:-}"; shift 2 ;; *) shift ;;
    esac; done
    mkdir -p "$out"
    { printf '# TicTack test run — %s\n\n- filter: `%s`\n' "$(date -Is)" "${filter:-<none>}"; } >"$summary"
    hr "xUnit suite"
    local f=""
    [ -n "$filter" ] && f="--filter '$filter'"
    record "$out/test.log" "dotnet test" \
        "$DOTNET test tests/TicTack.Tests.csproj -c Release $f --logger 'trx;LogFileName=test-results.trx'"
    grep -E '^(Passed!|Failed!|error)' "$out/test.log" | tail -3

    # --blame-hang writes a Sequence*.xml next to the results on a hang.
    local hang
    hang=$(find "$repo_root/tests/TestResults" -name 'Sequence*.xml' -newermt '-2 hours' 2>/dev/null | head -1)
    [ -n "$hang" ] && note "hang detected: $hang (CI uploads the same file as an artifact)"
    hr "summary: ${summary#"$repo_root"/}"
}

# ----------------------------------------------------------------- perf ------
perf_scenario() {
    local name="$1" workers="$2" verification="$3" warm="$4"
    local cfg="$out/bench-$name.yaml"
    cat >"$cfg" <<EOF
sources:
  - path: '$root/src'
    destination: '$root/dst'
    state_db_path: '$root/state'
    debounce_seconds: 0
    filter:
      max_file_size_mb: no-limit
    sync:
      verification: $verification
      durability: $dur
      initial_sync_workers: $workers
logging:
  level: info
  path: '$root/tictack.log'
  max_size_mb: 10
  max_files: 2
  console: true
  alert_path: '$out'
EOF

    hr "scenario $name (workers=$workers, verification=$verification)"
    rm -rf "$root/dst" "$root/state"
    if [ "$warm" = 1 ]; then
        record "$out/run-$name-warmup.log" "warmup $name" \
            "$DOTNET '$dll' --once --config '$cfg'"
    fi

    local start end secs
    start=$(date +%s%N)
    record "$out/run-$name.log" "timed $name" \
        "$DOTNET '$dll' --once --config '$cfg'"
    end=$(date +%s%N)
    secs=$(awk -v a="$start" -v b="$end" 'BEGIN{printf "%.3f", (b-a)/1e9}')

    if [ "$warm" = 1 ]; then
        local copied
        copied=$(grep -oE '[0-9]+ copied' "$out/run-$name.log" | tail -1 | cut -d' ' -f1)
        [ -n "$copied" ] && note "warm-noop copied=$copied (expect 0)"
    fi

    local files bytes mbps fps load diffv
    files=$(find "$root/dst" -type f 2>/dev/null | wc -l | tr -d ' ')
    bytes=$(du -sb "$root/dst" 2>/dev/null | cut -f1)
    bytes=${bytes:-0}
    mbps=$(awk -v b="$bytes" -v s="$secs" 'BEGIN{printf "%.1f", (s>0 ? b/1048576/s : 0)}')
    fps=$(awk -v f="$files" -v s="$secs" 'BEGIN{printf "%.1f", (s>0 ? f/s : 0)}')
    load=$(cut -d' ' -f1-3 /proc/loadavg)

    diff -r "$root/src" "$root/dst" >"$out/diff-$name.txt" 2>&1
    if [ -s "$out/diff-$name.txt" ]; then diffv="mismatch"; else diffv="match"; fi

    note "scenario $name: ${secs}s, $files files, $mbps MB/s, $fps files/s, load $load, diff $diffv"
    printf '| %s | %s | %s | %s | %s | %s | %s |\n' \
        "$name" "$secs" "$files" "$mbps" "$fps" "$load" "$diffv" >>"$summary"

    # hyperfine: statistically rigorous repeats with prepare-hook cache
    # control. Additive — the single timed run above stays the
    # baseline.json input; hyperfine's mean is the number to quote.
    # The JSON goes straight to its own file: record() appends human
    # output into its log file, which would corrupt JSON parsing
    # (same lesson as semgrep in cmd_static).
    if have hyperfine; then
        record "$out/run-$name-hyperfine.log" "hyperfine $name (1 warmup + 3 runs)" \
            "hyperfine --warmup 1 --runs 3 --export-json '$out/run-$name-hyperfine.json' --prepare \"rm -rf '$root/dst' '$root/state'\" \"$DOTNET '$dll' --once --config '$cfg'\""
        local hmean
        hmean=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['results'][0]['mean'])" \
            "$out/run-$name-hyperfine.json" 2>/dev/null || echo "")
        [ -n "$hmean" ] && note "hyperfine $name: mean ${hmean}s over 3 runs"
    else
        note "hyperfine not found — skipping rigorous repeats (sudo pacman -S --needed hyperfine)"
    fi

    [ "$sep" = "," ] && printf ',\n' >>"$json"
    sep=","
    printf '  {"scenario": "%s", "seconds": %s, "files": %s, "bytes": %s, "mbps": %s, "files_per_s": %s, "load": "%s", "diff": "%s"}' \
        "$name" "$secs" "$files" "$bytes" "$mbps" "$fps" "$load" "$diffv" >>"$json"

    if [ -f "$repo_root/benchmarks/baseline.json" ] && command -v jq >/dev/null 2>&1; then
        local base
        base=$(jq -r --arg s "$name" '.[]? | select(.scenario == $s) | .seconds' \
            "$repo_root/benchmarks/baseline.json" 2>/dev/null | head -1)
        if [ -n "$base" ] && [ "$base" != "null" ]; then
            awk -v n="$secs" -v b="$base" -v nm="$name" 'BEGIN{
                d = (b > 0 ? (n-b)/b*100 : 0);
                if (d > 20) printf "REGRESSION: %s is %.0f%% slower than baseline (%.3fs -> %.3fs)\n", nm, d, b, n;
            }' | while read -r l; do note "$l"; done
        fi
    fi
}

cmd_perf() {
    local root="${1:-${FIXTURE_ROOT:-/home/user/sync-bench}}"
    shift || true
    local dur="${DURABILITY:-full}"
    local dll="$repo_root/src/bin/Release/net10.0-windows/TicTackSv.dll"
    local json="$out/results.json"
    local sep="" scenario w

    local scenarios=("$@")
    [ "${#scenarios[@]}" -eq 0 ] && scenarios=(cold-initial warm-noop hash-verify workers=1 workers=2 workers=4)

    mkdir -p "$out"
    { printf '# TicTack perf run — %s\n\n- fixture: `%s`\n- durability: `%s`\n- scenarios: `%s`\n' \
        "$(date -Is)" "$root" "$dur" "${scenarios[*]}"; } >"$summary"
    hr "perf @ $root (durability=$dur)"

    bash "$repo_root/benchmarks/create-fixture.sh" "$root" | tail -3

    # fio storage baseline: is the disk the bottleneck? 64 MB sequential
    # write with fsync per write on the fixture filesystem — the fsync mean
    # is the latency floor no copy pipeline can beat. Small on purpose;
    # scratch dir removed afterwards. JSON straight to its own file (see
    # the hyperfine note above about record() and JSON).
    if have fio; then
        mkdir -p "$root/fio"
        record "$out/fio.log" "fio fsync-latency baseline (64MB)" \
            "fio --name=tictack --directory='$root/fio' --rw=write --size=64m --fsync=1 --output-format=json --output='$out/fio.json'"
        local flat
        flat=$(python3 -c "import json,sys;j=json.load(open(sys.argv[1]))['jobs'][0]['sync']['lat_ns'];print('%.3f'% (j['mean']/1e6))" \
            "$out/fio.json" 2>/dev/null || echo "")
        [ -n "$flat" ] && note "fio: mean fsync latency ${flat} ms on the fixture filesystem"
        rm -rf "$root/fio"
    else
        note "fio not found — skipping storage baseline (sudo pacman -S --needed fio)"
    fi

    record "$out/build.log" "build Release" \
        "$DOTNET build src/TicTack.csproj -c Release -v q --nologo"
    if grep -qiE '(Build FAILED|error CS|error MSB)' "$out/build.log"; then
        note "build error — see build.log, aborting"
        return 1
    fi

    hr "warming page cache"
    find "$root/src" -type f -print0 2>/dev/null | xargs -0 cat >/dev/null 2>&1

    printf '| scenario | seconds | files | MB/s | files/s | load | diff |\n|---|---|---|---|---|---|---|\n' >>"$summary"
    printf '[\n' >"$json"

    for scenario in "${scenarios[@]}"; do
        case "$scenario" in
            cold-initial) perf_scenario cold-initial 2 date_and_size 0 ;;
            warm-noop)    perf_scenario warm-noop 2 date_and_size 1 ;;
            hash-verify)  perf_scenario hash-verify 2 hash 0 ;;
            workers=*)    w="${scenario#workers=}"; perf_scenario "workers-$w" "$w" date_and_size 0 ;;
            *)            note "unknown scenario: $scenario" ;;
        esac
    done

    printf '\n]\n' >>"$json"
    if [ ! -f "$repo_root/benchmarks/baseline.json" ]; then
        note "no benchmarks/baseline.json — baseline comparison skipped"
    elif ! command -v jq >/dev/null 2>&1; then
        note "jq not found — baseline comparison skipped"
    fi

    hr "summary: ${summary#"$repo_root"/}"
    note "results: ${json#"$repo_root"/}"
}

# ---------------------------------------------------------------- static -----
# Local static analysis gate. Mirrored by the `static` CI job
# (.github/workflows/ci.yml), which installs both tools on the runner.
cmd_static() {
    mkdir -p "$out"
    { printf '# TicTack static analysis — %s\n\n' "$(date -Is)"; } >"$summary"
    hr "static analysis"
    if have semgrep; then
        # -o writes pure JSON, so run it directly: record() would prepend a
        # header into the file and break JSON parsing.
        { printf '\n### semgrep r/csharp\n\n```\n$ %s\n```\n' \
            "semgrep --config r/csharp --metrics off --json -o semgrep.json '$repo_root/src'"; } >>"$summary"
        local rc=0 count="?"
        semgrep --config r/csharp --metrics off --json -o "$out/semgrep.json" "$repo_root/src" \
            >"$out/semgrep.log" 2>&1 || rc=$?
        [ -f "$out/semgrep.json" ] && \
            count=$(python3 -c "import json,sys;print(len(json.load(open(sys.argv[1])).get('results',[])))" \
                "$out/semgrep.json" 2>/dev/null || count="?")
        note "semgrep: $count findings (r/csharp, exit $rc) -> ${out#"$repo_root"/}/semgrep.json"
    else
        note "semgrep not found — skipping semgrep r/csharp"
    fi
    if have ast-grep; then
        record "$out/ast-grep.txt" "ast-grep scan (rules/ bank)" \
            "cd '$repo_root' && ast-grep scan"
    else
        note "ast-grep not found — skipping banked rules scan (rules/)"
    fi
    hr "summary: ${summary#"$repo_root"/}"
}

# ----------------------------------------------------------------- main ------
case "$mode" in
    debug) cmd_debug "$@" ;;
    test)  cmd_test  "$@" ;;
    perf)  cmd_perf  "$@" ;;
    static) cmd_static "$@" ;;
    *)     sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
esac
