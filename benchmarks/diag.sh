#!/usr/bin/env bash
# TicTack diagnostics — Linux. One phased workflow, everything lives in benchmarks/.
#
#   benchmarks/diag.sh [-p1] [-p2] [-p3] [--full] [--dump] [--io] [--kuni] [--published] [PID|NAME] [SCENARIOS...]
#   benchmarks/diag.sh test [--filter EXPR]
#
# No -p flag = all three phases. -p1 -p2 = first two only, -p1 alone = triage only.
# --published (p3): measure the shipped artifact — dotnet publish (ReadyToRun,
#   same as service/linux/install-service.sh) instead of the JIT build output.
#
# No -p flag = all three phases. -p1 -p2 = first two only, -p1 alone = triage only.
# Phases: p1 live triage (process + dotnet-counters + optional --full depth),
#         p2 static analysis + unit tests, p3 fixtured copy scenarios + microbenchmarks.
#
# Every run writes diag-out/<timestamp>-perf/ (or -test) with a SUMMARY.md recording
# the exact commands used and the file each one produced.
#
# Cheap first, freezing last. Nothing here pauses the target process unless you
# pass --dump and confirm; a wedged (D-state) process is a kernel/filesystem
# problem and no .NET tool will help — the script says so and stops escalating.
# PID/NAME is automated: omitted means the running TicTackSv process.
set -uo pipefail

# Perf regression threshold, shared contract with diag.ps1 and documented in
# docs/linux-debug-perf.md (perf section): a scenario slower than its
# benchmarks/baseline-<os>.json entry by more than this percent is reported.
REGRESSION_PCT=20

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
bench_root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

# Under sudo, root's PATH lacks the caller's dotnet tools (and usually any
# dotnet at all), and DOTNET_ROOT is unset — every framework-dependent
# dotnet-* apphost then dies with SIGQUIT (exit 131) after printing
# "You must install .NET to run this application". Pull the SDK layout back:
# repo-local SDK first, then the caller's user-global one.
if [ "$(id -u)" -eq 0 ] && [ -n "${SUDO_USER:-}" ]; then
    _sudo_home=$(getent passwd "$SUDO_USER" 2>/dev/null | cut -d: -f6)
    if [ -x "$repo_root/.dotnet/dotnet" ]; then
        export DOTNET_ROOT="$repo_root/.dotnet"
        export PATH="$repo_root/.dotnet:$PATH"
    elif [ -n "$_sudo_home" ] && [ -x "$_sudo_home/.dotnet/dotnet" ]; then
        export DOTNET_ROOT="$_sudo_home/.dotnet"
        export PATH="$_sudo_home/.dotnet:$PATH"
    fi
    [ -n "$_sudo_home" ] && [ -d "$_sudo_home/.dotnet/tools" ] && \
        export PATH="$_sudo_home/.dotnet/tools:$PATH"
    unset _sudo_home
fi

if [ -x "$repo_root/.dotnet/dotnet" ]; then DOTNET="$repo_root/.dotnet/dotnet"; else DOTNET=$(command -v dotnet || true); fi
export PATH="$HOME/.dotnet/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# If this shell is killed (agent/CI timeout), take the children with it — a
# detached `--once` would keep writing to the destination with nobody watching.
trap 'pkill -P $$ 2>/dev/null' EXIT

# ---- flag routing: 'test' = suite mode, scenario names = p3 filter,
# ---- anything else = p1 target override. No -p flag = all phases.
p1=0; p2=0; p3=0; full=0; dump=0; io=0; kuni=0; published=0
filter=""; target=""; root=""; testmode=0
scenarios=()
while [ $# -gt 0 ]; do case "$1" in
    -p1) p1=1 ;;
    -p2) p2=1 ;;
    -p3) p3=1 ;;
    --full) full=1 ;; --dump) dump=1 ;; --io) io=1 ;; --kuni) kuni=1 ;;
    --published) published=1 ;;
    --filter) filter="${2:-}"; shift ;;
    --root) root="${2:-}"; shift ;;
    -h|--help|help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) printf 'unknown flag: %s\n' "$1" >&2; exit 2 ;;
    test) testmode=1 ;;
    cold-initial|warm-noop|hash-verify|workers=*) scenarios+=("$1") ;;
    *) [ -z "$target" ] && target="$1" ;;
esac; shift; done
[ "$testmode" = 0 ] && { [ "$p1$p2$p3" = 000 ] && { p1=1; p2=1; p3=1; }; }
[ "${#scenarios[@]}" -eq 0 ] && scenarios=(cold-initial warm-noop hash-verify workers=1 workers=2 workers=4)
[ -z "$root" ] && root="${FIXTURE_ROOT:-/home/user/sync-bench}"
[ "$testmode" = 1 ] && tag=test || tag=perf
out="$repo_root/diag-out/$(date +%Y%m%d-%H%M%S)-$tag"
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

