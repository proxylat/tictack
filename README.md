# TicTack — Real-Time File Sync + Automations

One-way file synchronisation service for Windows and Linux. Monitors source directories, copies changes to a destination, with optional versioning, deletion handling, validation, and crash recovery. Also runs scheduled tasks and external-drive jobs — any shell command.

---

## Design Principles

- **Never lose data** — fsync before rename ensures data is on disk before the final path is updated. Post-copy validation (size, hash, or both) verifies every file. Three layers of integrity: pre-read probe catches permissions early, temp+rename prevents partial overwrites, validation catches corruption.
- **Detect early, fail fast** — a 1-byte probe of the source surfaces most lock/permission/access issues before the full copy starts (the probe re-opens the file, so a file locked between probe and copy still retries through the normal path). Delete-threshold guard blocks accidental mass deletions. Source-disappearance guard prevents syncing from an unmounted or empty directory.
- **Zero CPU idle** — `SemaphoreSlim` blocks the processing thread with no CPU usage when no events are queued. Default `watcher` monitor blocks inside OS file-change notifications. SQLite state DB uses incremental writes (no periodic full rewrites). Periodic wake-ups still exist: lock refresh (30s), deferred-deletion deadline timer (one-shot, only when holds exist), parity rescan (6h); `polling`/`composite` monitor modes add their scan interval on top.
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
| Rename detection | Best-effort pairing via `FileChangedEventArgs.OldFullPath`, plus file-ID move replay: a renamed file whose identity is already in the state DB moves on the destination instead of re-copying |
| Deletion handling | `MirrorDeletion`, `ArchiveDeletion` (default; archives under `destination/.archive`, overwriting the same mirrored path — no timestamp) — factory-selected; an unknown mode falls back to archive |
| Versioning | `TimestampVersioning` (timestamp+guid suffix, max-versions limit), `NoVersioning` — factory-selected; versioning is off unless `versioning.path` is set |
| Post-copy validation | `SizeValidator`, `HashValidator` — mirrors the comparison level, except `full` compares date+size+hash but validates hash-only |
| Max file size | `SizeFilter` — `max_file_size_mb: <number>`, `no-limit` (default) or `none` (skip everything) |
| Crash Recovery | `CopyAction` writes to `.tictack.tmp` then atomic rename; `PowerGuard.Cleanup()` recovers orphaned temps on startup |
| Desktop alerts | `DesktopAlert.Write()` creates `TicTack-{LEVEL}-{timestamp}.txt` in `alert_path` (1s filename granularity; global 30s cooldown shared across levels, armed before the path check, so a missing `alert_path` still starts the cooldown) |
| Zero-CPU idle | `SemaphoreSlim` + blocking OS notifications — no CPU when idle; periodic wake-ups only: lock refresh (30s), deferred deadline timer (one-shot, when holds exist), parity rescan (6h), USN poll sleep, polling scan intervals |
| Volume label paths | `[VolumeLabel]\path` syntax resolved to drive letters via `DriveInfo.GetDrives()` |
| Scheduled jobs | `TimerScheduler` — daily/weekly/monthly/one-shot shell commands (`cmd.exe /c` Windows, `/bin/sh -c` Linux) with `{source}` substitution |
| External drive tasks | `DriveDiscoverer` — runs a shell command on each discovered external drive |
| State DB (skip-known) | `StateDb` — SQLite WAL, per-source (`<folder>-<hash>.db`, case-preserved leaf + path hash), `size+mtime` cache plus file-ID/ctime/hash identity columns to skip unchanged files on startup |
| Pre-read fail-fast | `CopyAction` probes 1 byte before full copy — catches permission/lock issues instantly |
| Deferred deletion | `DeferredDeletion` — SQLite hold store (`tictack-deferred-<lowercase-folder>-<hash>.db`, next to the log file), one row per batch clock + one indexed row per path; one-shot deadline timer (no polling), watcher-driven cancel on restore, daily warnings (counts + days left) from metadata only — the 20-path sample is a `debug`-only one-time log at record and expiry. Deleting the `.db` cancels every hold (keeps destination files). Empty store deletes its own files — no file means no pending work |
| Delete-threshold guard | Blocks a deletion burst at `>=` count, `>=` size GB, or `>=` N% of known files (percent needs 50+ known files; unknown baseline fails closed; suspect baselines with dead rows are flagged, not trusted). A localized burst (one folder owns the bulk, off by default) proceeds as folder cleanup instead of holding |
| Source-disappearance guard | Refuses to process Deleted events when source folder is missing |
| Exclusive lock | `.tictack.lock` — exclusive `FileMode.CreateNew` handle held open, identity write + flush, 5min stale timeout, 30s refresh, configurable wait budget (`lock_wait_seconds`) |
| Sparse file support | Detects `FILE_ATTRIBUTE_SPARSE_FILE`, uses `FSCTL_SET_SPARSE` via `DeviceIoControl` (Windows-only branch) |
| Spike guard | Sliding-window brake on copy/delete/rename churn: trips on bytes, file count, or rename count inside the window, writes a `.tictack-churn-hold` marker that pauses destructive work until you delete it (initial sync exempt) |
| Routine checks | `full_routine_check_days` (default 14; `0` disables): when due, unchanged files are byte-compared instead of skipped. `routine_check_days` (default 0, off) + `routine_check_files` (default 10): when due, a random sample of destination files is re-hashed and compared; mismatches log an error and re-alert daily until the next sync repairs them |
| Bulk cleanup shortcut | `bulk_cleanup_percent` (default 0, off): a deletion burst localized to one folder proceeds immediately as folder cleanup instead of holding |
| Destination self-defense | `destination_protect` (default off): on startup, denies write access to the destination for normal users (Windows) / strips group+other write bits (Linux) |

