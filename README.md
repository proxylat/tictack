# TicTack — Real-Time File Sync + Automations

One-way file synchronisation service for Windows and Linux. Monitors source directories, copies changes to a destination, with optional versioning, deletion handling, validation, and crash recovery. Also runs scheduled tasks and external-drive jobs — any shell command.

---

## Design Principles

- **Never lose data** — fsync before rename ensures data is on disk before the final path is updated. Post-copy validation (size, hash, or both) verifies every file. Three layers of integrity: pre-read probe catches permissions early, temp+rename prevents partial overwrites, validation catches corruption.
- **Detect early, fail fast** — a 1-byte read from the source catches ~90% of lock/permission/access issues before the full copy starts. Delete-threshold guard blocks accidental mass deletions. Source-disappearance guard prevents syncing from an unmounted or empty directory.
- **Zero CPU idle** — `SemaphoreSlim` blocks the processing thread with no CPU usage when no events are queued. Default `watcher` monitor blocks inside OS file-change notifications. SQLite state DB uses incremental writes (no periodic full rewrites). Periodic wake-ups still exist: lock refresh (30s), deferred-deletion check (1h, only when armed), parity rescan (6h); `polling`/`composite` monitor modes add their scan interval on top.
- **Crash-proof by construction** — every write follows the temp-then-rename pattern: no partial file ever lands at the final path. Startup recovery (`PowerGuard.Cleanup()`) collects orphaned `.tictack.tmp` files. SQLite WAL journal survives power loss without corruption. Lock files have stale-detection and auto-release.
- **No secrets** — zero external network calls, no accounts, no cloud. EventLog entries stay on the machine.
- **Dependency-light** — four runtime dependencies (Microsoft.Data.Sqlite, YamlDotNet, System.ServiceProcess.ServiceController, System.Diagnostics.EventLog). No npm, pip, cargo, or gem trees.

---

## Features

| Feature | Implementation |
|---|---|
| Change monitoring | `FileWatcherMonitor` (Windows) / `FsWatchMonitor` (Linux), `UsnJournalMonitor` (Windows NTFS standalone, needs elevation, falls back to watcher), `PollingMonitor`, `CompositeMonitor` (+ optional `usn` member) — pluggable via `IFileMonitor` |
| Comparison (date/size/hash) | `IFileComparer` — `SizeComparer`, `DateSizeComparer`, `HashComparer`, `FullComparer` |
| Locked file handling | `FileAccessor` opens with `FileShare.ReadWrite|Delete` + `FILE_FLAG_BACKUP_SEMANTICS` (SYSTEM bypass); retry backoff |
| VSS support | `VssFileAccessor` (`sync.file_access: vss`, Windows-only, needs elevation) — direct open fast path, on-demand per-volume snapshot only on lock failure, no writer coordination |
| Retry logic | `ExponentialBackoffRetry` — configurable attempts, delay, backoff multiplier |
| Logging | `FileLogger` (rotating), `BufferedLogger` (async channel, flushes on shutdown), `ConsoleLogger` (color), `DesktopAlertLogger`, `EventLogLogger`, `MultiLogger` |
| Rename detection | `RenameAction` + `FileChangedEventArgs.OldFullPath` |
| Deletion handling | `MirrorDeletion`, `ArchiveDeletion` — factory-selected |
| Versioning | `TimestampVersioning` (max-versions limit), `NoVersioning` — factory-selected |
| Post-copy validation | `SizeValidator`, `HashValidator` — mirrors comparison level |
| Max file size | `SizeFilter` — `max_file_size_mb: <number>` or `no-limit` |
| Crash Recovery | `CopyAction` writes to `.tictack.tmp` then atomic rename; `PowerGuard.Cleanup()` recovers orphaned temps on startup |
| Desktop alerts | `DesktopAlert.Write()` creates `TicTack-{LEVEL}-{timestamp}.txt` in `alert_path` (global 30s cooldown shared across levels) |
| Zero-CPU idle | `SemaphoreSlim` + blocking OS notifications — no CPU when idle; periodic wake-ups only: lock refresh (30s), deferred check (1h, when armed), parity rescan (6h), USN poll sleep, polling scan intervals |
| Volume label paths | `[VolumeLabel]\path` syntax resolved to drive letters via `DriveInfo.GetDrives()` |
| Scheduled jobs | `TimerScheduler` — daily shell commands (`cmd.exe /c` Windows, `/bin/sh -c` Linux) with `{source}` substitution |
| External drive tasks | `DriveDiscoverer` — runs a shell command on each discovered external drive |
| State DB (skip-known) | `StateDb` — SQLite WAL, per-source, `size+mtime` cache to skip unchanged files on startup |
| Pre-read fail-fast | `CopyAction` probes 1 byte before full copy — catches permission/lock issues instantly |
| Deferred deletion | `DeferredDeletion` — holds blocked deletions for `delete_hold_days`, daily warnings with first 20 paths, recheck before sync |
| Delete-threshold guard | Blocks deletions when >50% of known files would be removed in one batch, or when count/size exceeds configured limits |
| Source-disappearance guard | Refuses to process Deleted events when source folder is missing |
| Exclusive lock | `.tictack.lock` — exclusive `FileMode.CreateNew` handle held open, identity write + flush, 5min stale timeout, 30s refresh, configurable retry timeout (`retry_lock_seconds`) |
| Sparse file support | Detects `FILE_ATTRIBUTE_SPARSE_FILE`, uses `FSCTL_SET_SPARSE` via `DeviceIoControl` |
| EventLog propagation | `EventLogLogger` writes errors to Windows Application log under `TicTackSv` source |