# Resolve a dotnet-* tool: global install first, dnx one-shot second (ships
# with the .NET 10 SDK, same as CI), empty + skip-note last.
pick() { if have "$1"; then echo "$1"; elif have dnx; then echo "dnx $1"; else echo ""; fi; }
need() { # need <name> <resolved> — 0 when usable, else a skip note + 1
    [ -n "$2" ] && return 0
    note "$1 not found (dotnet tool install -g $1, or run via dnx) — skipping"
    return 1
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

# ------------------------------------------------------------------ p1 ------
cmd_p1() {
    local T_COUNTERS T_GCSTATS T_PSTACKS T_GCDUMP T_DSTRINGS T_TRACE T_STACK T_FULLGC T_DUMP
    T_COUNTERS=$(pick dotnet-counters); T_GCSTATS=$(pick dotnet-gcstats)
    T_PSTACKS=$(pick dotnet-pstacks);   T_GCDUMP=$(pick dotnet-gcdump)
    T_DSTRINGS=$(pick dotnet-dstrings); T_TRACE=$(pick dotnet-trace)
    T_STACK=$(pick dotnet-stack);       T_FULLGC=$(pick dotnet-fullgc)
    T_DUMP=$(pick dotnet-dump)

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
        printf -- '- host: `%s`\n- pid: `%s`\n- target: `%s`\n- phase: p1 live triage%s%s\n' \
            "$(uname -srm)" "$pid" "$target" "$([ $full = 1 ] && echo ' --full')" "$([ $dump = 1 ] && echo ' --dump')"
    } >"$summary"
    hr "p1 live triage, pid $pid"

    # B4: EventPipe preflight. Every dotnet-* tool except pstacks/dstrings
    # needs the target's diagnostics IPC socket; without it they fail with
    # misleading errors (seen live: 6 cascading FAILs against a socketless
    # target). Detect once, skip honestly, keep the tools that still work.
    local no_ipc=0
    if ! ls -d /tmp/dotnet-diagnostic-"$pid"-* >/dev/null 2>&1; then
        no_ipc=1
        note "no diagnostics IPC socket (/tmp/dotnet-diagnostic-$pid-*) — EventPipe tools skipped"
        note "     the runtime never created it (target-side). Restart the service"
        note "     (sudo systemctl restart tictack) and re-run; pstacks/dstrings below still work"
    fi

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

    record "$out/p1-process.txt" "process identity" \
        "ps -o pid,ppid,etime,time,stat,rss,nlwp,user,args -p $pid"

    local dcount
    dcount=$(ps -L -p "$pid" -o stat= 2>/dev/null | awk '$1 ~ /^D/' | wc -l)
    record "$out/p1-threads.txt" "thread states" \
        "ps -L -p $pid -o lwp,stat,pcpu,wchan:24,comm"
    if have pidstat; then
        record "$out/p1-pidstat.txt" "pidstat per-thread I/O (1s x3)" \
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
        if [ -r "/proc/$pid/fd" ]; then fds=$(ls "/proc/$pid/fd" 2>/dev/null | wc -l)
        else fds="? (fd dir not readable — owner/root?)"; fi
        note "resources: RSS ${rss:-?}, swap ${swap:-?}, ${fds} open fds, ${dcount} D-state threads"
    fi

    if [ "$dcount" -gt 0 ]; then
        note "VERDICT: $dcount thread(s) in D state — blocked in kernel I/O."
        note "         Not a .NET problem: check the destination filesystem and"
        note "         run 'dmesg | tail -30' / 'cat /proc/$pid/task/<spid>/stack'."
    elif [ "$pct" != "?" ] && [ "${pct%%.*}" -eq 0 ]; then
        note "VERDICT: idle CPU — blocked on a lock or an await, not spinning."
        if [ "$full" = 1 ]; then
            note "         --full is on: read pstacks/trace above for the blocking stack."
        else
            note "         Escalate to --full (parallel stacks) next."
        fi
    else
        note "VERDICT: busy — it is making progress; get a profile, not a hang dump."
    fi

    # ---- tier 1: always (cheap, never freezes) ----
    run_counters() {
        record "$out/p1-counters.json" "dotnet-counters 30s" \
            "$T_COUNTERS collect -p $pid --counters System.Runtime,TicTack --format json --duration 00:00:30 -o '$out/p1-counters.json'"
        if [ -f "$out/p1-counters.json" ]; then
            note "counters: Events[] holds one row per counter per sample"
            note "          (name, tags, timestamp, value). Group by name+tags and"
            note "          compare first vs last: gc-heap-size, gen-2-gc-count,"
            note "          threadpool-queue-length, monitor-lock-contention-count,"
            note "          plus the TicTack/* in-app sync counters"
            # The deployed binary can predate the in-app EventSource (the
            # provider then simply never shows up) — say which case this is.
            # Match the quoted provider, not "TicTackSv" in TargetProcess.
            grep -q '"TicTack"' "$out/p1-counters.json" || \
                note "counters: no TicTack/* series — target binary predates the in-app EventSource (rebuild/redeploy), or the provider is off"
        fi
    }
    if [ "$no_ipc" = 1 ]; then
        note "counters: skipped (no IPC socket — see above)"
    elif is_dotnet "$pid"; then
        need dotnet-counters "$T_COUNTERS" && run_counters
    elif ! head -c1 "/proc/$pid/maps" >/dev/null 2>&1; then
        # maps unreadable (another user?) — cannot confirm CoreCLR, but
        # EventPipe may still connect, so attempt instead of skipping.
        note "counters: /proc/$pid/maps not readable — cannot confirm CoreCLR, attempting anyway"
        note "          (run as the service user or with sudo -n if the connect fails)"
        need dotnet-counters "$T_COUNTERS" && run_counters
    else
        note "counters: /proc/$pid/maps shows no CoreCLR — not a .NET process, skipping"
    fi

    # ---- tier 2: --full (still no freeze) ----
    if [ "$full" = 1 ]; then
        if [ "$sudo_missing" = 1 ]; then
            note "note: pstacks/dstrings/gcstats ptrace-attach the target and"
            note "      /proc/sys/kernel/yama/ptrace_scope is $(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null)"
            note "      so they need root — the sudo lines are recorded in SUMMARY.md"
        fi
        if [ "$no_ipc" = 1 ]; then
            note "gcstats: skipped (no IPC socket — EventPipe-based)"
        elif need dotnet-gcstats "$T_GCSTATS"; then
            # gcstats logs one block per GC event and runs until interrupted.
            # Bound it with timeout; --kill-after is the backstop because a
            # stuck gcstats can ignore INT (seen live: timeout waited forever).
            # `|| true`: the nonzero exit after that kill is expected, and an
            # idle target legitimately logs nothing — neither is a run failure.
            priv "$out/p1-gcstats.txt" "dotnet-gcstats (60s)" "timeout -s INT --kill-after=10 65 $T_GCSTATS $pid || true"
            # `|| true` above deliberately masks the exit (the INT-kill path is
            # the normal ending), so a tool that never attached must be
            # detected by content, not rc: an attach failure prints an
            # exception and zero GC blocks. Heuristic, not exact — but the
            # alternative is the lying ok this printed before.
            if grep -qiE "unhandled exception|exception:|could not|couldn't|failed to|not authorized|error" "$out/p1-gcstats.txt" 2>/dev/null; then
                note "gcstats: tool failed to attach/collect — check p1-gcstats.txt (runtime/tool mismatch?)"
            else
            events=$(grep -c '^_______#' "$out/p1-gcstats.txt" 2>/dev/null || true)
            if [ "${events:-0}" -gt 0 ]; then
                note "gcstats: $events GC event(s) captured — see p1-gcstats.txt"
            elif [ "$(wc -l < "$out/p1-gcstats.txt")" -le 12 ]; then
                note "gcstats: no GC events in the 65s window (it logs per event, not per second — an idle target prints nothing)"
                note "         GC heap/collection telemetry is covered by counters (gc-heap-size, gen-2-gc-count)"
            else
                note "gcstats: no GC event blocks — check p1-gcstats.txt (tool/runtime mismatch?)"
            fi
            fi
        fi
        if need dotnet-pstacks "$T_PSTACKS"; then
            priv "$out/p1-pstacks.txt" "dotnet-pstacks" "$T_PSTACKS -p $pid > '$out/p1-pstacks.txt'"
        fi
        # gcdump uses EventPipe, so it works without root — but not without the socket.
        if [ "$no_ipc" = 1 ]; then
            note "gcdump: skipped (no IPC socket)"
        elif need dotnet-gcdump "$T_GCDUMP"; then
            record "$out/p1-gcdump-baseline.gcdump" "gcdump baseline" \
                "$T_GCDUMP collect -p $pid -o '$out/p1-gcdump-baseline.gcdump'"
            sleep 10
            record "$out/p1-gcdump-after.gcdump" "gcdump after 10s" \
                "$T_GCDUMP collect -p $pid -o '$out/p1-gcdump-after.gcdump'"
            # Same size-gate trap as trace above: a failed collect leaves
            # stderr text in a non-empty .gcdump. A real .gcdump is a
            # FastSerialization binary: length-prefixed '!FastSerialization.1'
            # at offset 4 (od-proofed on real dumps) — gate on the plain
            # token, coreutils only. No sigil: the prefix byte is '!', not '$'.
            gcdumps_valid=0
            if head -c 64 "$out/p1-gcdump-baseline.gcdump" 2>/dev/null | grep -qF 'FastSerialization' \
            && head -c 64 "$out/p1-gcdump-after.gcdump" 2>/dev/null | grep -qF 'FastSerialization'; then
                gcdumps_valid=1
            fi
            if [ "$gcdumps_valid" = 1 ]; then
                record "$out/p1-gcdump-report.txt" "gcdump report + diff" \
                    "$T_GCDUMP report '$out/p1-gcdump-baseline.gcdump' > '$out/p1-gcdump-report-base.txt'; $T_GCDUMP report '$out/p1-gcdump-after.gcdump' > '$out/p1-gcdump-report-after.txt'; diff '$out/p1-gcdump-report-base.txt' '$out/p1-gcdump-report-after.txt' || true"
                note "gcdump: baseline/after reports + diff in p1-gcdump-report*.txt (empty diff = flat heap over 10s)"
            else
                note "gcdump: no valid .gcdump pair collected — skipping report+diff"
            fi
        fi
        if need dotnet-dstrings "$T_DSTRINGS"; then
            priv "$out/p1-dstrings.txt" "dotnet-dstrings" "$T_DSTRINGS -p $pid > '$out/p1-dstrings.txt'"
            [ -f "$out/p1-dstrings.txt" ] && [ ! -s "$out/p1-dstrings.txt" ] && \
                note "dstrings empty — retry with '$T_DSTRINGS -p $pid -c 32 -s 10'"
        fi

        # EventPipe / diagnostics-IPC tools — no ptrace, so no root needed (socket still needed).
        if [ "$no_ipc" = 1 ]; then
            note "trace: skipped (no IPC socket)"
        elif need dotnet-trace "$T_TRACE"; then
            record "$out/p1-trace.nettrace" "dotnet-trace 30s" \
                "$T_TRACE collect -p $pid --duration 00:00:30 -o '$out/p1-trace.nettrace'"
            # A failed collect still leaves a non-empty file (record's log
            # header + the tool's stderr), which used to cascade into a
            # convert failure — gate on the .nettrace magic, not on size.
            if [ -s "$out/p1-trace.nettrace" ] && head -c 8 "$out/p1-trace.nettrace" 2>/dev/null | grep -q "Nettrace"; then
                record "$out/p1-trace.speedscope.log" "Speedscope flame graph" \
                    "$T_TRACE convert '$out/p1-trace.nettrace' --format Speedscope -o '$out/p1-trace'"
                [ -s "$out/p1-trace.speedscope.json" ] && \
                    note "trace: flame graph in p1-trace.speedscope.json (open in Speedscope, or PerfView on Windows)"
            else
                note "trace: no valid .nettrace collected — skipping Speedscope conversion"
            fi
        fi
        if [ "$no_ipc" = 1 ]; then
            note "stack: skipped (no IPC socket)"
        elif need dotnet-stack "$T_STACK"; then
            record "$out/p1-stack.txt" "dotnet-stack report" \
                "$T_STACK report -p $pid"
            [ -s "$out/p1-stack.txt" ] && \
                note "stack: no-dump managed stacks — use when a thread holds a lock in a native call (CloseHandle/ReadFile)"
            # symbolicate resolves tokens to line numbers; probe first so an
            # older dotnet-stack without the subcommand degrades to a note.
            if [ -s "$out/p1-stack.txt" ] && $T_STACK symbolicate --help >/dev/null 2>&1; then
                record "$out/p1-stack.sym.log" "dotnet-stack symbolicate" \
                    "$T_STACK symbolicate '$out/p1-stack.txt'"
                # Deployed builds ship no portable PDBs, so symbolicate can
                # exit 0 with a byte-identical copy — say so rather than
                # implying symbols were resolved.
                cmp -s "$out/p1-stack.txt" "$out/p1-stack.txt.symbolicated" && \
                    note "symbolicate: output identical to input (no portable PDBs next to the target) — expected for deployed builds"
            else
                note "stack: symbolicate not supported by the installed dotnet-stack — skipping"
            fi
        fi
        # dotnet-fullgc v1.2.0 crashes without -csn (IndexOutOfRange in its arg parsing).
        if [ "$no_ipc" = 1 ]; then
            note "fullgc: skipped (no IPC socket)"
        elif need dotnet-fullgc "$T_FULLGC"; then
            record "$out/p1-fullgc.txt" "dotnet-fullgc" \
                "$T_FULLGC $pid -csn 0"
            [ -s "$out/p1-fullgc.txt" ] && \
                note "fullgc: forced full GC — if the heap does not shrink, the retention is real (see dotnet-memory-analysis skill)"
        fi
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
            # fdatasync tracepoints are rejected by bpftrace on some kernels
            # even when the tracefs dirs exist (seen live on this lab box):
            # try the full probe, fall back to fsync-only in the same log.
            bpftrace_narrow="tracepoint:syscalls:sys_enter_fsync{ @s[tid]=nsecs; } tracepoint:syscalls:sys_exit_fsync /@s[tid]/{ @h[probe]=hist((nsecs-@s[tid])/1000); delete(@s[tid]); }"
            bpftrace_full="tracepoint:syscalls:sys_enter_fsync,sys_enter_fdatasync{ @s[tid]=nsecs; } tracepoint:syscalls:sys_exit_fsync,sys_exit_fdatasync /@s[tid]/{ @h[probe]=hist((nsecs-@s[tid])/1000); delete(@s[tid]); }"
            priv "$out/p1-fsync-bpftrace.txt" "bpftrace fsync latency hist (15s, system-wide)" \
                "timeout --preserve-status -s INT --kill-after=10 15 bpftrace -e '$bpftrace_full' || timeout --preserve-status -s INT --kill-after=10 15 bpftrace -e '$bpftrace_narrow'"
            # Histogram output lines start with @h[ — the echoed command above
            # also contains @h, so anchor the match.
            grep -q '^@h\[' "$out/p1-fsync-bpftrace.txt" || \
                note "bpftrace: no fsync/fdatasync calls in the 15s window"
            # This bcc's syncsnoop takes no -p and no duration: system-wide,
            # bounded by timeout. -s INT lets it print maps; --preserve-status
            # reports its clean exit instead of timeout's 124.
            priv "$out/p1-syncsnoop.txt" "syncsnoop 15s (system-wide: this bcc takes no -p)" \
                "timeout --preserve-status -s INT --kill-after=10 15 syncsnoop"
            grep -qE '^[0-9]+\.[0-9]+' "$out/p1-syncsnoop.txt" || \
                note "syncsnoop: no sync syscalls in the 15s window"
            # fsslower is the modern entry point (the <fs>slower names are
            # argv[0] aliases of it on this bcc); -d self-bounds, no timeout.
            fstype=$(stat -f -c %T "/proc/$pid/cwd" 2>/dev/null || echo "?")
            case "$fstype" in ext2|ext3|ext4|xfs|btrfs|fuse|nfs|f2fs|bcachefs|zfs) fsflag="-t $fstype";; *) fsflag="";; esac
            if have fsslower; then
                priv "$out/p1-fsslower.txt" "fsslower 15s ($fstype, pid $pid)" \
                    "fsslower $fsflag -p $pid -d 15"
                grep -qE '^[0-9]{2}:[0-9]{2}:[0-9]{2}' "$out/p1-fsslower.txt" || \
                    note "fsslower: no $fstype operations slower than 10ms in the window"
            else
                note "fsslower not found — skipping fs-slower trace"
            fi
            # -d self-bounds the capture; no timeout wrapper needed.
            priv "$out/p1-funclatency.txt" "funclatency vfs_fsync 15s" \
                "funclatency -p $pid -d 15 vfs_fsync"
            grep -q 'nsecs' "$out/p1-funclatency.txt" || \
                note "funclatency: no vfs_fsync calls in the window (idle target — run p3 for loaded runs)"
            # offcputime (bcc-libbpf-tools) aborts in ksyms__load when any
            # /proc/kallsyms name exceeds 255 chars: a known upstream stack
            # overflow (iovisor/bcc#5478, buffer 256 -> 2048), NOT a bad
            # install — the packaged 0.37.0-1 predates the fix, so reinstalling
            # cannot help. Detect the condition and use a bpftrace fallback
            # instead of a guaranteed SIGABRT.
            ksym_longest=$(wc -L < /proc/kallsyms 2>/dev/null || echo 0)
            if have offcputime && [ "${ksym_longest:-0}" -lt 275 ]; then
                priv "$out/p1-offcpu.txt" "offcputime 20s" \
                    "offcputime -p $pid 20"
            else
                if have offcputime; then
                    note "offcputime: skipped — /proc/kallsyms has a ${ksym_longest}-char line; bcc-libbpf-tools overflows a 256-byte buffer in ksyms__load (iovisor/bcc#5478) and SIGABRTs. Reinstalling will not help — it needs a package rebuilt with the upstream fix (or the bpftrace fallback below)."
                fi
                if have bpftrace; then
                    # bpftrace version of Brendan Gregg's offcputime.bt
                    # (Apache-2.0). sched_switch tracepoint, not
                    # kprobe:finish_task_switch: the kprobe needs struct
                    # task_struct field access that breaks across kernels
                    # (untraceable on this Arch 6.x box), while the
                    # tracepoint ABI is stable. Scoped to the target PID via
                    # $1: only the target's switch-out timestamps are stored,
                    # so kstack+ustack resolution (the fd-hungry part — one
                    # /proc/PID/root handle per sym lookup under sudo's
                    # 1024-fd limit) happens for our rows only instead of
                    # system-wide. Wakeup accounting still fires: it reads
                    # @start[next_pid], which is non-zero only for the
                    # recorded target.
                    cat >"$out/offcpu.bt" <<'BT'