---

## Architecture

```
FileWatcherMonitor / UsnJournalMonitor (Windows) / FsWatchMonitor (Linux) + PollingMonitor (composite mode)
        │
        ▼
   Debounce Queue (per-file timer, configurable debounce_seconds)
        │
        ▼
   CompositeFilter (pattern + size, globs case-insensitive on Windows)
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

Rename pairing is best-effort (a missed rename just copies fresh); versioning
archives the previous destination file only when `versioning.path` is set;
`full` verification compares date+size+hash but validates hash-only.

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
to `C:\bench` on Windows and `$FIXTURE_ROOT` (or `/home/user/sync-bench`) on Linux. Missing tools degrade to a skip-note with the
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
kernel ≥ 6.4, tracefs). `p2` runs `semgrep --config r/csharp` followed by `ast-grep scan` over the banked
`rules/` — mirrored by the `static` CI job — then the xUnit suite.
`-Dump` on Windows falls back
to `procdump -ma` when `dotnet-dump` is not installed. `-Dump` is the only step that
freezes the target and it asks first — it writes the dump to `/tmp` on Linux
(`%TEMP%` on Windows), analyzes
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
unknown names are skipped —
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
adopt a quiet run with `cp diag-out/<ts>/p3-results.json benchmarks/baseline-<os>.json`,
needs `jq` installed)
and flags anything >20 % slower. The `hyperfine` means cover repeated runs;
without `hyperfine` each figure is a single timed pass. Pass scenario names
to run a subset. Override with the `DURABILITY` environment
variable (`rename-only` for the fast mode — the value is passed through
unvalidated, so any other string just runs the default `full` mode).

Record the load average with every number. A saturated host swamps the
pipeline: on a 4-core box at 41 % iowait and 7 blocked processes the same
2 GB fixture took 356 s, while a `cp -a` of it took 46 s. `durability: full`
costs only ~1.4× more than `rename-only` — it is not the dominant term. The
p3 phase hands the sync off to a detached child, so give the whole
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
| `--once` | Single sync pass over all sources, then exit (always exits 0, even on per-file failures; scheduled jobs never run, parity cleanup does) |
| `--validate` | Load and validate config, exit with code 0/1 |
| `--rebuild` | Clear state DBs and re-sync everything from scratch (archives stale dest files under `archive` mode, deletes them under `mirror`). Stop the service first — it locks the state DBs |
| `--deferred-list` | Table of pending deletion holds: source stem, path, batches, files, oldest, next due |
| `--deferred-approve <source>` | Drain expired holds now through the normal delete path (unexpired batches report "Nothing to approve" and wait). Stop the service first |
| `--deferred-check <source>` | Run warnings + expiry evaluation once, without draining (still stamps the daily-warning clock, so it is not read-only) |
| `--reprove <source>` | Cancel the hold, keep every destination file (same as deleting the hold `.db`; `--deferred-cancel` alias; the store file is deleted afterwards). Stop the service first |
| `--external-drives` | Auto-discover external drives with marker file, ask `Proceed? (y/n)` per drive, run command on each (Restic, Rclone, rsync, etc.; commands are killed after 10 minutes, no knob) |
| `--service` | Run as Windows Service (auto-detected when non-interactive; Windows-only) |
| `--config <path>` | Path to config file (default: `config.yaml` in exe dir) |

---

## Configuration

All paths support `[VolumeLabel]` syntax on Windows (e.g., `[Backup-Disk]\Sync`) — resolved to the actual drive letter at startup, before any log file is opened. Labels inside `jobs:` and `external_drives:` commands are rewritten too. If a labeled volume isn't present yet (slow USB), startup waits up to `volume_wait_minutes` (default `2`) for it to appear, then exits with an error naming the missing label instead of syncing against a wrong or empty path. `0` disables the wait (single resolution pass, then fail fast).
Duplicate YAML keys are rejected at startup, and so is any unknown key — a misspelled field or a `sync:` field placed at source level fails the load, it is never ignored.

**Do not use environment variables like `%USERPROFILE%` or `%HOME%`, or `~`.** Nothing expands them — the literal string becomes the path. The Windows service runs as `LocalSystem` and systemd units run as root, so even an expanded value would point at the wrong profile. Always write the full path (`C:\Users\User\Desktop`, `/home/user/Pictures`).

### sources

Fields live at three levels: top-level source keys (`path`/`paths`, `destination`, `state_db_path`, `debounce_seconds`); the `filter:` sub-block (`max_file_size_mb`, `exclude`); everything else (`drain_strategy` through `file_access`, plus `verification` down) lives under the nested `sync:` key (`retry:`, `versioning:`, `deletion:` are sub-blocks of it) — a `sync:` field placed at source level is an unknown key and fails the load. Each `paths` entry becomes its own source at `destination + foldername`, sharing the same filter/sync blocks; two sources may not share one destination. A lone `path:` (singular) is also accepted; setting both keeps `paths` and fails validation.

| Field | Default | Description |
|---|---|---|
| `paths` | — | List of source folders to monitor. Each entry becomes its own source at `destination + foldername` |
| `destination` | — | Drive+folder to sync into, supports `[VolumeLabel]` |
| `state_db_path` | `C:\ProgramData\TicTack` / `/var/lib/tictack` | **Directory** for per-source state DBs (`<folder>-<hash>.db`, SQLite WAL — hash suffix keeps same-leaf sources apart) used to skip unchanged files on startup |
| `debounce_seconds` | `10` | Wait time (s) after last change before triggering sync |
| `drain_strategy` | `scan` | Backlog drain order: `scan` = unordered scan, zero extra memory / `ready_queue` = earliest-expiry-first heap for watcher-overflow backlogs (grows with queued events, duplicates included, roughly 2x backlog memory transiently) |
| `dir_sync` | `per-file` | Directory-entry durability after each rename: `per-file` = fsync each file's parent dir / `per-batch` = collect dirs and fsync once per state checkpoint — bulk scans only; trickle copies still sync per file. Fewer syncs on initial sync, larger crash window: files already renamed but not yet dir-synced may need a re-copy. Measured: neutral under `full`, but 3.5x faster cold initial sync when combined with `rename-only` (24.7s → 7.0s, 10k files / 2GB) |
| `complete_mode` | `inline` | Copy pipeline shape: `inline` = copy → fsync → rename on the worker thread / `pipelined` = workers copy to temp and one completer thread owns flush → rename → validate → upsert in order (bounded queue of 2x workers for backpressure; crash-safe: state is still claimed only after durable rename, orphan tmps go to PowerGuard) |
| `file_access` | `direct` | File read path: `direct` = open source files directly / `vss` = read locked files via on-demand VSS snapshots (Windows-only, needs elevation; falls back to `direct` with a warning otherwise) — snapshot created per volume only on lock failure, cached 10 min, no writer coordination (file-level reads, not app-consistent quiesce) |
| `max_file_size_mb` | `no-limit` | Skip files larger than this (MB). `no-limit` = all files, `none` = skip everything |
| `exclude` | `[]` | Glob patterns to skip (`*.iso`, `*.tmp`, `temp/*`) — case-insensitive on Windows only |
| `verification` | `date_and_size` | Pre-copy compare + post-copy check: `size` / `date_and_size` / `hash` / `full` (`full` compares date+size+hash but validates hash-only) |
| `durability` | `full` | `full` fsyncs each temporary file before rename; `fdatasync` (Linux) flushes file data and size but a freshly written mtime may roll back on power loss — safe, the next comparison re-copies; on Windows it falls back to `full`. `rename-only` skips per-file disk flush and fsyncs the destination directory instead, trading a power-loss window for speed |
| `initial_sync_workers` | `2` | Bounded parallel workers for initial sync only; copying remains complete before parity/deletion cleanup |
| `retry.max_attempts` | `5` | Per file: max copy retries on failure |
| `retry.delay_ms` | `1000` | Per file: initial retry delay (ms) |
| `retry.backoff` | `2.0` | Per file: delay multiplier per retry (1s → 2s → 4s) |
| `lock_handling` | `retry` | Source lock: `retry` = wait when another instance holds the lock / `ignore` = take a fail-fast shared lock and refuse to start when contended — never lock-free. Matched case-sensitively |
| `lock_wait_seconds` | `600` | Source lock: total seconds to wait on a held lock before giving up, with progressive backoff (2s→5s→15s→30s→60s cap, only when `lock_handling: retry`) |
| `delete_threshold_count` | `1000` | Block deletion if one burst contains >= N files (`0` disables this wire) |
| `delete_threshold_size_gb` | `50` | Block deletion if one burst total size >= N GB (`0` disables this wire) |
| `delete_threshold_percent` | `50` | Block deletion if one burst >= N% of known files (needs 50+ known files; unknown baseline fails closed) |
| `bulk_cleanup_percent` | `0` (off) | A burst localized to one folder proceeds immediately as folder cleanup when that folder owns >= N% of the burst (needs a trustworthy baseline and 10+ files) |
| `delete_hold_days` | `7` | Days to hold blocked deletions before syncing; daily warnings sent (`0` or negative falls back to 7) |
| `full_routine_check_days` | `14` | Days between full content routine checks: when due, unchanged files are byte-compared instead of skipped; `0` disables |
| `routine_check_days` | `0` (off) | Days between destination sample checks; `0` disables |
| `routine_check_files` | `10` | Files re-hashed per routine check |
| `destination_protect` | `false` | Harden the destination at startup against other users (Windows deny-write / Linux permission strip); turning it off does not revert |
| `spike_window_minutes` | `60` | Churn-guard sliding window (minutes); `0` or negative falls back to 60 |
| `spike_bytes_gb` | `100` | Trip when copied bytes in the window reach N GB (`0` or negative falls back to 100) |
| `spike_files` | `50000` | Trip when changed files in the window reach N (`0` or negative falls back to 50000) |
| `spike_renames` | `10000` | Trip when renames in the window reach N (`0` or negative falls back to 10000) |
| `versioning.max_versions` | `10` | Keep up to N old versions per file |
| `versioning.path` | — | Where archived versions go (timestamp suffix) |
| `deletion.mode` | `archive` | On source deletion: `mirror` = delete dest too / `archive` = move to .archive |
| `deletion.path` | — | Target dir in `archive` mode |

**Path mirroring:** archived and versioned files keep their real folder structure. With a shared `.archive` / `.versions` next to the sync root, deleting `Desktop\foo.txt` lands in `.archive\Desktop\foo.txt` — not in the archive root. Archiving overwrites that same path (only versioning adds timestamp+guid suffixes).

**Durability warning:** `rename-only` preserves atomic temp+rename behavior but does not force each file's data to stable storage before the rename. `fdatasync` (Linux only) flushes file contents and size but a freshly written mtime may roll back on power loss — safe, the next comparison re-copies; on Windows it falls back to `full`. Use the default `full` setting when power-loss durability matters more than initial-sync speed.

**Honest-disk assumption:** `full` and `fdatasync` assume the drive honors flush commands and the machine has clean power (a UPS): disks that silently ignore flushes can lose recently synced data on power loss no matter how correct the fsync sequence is (see Dan Luu's file-consistency notes). `rename-only` explicitly trades this guarantee away for speed — atomic replacement still holds, power-loss durability does not.

**Delete-threshold guard:** a deletion burst trips at `>=` count, `>=` size GB, or `>=` N% of known files (percent needs 50+ known files; an unknown baseline fails closed and holds). The burst moves to the SQLite hold store `tictack-deferred-<lowercase-folder>-<hash>.db` (next to the log file). After `delete_hold_days`, still-missing files are synced and reappeared files cancel individually; warnings log daily with counts and days left (the path sample is `debug`-only). Deleting the `.db` cancels every hold (destination files kept). A burst localized to one folder proceeds immediately as folder cleanup when `bulk_cleanup_percent` is set and that folder owns enough of the burst.

**Deciding a hold early** (stop the service first for approve/cancel — the service owns the store connection):
- `--deferred-approve <stem>` = proceed now, but only with expired batches. Still-missing files delete through the normal path; reappeared ones cancel individually; unexpired batches report "Nothing to approve". The hold ends and the store self-deletes. Destructive — this is the one that removes destination files.
- `--reprove <stem>` (a.k.a. `--deferred-cancel`) = drop the hold, keep every destination file. The store file is deleted afterwards. Same outcome as deleting the `.db` by hand, but explicit and logged.
- Deleting the `.db` yourself = same as reprove. Fail-safe direction is fixed: every path either proceeds through validation or keeps files; nothing deletes silently. `<stem>` is the full leaf-hash from `--deferred-list` (a bare leaf works only when unambiguous).

### monitor

| Field | Default | Description |
|---|---|---|
| `type` | `watcher` | `watcher` = instant OS events, zero CPU idle (`ReadDirectoryChangesW` P/Invoke on Windows, `FileSystemWatcher` on Linux). `usn` = NTFS USN-journal cursor (Windows-only, needs elevation; falls back to `watcher` with a warning otherwise) — miss-proof while running, no buffer overruns. `polling` = periodic dir scan (no missed events). `composite` = watcher for speed + polling as safety net (+ optional `usn` journal member, next row) |
| `usn` | `false` | Composite-only: also tap the NTFS journal as a third member (defense in depth — two independent observers must both miss an event to lose it). Windows + elevation required; probe-skips with a warning otherwise. Ignored with a warning for non-composite types |
| `watcher_buffer_kb` | `64` | Watcher buffer in KB (NTFS on Windows, inotify on Linux). **Larger = survives bursts (git clone, npm install, unzip) without event loss.** Use 512+ for heavy churn. Lower values silently clamp to the minimum |
| `polling_interval_seconds` | `3600` | Full directory scan interval (s) for polling fallback. Lower values silently clamp to 10 |
| `polling_backstop` | `true` | Composite-only: keep the polling member as the hourly safety net. Set `false` to drop it (zero snapshot memory) when the `usn` member is healthy — watcher overflows and USN journal re-baselines then fire on-demand covering scans instead (creates + modifies + deletes, nothing retained). Kept automatically when USN is unavailable. Turning off both still polls — there is no watcher-only composite. Ignored with a warning for non-composite types |
| `restart_delay_seconds` | `10` | Wait before restarting watcher after error |
| `usn_poll_interval_ms` | `200` | Idle sleep (ms) between USN journal reads when no records arrive. Larger = quieter idle, slower pickup. Lower values silently clamp to 50 |
| `usn_parent_prefilter` | `true` | USN only: skip journal records whose parent dir is outside the watched tree before any path resolve (kills the volume-wide resolve tax — Spotify/Temp churn costs zero syscalls). UnderWatch stays the authority, so disabling only costs syscalls, never events |

### logging

| Field | Default | Description |
|---|---|---|
| `level` | `info` | `debug` / `info` / `warn` / `error` (anything else falls back to `info` with a warning) |
| `path` | `tictack.log` | Log file path, dir created automatically |
| `max_size_mb` | `10` | Rotate log after N MB |
| `max_files` | `5` | Keep N rotated logs (`tictack.log`, `.1`, `.2`...) |
| `console` | `false` | Also write to stdout (auto-enabled in `--cli`/`--once`) |
| `alert_path` | unset (alerts off) | Where `TicTack-WARN/ERROR-*.txt` alert files are written — no code default, so leave it set (the examples use the Desktop). On Windows, Desktop is `C:\Users\User\Desktop`; on Linux, `/home/user/Desktop` |

### watchdog

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Per-source stall check (Windows service mode only — no watchdog under `--cli`/systemd): a faulted processor task or a non-empty queue with no progress for a full interval writes `[WATCHDOG]` Error to the log, the `alert_path` dir, and the Windows EventLog. Heartbeat itself logs at `debug` only, so `info` stays quiet |
| `interval_minutes` | `30` | Minutes between checks (also the no-progress stall threshold; values under a minute silently clamp to a minute) |

### external_drives

Run any command on each discovered external drive (Restic, Rclone, rsync, etc.). Discovers drives via a marker file, asks `Proceed? (y/n)` per drive, and executes the configured command, passing `{source}` and `{drive}` placeholders. Commands are killed after 10 minutes (no knob). Drives containing sync destinations are automatically excluded.

| Field | Default | Description |
|---|---|---|
| `command` | `restic ... backup ...` | Shell command with `{source}` (all source paths) and `{drive}` (each discovered drive). Works with any tool — Restic, Rclone, rsync, etc. The default already switches per OS (`restic.exe` on Windows, `restic` on Linux) |
| `working_dir` | exe dir | Working directory for the command |
| `require_marker_file` | `true` | Only run on drives with a marker file (safety gate) |
| `marker_file_name` | `.tictack-target` | Marker file name to look for at drive root |
| `exclude_drives` | `[]` | Drive letters to always skip (system drive + sync destination drives excluded automatically) |

### jobs

Scheduled commands (`cmd.exe /c` Windows, `/bin/sh -c` Linux). Bare `time:` = daily; add `schedule: weekly` + `day:` weekday, `schedule: monthly` + `day:` 1-31 (clamped to short months, so 31 covers the 30th/28th), or `run_once_on:` ISO date for a one-shot (an explicit `schedule: daily` next to `run_once_on:` is still treated as one-shot). If the machine was off at the scheduled time, the job runs on next startup (catch-up fires immediately at startup). Jobs are fire-and-forget with no overlap guard; a timed-out or failed job is never stamped, so it re-fires next cycle.

| Field | Default | Description |
|---|---|---|
| `name` | — | Job label for logs **(required)** |
| `time` | — | Trigger time, `HH:mm` 24h format **(required)** |
| `schedule` | `daily` | Cadence: `daily` \| `weekly` \| `monthly` (mutually exclusive with `run_once_on`) |
| `day` | — | Weekly: weekday name (`Monday`-`Sunday`). Monthly: day of month `1`-`31` |
| `run_once_on` | — | One-shot date, `YYYY-MM-DD` (mutually exclusive with `schedule`/`day`) |
| `command` | — | Shell command (`cmd.exe /c` on Windows, `/bin/sh -c` on Linux). `{source}` = all source paths quoted **(required)** |
| `working_dir` | exe dir | Working directory for the command |
| `timeout_minutes` | 10 | Per job: kill the command if it runs longer than this (`0` or negative skips the job entirely) |

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
- Config paths use Linux separators with full absolute paths (`/home/user/Pictures`) — `~` is not expanded and `[VolumeLabel]` syntax is Windows-only.
- The unit sends SIGINT on stop for a graceful shutdown; deploy dir is `/opt/tictack`.
- `install-service.sh` auto-detects `dotnet`: with a system runtime it publishes framework-dependent, without one it publishes self-contained (~80 MB, no host runtime required).
- Jobs and commands run via `/bin/sh -c`; use Linux syntax and paths in `jobs:` / `external_drives.command`.