---

## Architecture

```
FileWatcherMonitor / UsnJournalMonitor (Windows) / FsWatchMonitor (Linux) + PollingMonitor (composite mode)
        │
        ▼
   Debounce Queue (per-file timer, configurable debounce_seconds)
        │
        ▼
   CompositeFilter (pattern + size, case-insensitive globs)
        │
        ▼
   IFileComparer (size | date+size | hash | full) — skip if unchanged
        │
        ▼
   IVersioningStrategy — archive previous version to .versions (optional)
        │
        ▼
   ExponentialBackoffRetry + CopyAction (.tictack.tmp → fsync → atomic rename)
        │
        ▼
   IValidator (size | hash) — post-copy integrity check
        │
        ▼
   IDeletionStrategy — handle source deletions (mirror | archive)
```

---

## Quick Start

Requires: .NET 10 SDK. Dependencies: **YamlDotNet 16.3.0**, **Microsoft.Data.Sqlite 10.0.12** (via NuGet; `dotnet restore` fetches automatically). **Meziantou.Analyzer** (compile-time Roslyn rules, `PrivateAssets=all` — never ships) and **BenchmarkDotNet** (`benchmarks/` project only) are dev-only.

### Windows

1. Copy `service\win\config_win.yaml.example` to `service\win\config.yaml` and edit your source/destination paths.
2. Run `service\win\install-service.bat` as Administrator — compiles (`dotnet publish`), registers the `TicTackSv` Windows service, and starts it.
   AOT alternative: `service\win\install-service-aot.bat` — same flow, publishes a self-contained NativeAOT exe (~8 MB, no .NET runtime needed; requires VS Build Tools with MSVC).
3. `sc stop TicTackSv` / `sc start TicTackSv` to restart after config changes.

Or run manually: `service\win\TicTackSv.exe --cli` (interactive) or `service\win\TicTackSv.exe --once` (single pass).

### Linux

1. Copy `service/linux/config_linux.yaml.example` to `service/linux/config.yaml` and edit your paths.
2. Run `cd service/linux && ./install-service.sh` — publishes, installs to `/opt/tictack`, and enables the `tictack` systemd service.
3. `sudo systemctl restart tictack` to restart after config changes.

### Tests

```
dotnet test tests\TicTack.Tests.csproj
# Windows native watcher/access tests (run on Windows)
dotnet test tests\TicTack.Tests.csproj --filter "Category=Windows"
```

Crash-recovery and lock-contention tests launch the real copy/lock code in a
separate worker process and terminate it at controlled durability checkpoints.
Physical power-cut tests require dedicated hardware or VM infrastructure.

### Diagnostics & performance

One phased workflow per OS wraps the `dotnet-*` diagnostic tools, static
analysis, the unit suite and the fixtured copy benchmark. Everything lives in
`benchmarks/`. Every run writes `diag-out/<timestamp>-perf/` (or `-test`) with
a `SUMMARY.md` recording each command and the file it produced.