tracepoint:sched:sched_switch
{
    if (args->prev_pid == $1) {
        @start[args->prev_pid] = nsecs;
    }
    $blocked = @start[args->next_pid];
    if ($blocked != 0) {
        @[kstack, ustack, args->next_comm] = sum(nsecs - $blocked);
        delete(@start[args->next_pid]);
    }
}
BT
                    priv "$out/p1-offcpu-bpftrace.txt" "bpftrace off-CPU stacks (20s)" \
                        "timeout --preserve-status -s INT --kill-after=5 20 bpftrace '$out/offcpu.bt' '$pid'"
                    grep -q 'TicTackSv' "$out/p1-offcpu-bpftrace.txt" || \
                        note "off-CPU fallback: no blocked TicTackSv stacks captured (idle target, or see the log for a bpftrace error)"
                else
                    note "off-CPU stacks unavailable: offcputime aborts on this kernel and bpftrace is missing (pacman -S bpftrace)"
                fi
            fi
            unset bpftrace_full bpftrace_narrow fstype fsflag
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
        if [ "$no_ipc" = 1 ]; then
            note "kuni: skipped (collect-linux needs the IPC socket)"
        elif { [ "$kmaj" -gt 6 ] || { [ "$kmaj" -eq 6 ] && [ "$kmin" -ge 4 ]; }; } && [ -d /sys/kernel/tracing ]; then
            if need dotnet-trace "$T_TRACE"; then
                priv "$out/p1-trace-kuni.nettrace" "dotnet-trace collect-linux 30s" \
                    "$T_TRACE collect-linux -p $pid --duration 00:00:30 -o '$out/p1-trace-kuni.nettrace'"
                [ -s "$out/p1-trace-kuni.nettrace" ] && \
                    note "kuni: unified trace — managed + kernel + native frames; open in PerfView (preview format)"
            fi
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
            if need dotnet-dump "$T_DUMP"; then
                record "$out/p1-dump-collect.txt" "dotnet-dump collect (FREEZES)" \
                    "$T_DUMP collect -p $pid -o '$dmp'"
                if [ -s "$dmp" ]; then
                    note "dump: $(du -m "$dmp" | cut -f1) MB collected"
                    record "$out/p1-dump-heap.txt" "SOS: eeheap + dumpheap" \
                        "$T_DUMP analyze '$dmp' -c 'eeheap -gc' -c 'dumpheap -stat' -c exit"
                    # Leak-triage chain: biggest live type -> 2 instances -> gcroot +
                    # objsize each, plus finalizequeue (undisposed backlog). Bounded:
                    # head -80 per analyze call so one chatty root cannot flood the log.
                    # Instance rows are matched as `$2 == MT`: a bare `^0000` address
                    # match catches SOS preamble (e.g. the syncblk zero row) on some
                    # runtimes, and Linux addresses carry no leading zeros anyway.
                    mt=$(awk 'NF>=4 && $1 ~ /^[0-9a-fA-F]+$/ && $NF != "Free" {if ($3+0>max){max=$3; mt=$1}} END{print mt}' "$out/p1-dump-heap.txt")
                    if [ -n "$mt" ]; then
                        record "$out/p1-dump-leak.txt" "SOS leak chain ($mt)" \
                            "for a in \$($T_DUMP analyze '$dmp' -c 'dumpheap -mt $mt' -c exit | awk -v mt=$mt 'tolower(\$2)==tolower(mt){print \$1}' | head -2); do $T_DUMP analyze '$dmp' -c \"gcroot \$a\" -c \"objsize \$a\" -c exit | head -80; done; $T_DUMP analyze '$dmp' -c finalizequeue -c exit"
                    else
                        note "dump: no MT parsed from p1-dump-heap.txt — skipping leak chain"
                    fi
                    if need dotnet-pstacks "$T_PSTACKS"; then
                        record "$out/p1-dump-pstacks.txt" "dotnet-pstacks <dump>" \
                            "$T_PSTACKS '$dmp'"
                    fi
                    if need dotnet-dstrings "$T_DSTRINGS"; then
                        record "$out/p1-dump-dstrings.txt" "dotnet-dstrings <dump>" \
                            "$T_DSTRINGS '$dmp'"
                        [ -s "$out/p1-dump-dstrings.txt" ] || \
                            note "dump dstrings empty — retry '$T_DSTRINGS '$dmp' -c 32 -s 10'"
                    fi
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
    fi
}