```
benchmarks\diag.ps1 [-p1] [-p2] [-p3] [-Root DIR] [-Full] [-Dump] [PID|NAME] [-Published] [SCENARIOS...]  # Windows
benchmarks\diag.ps1 test [-Filter EXPR] [-WindowsOnly] [-Coverage] [-Elevated] [-NoBuild]

benchmarks/diag.sh [-p1] [-p2] [-p3] [--full] [--dump] [--io] [--kuni] [--published] [PID|NAME] [SCENARIOS...]  # Linux
benchmarks/diag.sh test [--filter EXPR]
```

No `-p` flag runs all three phases; `-p1 -p2` runs the first two, `-p1` triage
only. `PID|NAME` is automated (omitted = running `TicTackSv`); `-Root` defaults
to `C:\bench` on Windows. Missing tools degrade to a skip-note with the
install one-liner — a global install works, otherwise the script falls back to
`dnx` one-shot runs (ships with the .NET 10 SDK, same as CI).

`p1` escalates cheap to expensive: process identity, thread states and a
CPU-delta read (stuck vs. slow), then `dotnet-counters` (System.Runtime plus
the in-app `TicTack` provider: files/bytes copied, copy+fsync latency,
pending events). `-Full` adds `dotnet-gcstats`, `dotnet-pstacks`, two
`dotnet-gcdump` samples (auto-diffed), `dotnet-dstrings`, `dotnet-trace`
(plus a Speedscope flame-graph conversion of the `.nettrace`),
`dotnet-stack` (+ `symbolicate` when supported), `dotnet-fullgc`, a bounded
PerfView GC capture when `C:\tools\PerfView.exe` exists, and the
Windows event log. On Linux the thread snapshot also includes `pidstat`
per-thread I/O, `-p1 --io` adds bounded BPF/fs tracing (bpftrace fsync
hist, syncsnoop, fs-specific `*slower`, funclatency, offcputime — needs root plus
`bcc-tools`/`bpftrace` installed), and `-p1 --kuni` captures a unified
managed+kernel+native trace (`dotnet-trace collect-linux`; needs root,
kernel ≥ 6.4, tracefs). `p2` runs `semgrep --config r/csharp` (`ast-grep scan` over the banked
`rules/`) — mirrored by the `static` CI job — then the xUnit suite.
`-Dump` on Windows falls back
to `procdump -ma` when `dotnet-dump` is not installed. `-Dump` is the only step that
freezes the target and it asks first — it writes the dump to `/tmp`, analyzes
it offline (`dotnet-pstacks` / `dotnet-dstrings` / `dotnet-dump analyze` plus
an automated leak-triage chain, so
no root is needed even when ptrace is blocked), then deletes it. Threads stuck
in `D`-state are a kernel/filesystem problem, so the script reports that
instead of escalating.

`p3` regenerates the fixture inline (9000 small + 1000×1MB + 10×100MB),
builds Release (or `dotnet publish` with `-Published` — the ReadyToRun shipped
artifact, same flags as the install scripts; quote the `-Published` numbers),
takes a storage fsync-latency
baseline of the fixture filesystem (`fio` on Linux, `diskspd` on Windows,
when installed), then runs named
scenarios — `cold-initial`, `warm-noop`, `hash-verify` and `workers=1|2|4` —
each sampled for native per-process stats (CPU-cycle delta, CPU %, I/O totals
+ amplification, context switches — `TicTackSv` only, no WMI).
Every non-warm scenario drops the page cache right before its timed pass
(`RAMMap -Et` on Windows, `drop_caches` on Linux — both need admin/root;
without them the run stays warm and SUMMARY says so), so scenario order no
longer confounds the numbers; `warm-noop` intentionally stays warm.
Each scenario writes
one row (seconds, MB/s, files/s, cpu%, diff
verdict) plus a machine-readable `results.json`, with `hyperfine` means when
`hyperfine` is installed, then the `BenchmarkDotNet` microbenchmarks
(`benchmarks/TicTack.Benchmarks.csproj`: filter matching, arg construction,
hex formatting, `FileSnapshot` equality, StateDb batch ops), and a derived
verdicts block (cache speedup, worker scaling, CPU- vs I/O-bound call).
It diffs those numbers against
`benchmarks/baseline-windows.json` / `baseline-linux.json` (per-OS, git-ignored;
adopt a quiet run with `cp diag-out/<ts>/p3-results.json benchmarks/baseline-<os>.json`)
and flags anything >20 % slower. Pass scenario names
to run a subset. Override with the `DURABILITY` (`rename-only`) environment
variable.

Record the load average with every number. A saturated host swamps the
pipeline: on a 4-core box at 41 % iowait and 7 blocked processes the same
2 GB fixture took 356 s, while a `cp -a` of it took 46 s. `durability: full`
costs only ~1.4× more than `rename-only` — it is not the dominant term. The
`perf` subcommand hands the sync off to a detached child, so give the whole
run its own wall-clock budget rather than the default agent/CI step timeout.

Install the tools once with `dotnet tool install -g dotnet-counters dotnet-gcdump
dotnet-dump dotnet-trace dotnet-stack dotnet-pstacks dotnet-dstrings dotnet-gcstats
dotnet-fullgc` — or skip the install and run them one-shot with `dnx`
(`dnx dotnet-counters …`, ships with the .NET 10 SDK). Tool selection by symptom and the PerfView GC recipes live in
[`docs/linux-debug-perf.md`](docs/linux-debug-perf.md),
[`docs/windows-debug-perf.md`](docs/windows-debug-perf.md) and
[`docs/windows-perf.md`](docs/windows-perf.md).

---

## CLI Flags

| Flag | Description |
|---|---|
| `--cli` | Interactive console mode — live monitoring with per-event logging |
| `--once` | Single sync pass over all sources, then exit |
| `--validate` | Load and validate config, exit with code 0/1 |
| `--rebuild` | Clear state DBs and re-sync everything from scratch (archives stale dest files). Stop the service first — it locks the state DBs |
| `--external-drives` | Auto-discover external drives with marker file, run command on each (Restic, Rclone, rsync, etc.) |
| `--service` | Run as Windows Service (auto-detected when non-interactive; Windows-only) |
| `--config <path>` | Path to config file (default: `config.yaml` in exe dir) |

---

## Configuration

All paths support `[VolumeLabel]` syntax on Windows (e.g., `[Backup-Disk]\Sync`) — resolved to the actual drive letter at startup, before any log file is opened. If a labeled volume isn't present yet (slow USB), startup waits up to `volume_wait_minutes` (default `2`) for it to appear, then exits with an error naming the missing label instead of syncing against a wrong or empty path. `0` disables the wait (single resolution pass, then fail fast).
Duplicate YAML keys are rejected at startup; unknown properties are ignored.

**Do not use environment variables like `%USERPROFILE%` or `%HOME%`.** The Windows service runs as `LocalSystem`, so `%USERPROFILE%` resolves to `C:\WINDOWS\system32\config\systemprofile`, not your profile — same for `%HOME%` under systemd (root). Always write the full path (`C:\Users\User\Desktop`).

### sources

Fields live at three levels: top-level source keys (`paths`, `destination`, `state_db_path`, `debounce_seconds`); the `filter:` sub-block (`max_file_size_mb`, `exclude`); everything else (`drain_strategy` through `file_access`, plus `verification` down) lives under the nested `sync:` key (`retry:`, `versioning:`, `deletion:` are sub-blocks of it) — `sync:` fields placed at source level are silently ignored.