# ------------------------------------------------- p2: static + tests ------
cmd_p2() {
    { printf '\n## p2 static analysis\n'; } >>"$summary"
    hr "p2 static analysis"
    if have semgrep; then
        # -o writes pure JSON, so run it directly: record() would prepend a
        # header into the file and break JSON parsing.
        { printf '\n### semgrep r/csharp\n\n```\n$ %s\n```\n' \
            "semgrep --config r/csharp --metrics off --json -o p2-semgrep.json '$repo_root/src'"; } >>"$summary"
        local rc=0 count="?"
        semgrep --config r/csharp --metrics off --json -o "$out/p2-semgrep.json" "$repo_root/src" \
            >"$out/p2-semgrep.log" 2>&1 || rc=$?
        [ -f "$out/p2-semgrep.json" ] && \
            count=$(python3 -c "import json,sys;print(len(json.load(open(sys.argv[1])).get('results',[])))" \
                "$out/p2-semgrep.json" 2>/dev/null || count="?")
        note "semgrep: $count findings (r/csharp, exit $rc) -> ${out#"$repo_root"/}/p2-semgrep.json"
    else
        note "semgrep not found (pipx install semgrep) — skipping semgrep r/csharp"
    fi
    if have ast-grep; then
        record "$out/p2-ast-grep.txt" "ast-grep scan (rules/ bank)" \
            "cd '$repo_root' && ast-grep scan"
    else
        note "ast-grep not found (npm i -g @ast-grep/cli) — skipping banked rules scan (rules/)"
    fi

    { printf '\n## p2 unit tests\n'; } >>"$summary"
    hr "p2 unit tests (xUnit suite)"
    local f=""
    [ -n "$filter" ] && f="--filter '$filter'"
    record "$out/p2-test.log" "dotnet test" \
        "$DOTNET test tests/TicTack.Tests.csproj -c Release $f --logger 'trx;LogFileName=test-results.trx'"
    grep -E '^(Passed!|Failed!|error)' "$out/p2-test.log" | tail -3

    # --blame-hang writes a Sequence*.xml next to the results on a hang.
    local hang
    hang=$(find "$repo_root/tests/TestResults" -name 'Sequence*.xml' -newermt '-2 hours' 2>/dev/null | head -1)
    [ -n "$hang" ] && note "hang detected: $hang (CI uploads the same file as an artifact)"
}