| Field | Default | Description |
|---|---|---|
| `paths` | — | List of source folders to monitor. Each entry becomes its own source at `destination + foldername` |
| `destination` | — | Drive+folder to sync into, supports `[VolumeLabel]` |
| `state_db_path` | `C:\ProgramData\TicTack` / `/var/lib/tictack` | **Directory** for per-source state DBs (`<folder>.db`, SQLite WAL) used to skip unchanged files on startup |
| `debounce_seconds` | `10` | Wait time (s) after last change before triggering sync |
| `drain_strategy` | `scan` | Backlog drain order: `scan` = hash-order scan (zero extra memory) / `ready_queue` = earliest-expiry-first heap (bounded drain under watcher-overflow backlogs, transient ~2x backlog memory) |
| `dir_sync` | `per-file` | Directory-entry durability after each rename: `per-file` = fsync each file's parent dir / `per-batch` = collect dirs and fsync once per state checkpoint (fewer syncs on initial sync, larger crash window: files already renamed but not yet dir-synced may need a re-copy). Measured: neutral under `full`, but 3.5x faster cold initial sync when combined with `rename-only` (24.7s → 7.0s, 10k files / 2GB) |
| `complete_mode` | `inline` | Copy pipeline shape: `inline` = copy → fsync → rename on the worker thread / `pipelined` = workers copy to temp and one completer thread owns flush → rename → validate → upsert in order (bounded queue of 2x workers for backpressure; crash-safe: state is still claimed only after durable rename, orphan tmps go to PowerGuard) |
| `file_access` | `direct` | File read path: `direct` = open source files directly / `vss` = read locked files via on-demand VSS snapshots (Windows-only, needs elevation; falls back to `direct` with a warning otherwise) — snapshot created per volume only on lock failure, cached 10 min, no writer coordination (file-level reads, not app-consistent quiesce) |
| `max_file_size_mb` | `no-limit` | Skip files larger than this (MB). `no-limit` = all files |
| `exclude` | `[]` | Case-insensitive glob patterns to skip (`*.iso`, `*.tmp`, `temp/*`) |
| `verification` | `date_and_size` | Pre-copy compare + post-copy check: `size` / `date_and_size` / `hash` / `full` |
| `durability` | `full` | `full` fsyncs each temporary file before rename; `fdatasync` (Linux) flushes file data but not metadata-only changes; `rename-only` skips per-file disk flush and fsyncs the destination directory instead, trading a power-loss window for speed |
| `initial_sync_workers` | `2` | Bounded parallel workers for initial sync only; copying remains complete before parity/deletion cleanup |
| `retry.max_attempts` | `5` | Max retries on failed copy |
| `retry.delay_ms` | `1000` | Initial retry delay (ms) |
| `retry.backoff` | `2.0` | Delay multiplier per retry (1s → 2s → 4s) |
| `lock_handling` | `retry` | `retry` = wait and retry when lock is held / `ignore` = proceed without lock |
| `retry_lock_seconds` | `600` | Seconds to retry when lock is held before giving up, with progressive backoff (2s→5s→15s→30s→60s cap, only when `lock_handling: retry`) |
| `delete_threshold_count` | `1000` | Block deletion if one burst contains >= N files |
| `delete_threshold_size_gb` | `50` | Block deletion if one burst total size >= N GB |
| `delete_threshold_percent` | `50` | Block deletion if one burst >= N% of known files |
| `delete_hold_days` | `7` | Days to hold blocked deletions before syncing; daily warnings sent |
| `versioning.max_versions` | `10` | Keep up to N old versions per file |
| `versioning.path` | — | Where archived versions go (timestamp suffix) |
| `deletion.mode` | `archive` | On source deletion: `mirror` = delete dest too / `archive` = move to .archive |
| `deletion.path` | — | Target dir in `archive` mode |

**Path mirroring:** archived and versioned files keep their real folder structure. With a shared `.archive` / `.versions` next to the sync root, deleting `Desktop\foo.txt` lands in `.archive\Desktop\foo_ts.txt` — not in the archive root.

**Durability warning:** `rename-only` preserves atomic temp+rename behavior but does not force each file's data to stable storage before the rename. `fdatasync` (Linux only) flushes file contents but may skip metadata updates, so a freshly extended file can lose its size fix-up on power loss; on Windows it falls back to `full`. Use the default `full` setting when power-loss durability matters more than initial-sync speed.

**Delete-threshold guard:** if one deletion burst exceeds `delete_threshold_count`, `delete_threshold_size_gb`, or `delete_threshold_percent` of known files, it is deferred to `tictack-deferred-<folder>.json` (next to the log file). After `delete_hold_days`, remaining files are synced; warnings are logged daily with the first 20 paths + full list location.

### monitor

| Field | Default | Description |
|---|---|---|
| `type` | `watcher` | `watcher` = instant OS events, zero CPU idle (`ReadDirectoryChangesW` P/Invoke on Windows, `FileSystemWatcher` on Linux). `usn` = NTFS USN-journal cursor (Windows-only, needs elevation; falls back to `watcher` with a warning otherwise) — miss-proof while running, no buffer overruns. `polling` = periodic dir scan (no missed events). `composite` = watcher for speed + polling as safety net (+ optional `usn` journal member, next row) |
| `usn` | `false` | Composite-only: also tap the NTFS journal as a third member (defense in depth — two independent observers must both miss an event to lose it). Windows + elevation required; probe-skips with a warning otherwise. Ignored with a warning for non-composite types |
| `watcher_buffer_kb` | `64` | Watcher buffer in KB (NTFS on Windows, inotify on Linux). **Larger = survives bursts (git clone, npm install, unzip) without event loss.** Use 512+ for heavy churn |
| `polling_interval_seconds` | `3600` | Full directory scan interval (s) for polling fallback. Min 10 |
| `restart_delay_seconds` | `10` | Wait before restarting watcher after error |
| `usn_poll_interval_ms` | `200` | Idle sleep (ms) between USN journal reads when no records arrive. Larger = quieter idle, slower pickup. Min 50 |
| `usn_parent_prefilter` | `true` | USN only: skip journal records whose parent dir is outside the watched tree before any path resolve (kills the volume-wide resolve tax — Spotify/Temp churn costs zero syscalls). UnderWatch stays the authority, so disabling only costs syscalls, never events |

### logging

| Field | Default | Description |
|---|---|---|
| `level` | `info` | `debug` / `info` / `warn` / `error` / `silent` |
| `path` | `tictack.log` | Log file path, dir created automatically |
| `max_size_mb` | `10` | Rotate log after N MB |
| `max_files` | `5` | Keep N rotated logs (`tictack.log`, `.1`, `.2`...) |
| `console` | `false` | Also write to stdout (auto-enabled in `--cli`/`--once`) |
| `alert_path` | unset (alerts off) | Where `TicTack-WARN/ERROR-*.txt` alert files are written — no code default, so leave it set (the examples use the Desktop). On Windows, Desktop is `C:\Users\User\Desktop`; on Linux, `~/Desktop` |

### watchdog

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Per-source stall check: a faulted processor task or a non-empty queue with no progress for a full interval writes `[WATCHDOG]` Error to the log, the `alert_path` dir, and the Windows EventLog. Heartbeat itself logs at `debug` only, so `info` stays quiet |
| `interval_minutes` | `30` | Minutes between checks (also the no-progress stall threshold) |

### external_drives

Run any command on each discovered external drive (Restic, Rclone, rsync, etc.). Discovers drives via a marker file and executes the configured command, passing `{source}` and `{drive}` placeholders. Drives containing sync destinations are automatically excluded.

| Field | Default | Description |
|---|---|---|
| `command` | `restic.exe backup ...` | Shell command with `{source}` (all source paths) and `{drive}` (each discovered drive). Works with any tool — Restic, Rclone, rsync, etc. Default uses `restic.exe`; override for Linux (`restic backup ...`) |
| `working_dir` | exe dir | Working directory for the command |
| `require_marker_file` | `true` | Only run on drives with a marker file (safety gate) |
| `marker_file_name` | `.tictack-target` | Marker file name to look for at drive root |
| `exclude_drives` | `[]` | Drive letters to always skip (system drive + sync destination drives excluded automatically) |

### jobs

Scheduled commands run once per day (`cmd.exe /c` Windows, `/bin/sh -c` Linux). If the machine was off at the scheduled time, the job runs on next startup (catch-up within 30 seconds).

| Field | Default | Description |
|---|---|---|
| `name` | — | Job label for logs **(required)** |
| `time` | — | Daily trigger time, `HH:mm` 24h format **(required)** |
| `command` | — | Shell command (`cmd.exe /c` on Windows, `/bin/sh -c` on Linux). `{source}` = all source paths quoted **(required)** |
| `working_dir` | exe dir | Working directory for the command |

---

## Service Management

### Linux (systemd)

| Command | Action |
|---|---|
| `sudo systemctl status tictack` | Show status |
| `sudo systemctl restart tictack` | Restart after config changes (`/opt/tictack/config.yaml`) |
| `journalctl -u tictack -f` | Follow logs |
| `./uninstall-service.sh` | Stop + remove (keeps `/opt/tictack/config.yaml`) |

Unit name: `tictack`, runs `TicTackSv --cli` under systemd with SIGINT shutdown.

Notes:
- Config paths use Linux separators (`~/Pictures`); `[VolumeLabel]` syntax is Windows-only.
- The unit sends SIGINT on stop for a graceful shutdown; deploy dir is `/opt/tictack`.
- `install-service.sh` auto-detects `dotnet`: with a system runtime it publishes framework-dependent, without one it publishes self-contained (~80 MB, no host runtime required).
- Jobs and commands run via `/bin/sh -c`; use Linux syntax and paths in `jobs:` / `external_drives.command`.