cmd_test() {
    { printf '# TicTack test run — %s\n\n- filter: `%s`\n' "$(date -Is)" "${filter:-<none>}"; } >"$summary"
    hr "xUnit suite"
    local f=""
    [ -n "$filter" ] && f="--filter '$filter'"
    record "$out/p2-test.log" "dotnet test" \
        "$DOTNET test tests/TicTack.Tests.csproj -c Release $f --logger 'trx;LogFileName=test-results.trx'"
    grep -E '^(Passed!|Failed!|error)' "$out/p2-test.log" | tail -3
    local hang
    hang=$(find "$repo_root/tests/TestResults" -name 'Sequence*.xml' -newermt '-2 hours' 2>/dev/null | head -1)
    [ -n "$hang" ] && note "hang detected: $hang (CI uploads the same file as an artifact)"
}

# ------------------------------------------------- fixture (inlined) -------
# Folded in from benchmarks/create-fixture.sh (deleted): same tree
# (9000 small + 1000x1MB + 10x100MB), same names, same unsafe-root refusal.
perf_fixture() {
    local root="$1"
    if [[ "$root" == "/" || "$root" == "/home" || "$root" == "/tmp" ]]; then
        printf 'Refusing unsafe fixture root: %s\n' "$root" >&2
        return 1
    fi
    local src="$root/src" dst="$root/dst"
    rm -rf "$src" "$dst" "$root/state" "$root/archive"
    rm -f "$root/tictack.log"
    mkdir -p "$src/small" "$src/medium" "$src/large" "$dst"
    local i name
    for i in $(seq 1 9000); do
        printf -v name '%05d.txt' "$i"
        printf 'TicTack benchmark file %s\n' "$i" > "$src/small/$name"
    done
    for i in $(seq 1 1000); do
        printf -v name '%04d.bin' "$i"
        dd if=/dev/zero of="$src/medium/$name" bs=1M count=1 status=none
    done
    for i in $(seq 1 10); do
        printf -v name '%02d.bin' "$i"
        dd if=/dev/zero of="$src/large/$name" bs=1M count=100 status=none
    done
    printf 'Fixture ready: %s\n' "$src"
    printf 'Files: %s\n' "$(find "$src" -type f | wc -l)"
    printf 'Bytes: %s\n' "$(du -sb "$src" | cut -f1)"
}

# ----------------------------------------------------------------- p3 ------
perf_scenario() {
    local name="$1" workers="$2" verification="$3" warm="$4"
    local cfg="$out/p3-bench-$name.yaml"
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
    # warm-noop never wipes: removing dst+state makes every repeat cold again.
    if [ "$warm" != 1 ]; then
        rm -rf "$root/dst" "$root/state"
        # Honest first pass: drop the page cache so the timed run is not
        # riding the previous scenario's warmth. Needs root; otherwise the
        # run stays warm and says so.
        if [ "$(id -u)" -eq 0 ]; then
            sync; echo 3 > /proc/sys/vm/drop_caches 2>/dev/null \
                && note "cache: dropped page cache before $name" \
                || note "cache: drop failed — $name ran cache-warm, treat hash-verify comparison as approximate"
        elif sudo -n true 2>/dev/null; then
            sudo -n sh -c 'sync; echo 3 > /proc/sys/vm/drop_caches' \
                && note "cache: dropped page cache before $name (sudo)" \
                || note "cache: drop failed — $name ran cache-warm, treat hash-verify comparison as approximate"
        else
            note "cache: no root — $name ran cache-warm, treat hash-verify comparison as approximate"
        fi
    else
        record "$out/p3-run-$name-warmup.log" "warmup $name" \
            "$DOTNET '$dll' --once --config '$cfg'"
    fi

    local start end secs
    start=$(date +%s%N)
    record "$out/p3-run-$name.log" "timed $name" \
        "$DOTNET '$dll' --once --config '$cfg'"
    end=$(date +%s%N)
    secs=$(awk -v a="$start" -v b="$end" 'BEGIN{printf "%.3f", (b-a)/1e9}')

    local copied=""
    copied=$(grep -oE '[0-9]+ copied' "$out/p3-run-$name.log" | tail -1 | cut -d' ' -f1)
    if [ "$warm" = 1 ] && [ -n "$copied" ]; then
        note "warm-noop copied=$copied (expect 0)"
    fi

    local files bytes mbps fps load diffv
    files=$(find "$root/dst" -type f 2>/dev/null | wc -l | tr -d ' ')
    bytes=$(du -sb "$root/dst" 2>/dev/null | cut -f1)
    bytes=${bytes:-0}
    # Throughput is undefined when nothing was copied — dst-bytes/elapsed
    # produced the false warm-noop row. Report n/a instead.
    if [ "$copied" = "0" ]; then mbps="n/a"; fps="n/a"
    else
        mbps=$(awk -v b="$bytes" -v s="$secs" 'BEGIN{printf "%.1f", (s>0 ? b/1048576/s : 0)}')
        fps=$(awk -v f="$files" -v s="$secs" 'BEGIN{printf "%.1f", (s>0 ? f/s : 0)}')
    fi
    load=$(cut -d' ' -f1-3 /proc/loadavg)

    diff -r "$root/src" "$root/dst" >"$out/p3-diff-$name.txt" 2>&1
    if [ -s "$out/p3-diff-$name.txt" ]; then diffv="mismatch"; else diffv="match"; fi

    note "scenario $name: ${secs}s, $files files, $mbps MB/s, $fps files/s, load $load, diff $diffv"
    printf '| %s | %s | %s | %s | %s | %s | %s |\n' \
        "$name" "$secs" "$files" "$mbps" "$fps" "$load" "$diffv" >>"$summary"

    # hyperfine: statistically rigorous repeats. Additive — the single timed run
    # above stays the baseline-linux.json input; hyperfine's mean is the number to
    # quote. The JSON goes straight to its own file: record() appends human
    # output into its log file, which would corrupt JSON parsing.
    # warm-noop runs WITHOUT --prepare (state wipe = every repeat cold).
    if have hyperfine; then
        if [ "$warm" = 1 ]; then
            record "$out/p3-run-$name-hyperfine.log" "hyperfine $name (1 warmup + 3 runs, no prepare)" \
                "hyperfine --warmup 1 --runs 3 --export-json '$out/p3-run-$name-hyperfine.json' \"$DOTNET '$dll' --once --config '$cfg'\""
        else
            record "$out/p3-run-$name-hyperfine.log" "hyperfine $name (1 warmup + 3 runs)" \
                "hyperfine --warmup 1 --runs 3 --export-json '$out/p3-run-$name-hyperfine.json' --prepare \"rm -rf '$root/dst' '$root/state'\" \"$DOTNET '$dll' --once --config '$cfg'\""
        fi
        local hmean
        hmean=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['results'][0]['mean'])" \
            "$out/p3-run-$name-hyperfine.json" 2>/dev/null || echo "")
        [ -n "$hmean" ] && note "hyperfine $name: mean ${hmean}s over 3 runs"
    else
        note "hyperfine not found — skipping rigorous repeats (sudo pacman -S --needed hyperfine)"
    fi

    [ "$sep" = "," ] && printf ',\n' >>"$json"
    sep=","
    printf '  {"scenario": "%s", "seconds": %s, "files": %s, "bytes": %s, "copied": "%s", "mbps": "%s", "files_per_s": "%s", "load": "%s", "diff": "%s"}' \
        "$name" "$secs" "$files" "$bytes" "${copied:-?}" "$mbps" "$fps" "$load" "$diffv" >>"$json"

    if [ -f "$bench_root/baseline-linux.json" ] && command -v jq >/dev/null 2>&1; then
        local base
        base=$(jq -r --arg s "$name" '.[]? | select(.scenario == $s) | .seconds' \
            "$bench_root/baseline-linux.json" 2>/dev/null | head -1)
        if [ -n "$base" ] && [ "$base" != "null" ]; then
            awk -v n="$secs" -v b="$base" -v nm="$name" -v t="$REGRESSION_PCT" 'BEGIN{
                d = (b > 0 ? (n-b)/b*100 : 0);
                if (d > t) printf "REGRESSION: %s is %.0f%% slower than baseline (%.3fs -> %.3fs)\n", nm, d, b, n;
            }' | while read -r l; do note "$l"; done
        else
            note "no baseline entry for $name — comparison skipped"
        fi
    elif [ ! -f "$bench_root/baseline-linux.json" ]; then
        : # reported once at the end of cmd_p3
    fi
}

cmd_p3() {
    local dur="${DURABILITY:-full}"
    local dll="$repo_root/src/bin/Release/net10.0-windows/TicTackSv.dll"
    local json="$out/p3-results.json"
    local sep="" scenario w
    local binary_kind="build (JIT)"
    [ "$published" = 1 ] && binary_kind="publish (ReadyToRun, shipped artifact)"

    mkdir -p "$out"
    { printf '# TicTack perf run — %s\n\n- fixture: `%s`\n- durability: `%s`\n- binary: `%s`\n- scenarios: `%s`\n' \
        "$(date -Is)" "$root" "$dur" "$binary_kind" "${scenarios[*]}"; } >"$summary"
    hr "p3 perf @ $root (durability=$dur, binary=$binary_kind)"

    perf_fixture "$root" | tail -3 || { note "fixture error — aborting"; return 1; }

    # fio storage baseline: is the disk the bottleneck? 64 MB sequential
    # write with fsync per write on the fixture filesystem — the fsync mean
    # is the latency floor no copy pipeline can beat. Small on purpose;
    # scratch dir removed afterwards. JSON straight to its own file (see
    # the hyperfine note above about record() and JSON).
    if have fio; then
        mkdir -p "$root/fio"
        record "$out/p3-fio.log" "fio fsync-latency baseline (64MB)" \
            "fio --name=tictack --directory='$root/fio' --rw=write --size=64m --fsync=1 --output-format=json --output='$out/p3-fio.json'"
        local flat
        flat=$(python3 -c "import json,sys;j=json.load(open(sys.argv[1]))['jobs'][0]['sync']['lat_ns'];print('%.3f'% (j['mean']/1e6))" \
            "$out/p3-fio.json" 2>/dev/null || echo "")
        [ -n "$flat" ] && note "fio: mean fsync latency ${flat} ms on the fixture filesystem"
        rm -rf "$root/fio"
    else
        note "fio not found — skipping storage baseline (sudo pacman -S --needed fio)"
    fi

    if [ "$published" = 1 ]; then
        # Shipped artifact: dotnet publish with ReadyToRun (from the csproj),
        # same as service/linux/install-service.sh. Published into the artifact
        # dir so service/ stays untouched.
        record "$out/p3-build.log" "publish Release (R2R)" \
            "$DOTNET publish src/TicTack.csproj -c Release -o '$out/p3-publish' -v q --nologo"
        dll="$out/p3-publish/TicTackSv.dll"
    else
        record "$out/p3-build.log" "build Release" \
            "$DOTNET build src/TicTack.csproj -c Release -v q --nologo"
    fi
    if grep -qiE '(Build FAILED|error CS|error MSB)' "$out/p3-build.log"; then
        note "build error — see p3-build.log, aborting"
        return 1
    fi
    if { [ "$published" = 1 ] && [ ! -f "$dll" ]; }; then
        note "publish output missing: $dll — aborting"
        return 1
    fi

    # No upfront page-cache warming: each non-warm scenario drops the cache
    # itself right before its timed run, so a warmup here would only burn a
    # 2 GB read for no benefit.

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

    # Microbenchmarks (BenchmarkDotNet hot paths): filter matching, arg
    # construction, hex formatting, FileSnapshot equality, StateDb batch ops.
    # ShortRunJob is set in the project — minutes, not tens of minutes.
    record "$out/p3-bdn.log" "BenchmarkDotNet microbenchmarks" \
        "$DOTNET run -c Release --project '$bench_root/TicTack.Benchmarks.csproj' -- --filter '*'"

    # Verdicts: conclusions, not a raw dump.
    { printf '\n## verdicts\n'; } >>"$summary"
    if command -v jq >/dev/null 2>&1; then
        jq -r '.[] | "\(.scenario) \(.seconds) \(.copied)"' "$json" 2>/dev/null | while read -r s n c; do
            case "$s" in
                cold-initial) note "cold: ${n}s for the fixture payload" ;;
                warm-noop) [ "$c" = "0" ] && note "cache: state DB skips everything (warm-noop copied=0)" ;;
            esac
        done
        w1=$(jq -r '.[] | select(.scenario=="workers-1") | .seconds' "$json" 2>/dev/null)
        w2=$(jq -r '.[] | select(.scenario=="workers-2") | .seconds' "$json" 2>/dev/null)
        w4=$(jq -r '.[] | select(.scenario=="workers-4") | .seconds' "$json" 2>/dev/null)
        if [ -n "$w1" ] && [ -n "$w2" ]; then
            awk -v a="$w1" -v b="$w2" 'BEGIN{
                r = (b > 0 ? a/b : 0);
                if (r >= 1.5) printf "workers: 2 beats 1 by %.2fx — scales\n", r;
                else printf "workers: 2 beats 1 by only %.2fx — barely scales, likely I/O-bound\n", r;
            }' | while read -r l; do note "$l"; done
        fi
        if [ -n "$w2" ] && [ -n "$w4" ]; then
            awk -v a="$w2" -v b="$w4" 'BEGIN{
                r = (b > 0 ? a/b : 0);
                if (r < 1.1) printf "workers: 4 gains nothing over 2 (%.2fx) — I/O-bound, keep default 2\n", r;
                else printf "workers: 4 beats 2 by %.2fx\n", r;
            }' | while read -r l; do note "$l"; done
        fi
    else
        note "jq not found — verdict ratios skipped (install jq for the derived conclusions)"
    fi

    if [ ! -f "$bench_root/baseline-linux.json" ]; then
        note "no benchmarks/baseline-linux.json — adopt this quiet run: cp ${json#"$repo_root"/} benchmarks/baseline-linux.json"
    elif ! command -v jq >/dev/null 2>&1; then
        note "jq not found — baseline comparison skipped"
    fi

    hr "summary: ${summary#"$repo_root"/}"
    note "results: ${json#"$repo_root"/}"
}

# ----------------------------------------------------------------- main ------
mkdir -p "$out"
if [ "$testmode" = 1 ]; then
    cmd_test "$@"
else
    [ "$p1" = 1 ] && cmd_p1 "$@"
    [ "$p2" = 1 ] && cmd_p2 "$@"
    [ "$p3" = 1 ] && cmd_p3 "$@"
fi
hr "artifacts in ${out#"$repo_root"/}/"
printf '  summary: %s\n' "${summary#"$repo_root"/}"
