# TicTack — Real-Time File Sync + Automations

One-way file synchronisation service for Windows and Linux. Monitors source directories, copies changes to a destination, with optional versioning, deletion handling, validation, and crash recovery. Also runs scheduled tasks and external-drive jobs — any shell command.

---

## Design Principles

- **Never lose data** — fsync before rename ensures data is on disk before the final path is updated. Post-copy validation (size, hash, or both) verifies every file. Three layers of integrity: pre-read probe catches permissions early, temp+rename prevents partial overwrites, validation catches corruption.
- **Detect early, fail fast** — a 1-byte read from the source catches ~90% of lock/permission/access issues before the full copy starts. Delete-threshold guard blocks accidental mass deletions. Source-disappearance guard prevents syncing from an unmounted or empty directory.
- **Zero CPU idle** — `SemaphoreSlim` blocks the processing thread with no CPU usage when no events are queued. No polling timers. SQLite state DB uses incremental writes (no periodic full rewrites). The only wake-ups are file-change events from the OS.
- **Crash-proof by construction** — every write follows the temp-then-rename pattern: no partial file ever lands at the final path. Startup recovery (`PowerGuard.Cleanup()`) collects orphaned `.tictack.tmp` files. SQLite WAL journal survives power loss without corruption. Lock files have stale-detection and auto-release.
- **No secrets** — zero external network calls, no accounts, no cloud. Local diagnostic logs (`tictack-diag.log`, EventLog) stay on the machine.
- **Dependency-light** — four runtime dependencies (Microsoft.Data.Sqlite, YamlDotNet, System.ServiceProcess.ServiceController, System.Diagnostics.EventLog). No npm, pip, cargo, or gem trees.

---

## Features

| Feature | Implementation |
|---|---|
| Change monitoring | `FileWatcherMonitor`, `PollingMonitor`, `CompositeMonitor` — pluggable via `IFileMonitor` |
| Comparison (date/size/hash) | `IFileComparer` — `SizeComparer`, `DateSizeComparer`, `HashComparer`, `FullComparer` |
| Locked file handling | `FileAccessor` opens with `FileShare.ReadWrite|Delete` + `FILE_FLAG_BACKUP_SEMANTICS` (SYSTEM bypass); retry backoff |
| VSS support | Designed via `IFileAccessor` — swap in VSS-based accessor without pipeline changes |
| Retry logic | `ExponentialBackoffRetry` — configurable attempts, delay, backoff multiplier |
| Logging | `FileLogger` (rotating), `ConsoleLogger` (color), `DesktopAlertLogger`, `EventLogLogger`, `MultiLogger` |
| Rename detection | `RenameAction` + `FileChangedEventArgs.OldFullPath` |
| Deletion handling | `MirrorDeletion`, `ArchiveDeletion` — factory-selected |
| Versioning | `TimestampVersioning` (max-versions limit), `NoVersioning` — factory-selected |
| Post-copy validation | `SizeValidator`, `HashValidator` — mirrors comparison level |
| Max file size | `SizeFilter` — `max_file_size_mb: <number>` or `no-limit` |
| Crash Recovery | `CopyAction` writes to `.tictack.tmp` then atomic rename; `PowerGuard.Cleanup()` recovers orphaned temps on startup |
| Desktop alerts | `DesktopAlert.Write()` creates `TicTack-{LEVEL}-{timestamp}.txt` in `alert_path` (30s cooldown per level) |
| Zero-CPU idle | `SemaphoreSlim` in `SyncPipeline` — thread sleeps with zero CPU when idle, wakes instantly on file events |
| Volume label paths | `[VolumeLabel]\path` syntax resolved to drive letters via `DriveInfo.GetDrives()` |
| Scheduled jobs | `TimerScheduler` — daily shell commands (`cmd.exe /c` Windows, `/bin/sh -c` Linux) with `{source}` substitution |
| External drive tasks | `DriveDiscoverer` — runs a shell command on each discovered external drive |
| State DB (skip-known) | `StateDb` — SQLite WAL, per-source, `size+mtime` cache to skip unchanged files on startup |
| Pre-read fail-fast | `CopyAction` probes 1 byte before full copy — catches permission/lock issues instantly |
| Deferred deletion | `DeferredDeletion` — holds blocked deletions for `delete_hold_days`, daily warnings with first 20 paths, recheck before sync |
| Delete-threshold guard | Blocks deletions when >50% of known files would be removed in one batch, or when count/size exceeds configured limits |
| Source-disappearance guard | Refuses to process Deleted events when source folder is missing |
| Exclusive lock | `.tictack.lock` — Restic-style 2-phase check, 200ms settle, 5min stale timeout, 30s refresh, configurable retry timeout |
| Sparse file support | Detects `FILE_ATTRIBUTE_SPARSE_FILE`, uses `FSCTL_SET_SPARSE` via `DeviceIoControl` |
| EventLog propagation | `EventLogLogger` writes errors to Windows Application log under `TicTackSv` source |

---

## Architecture

```
FileWatcherMonitor (Windows) / FsWatchMonitor (Linux) + PollingMonitor (composite mode)
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
   IRetryPolicy + CopyAction (.tictack.tmp → fsync → atomic rename)
        │
        ▼
   IValidator (size | hash) — post-copy integrity check
        │
        ▼
   IDeletionStrategy — handle source deletions (mirror | archive)
```

---

## Quick Start

Requires: .NET 10 SDK. Dependencies: **YamlDotNet 16.3.0**, **Microsoft.Data.Sqlite 10.0.9** (via NuGet; `dotnet restore` fetches automatically).

### Windows

1. Copy `service\win\config_win.yaml.example` to `service\win\config.yaml` and edit your source/destination paths.
2. Run `service\win\install-service.bat` as Administrator — compiles (`dotnet publish`), registers the `TicTackSv` Windows service, and starts it.
3. `sc stop TicTackSv` / `sc start TicTackSv` to restart after config changes.

Or run manually: `service\win\TicTackSv.exe --cli` (interactive) or `service\win\TicTackSv.exe --once` (single pass).

### Linux

1. Copy `service/linux/config_linux.yaml.example` to `service/linux/config.yaml` and edit your paths.
2. Run `cd service/linux && ./install-service.sh` — publishes, installs to `/opt/tictack`, and enables the `tictack` systemd service.
3. `sudo systemctl restart tictack` to restart after config changes.

### Tests

```
dotnet test tests\TicTack.Tests.csproj
```

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

All paths support `[VolumeLabel]` syntax on Windows (e.g., `[Backup-Disk]\Sync`) — resolves to the actual drive letter at startup.

**Do not use environment variables like `%USERPROFILE%` or `%HOME%`.** The Windows service runs as `LocalSystem`, so `%USERPROFILE%` resolves to `C:\WINDOWS\system32\config\systemprofile`, not your profile — same for `%HOME%` under systemd (root). Always write the full path (`C:\Users\User\Desktop`).

### sources

| Field | Default | Description |
|---|---|---|
| `paths` | — | List of source folders to monitor. Each entry becomes its own source at `destination + foldername` |
| `destination` | — | Drive+folder to sync into, supports `[VolumeLabel]` |
| `state_db_path` | `C:\ProgramData\TicTack` / `/var/lib/tictack` | **Directory** for per-source state DBs (`<folder>.db`, SQLite WAL) used to skip unchanged files on startup |
| `debounce_seconds` | `10` | Wait time (s) after last change before triggering sync |
| `max_file_size_mb` | `no-limit` | Skip files larger than this (MB). `no-limit` = all files |
| `exclude` | `[]` | Case-insensitive glob patterns to skip (`*.iso`, `*.tmp`, `temp/*`) |
| `verification` | `date_and_size` | Pre-copy compare + post-copy check: `size` / `date_and_size` / `hash` / `full` |
| `max_attempts` | `5` | Max retries on failed copy |
| `delay_ms` | `1000` | Initial retry delay (ms) |
| `backoff` | `2.0` | Delay multiplier per retry (1s → 2s → 4s) |
| `lock_handling` | `retry` | `retry` = wait and retry when lock is held / `ignore` = proceed without lock |
| `retry_lock_minutes` | `10` | Minutes to retry when lock is held before giving up (only when `lock_handling: retry`) |
| `delete_threshold_count` | `1000` | Block deletion if one burst contains >= N files |
| `delete_threshold_size_gb` | `50` | Block deletion if one burst total size >= N GB |
| `delete_threshold_percent` | `50` | Block deletion if one burst >= N% of known files |
| `delete_hold_days` | `7` | Days to hold blocked deletions before syncing; daily warnings sent |
| `rename_detection` | `true` | Track renames (vs delete+re-create, saves bandwidth) |
| `max_versions` | `10` | Keep up to N old versions per file |
| `path` | — | Where archived versions go (timestamp suffix) |
| `deletion_mode` | `archive` | On source deletion: `mirror` = delete dest too / `archive` = move to .archive |
| `path` | — | Target dir in `archive` mode |

**Path mirroring:** archived and versioned files keep their real folder structure. With a shared `.archive` / `.versions` next to the sync root, deleting `Desktop\foo.txt` lands in `.archive\Desktop\foo_ts.txt` — not in the archive root.

**Delete-threshold guard:** if one deletion burst exceeds `delete_threshold_count`, `delete_threshold_size_gb`, or `delete_threshold_percent` of known files, it is deferred to `tictack-deferred-<folder>.json` (next to the log file). After `delete_hold_days`, remaining files are synced; warnings are logged daily with the first 20 paths + full list location.

### monitor

| Field | Default | Description |
|---|---|---|
| `type` | `composite` | `watcher` = instant OS events, zero CPU idle (`ReadDirectoryChangesW` P/Invoke on Windows, `FileSystemWatcher` on Linux). `polling` = periodic dir scan (no missed events). `composite` = both (watcher for speed, polling as safety net) |
| `watcher_buffer_kb` | `64` | Watcher buffer in KB (NTFS on Windows, inotify on Linux). **Larger = survives bursts (git clone, npm install, unzip) without event loss.** Use 512+ for heavy churn |
| `polling_interval_seconds` | `3600` | Full directory scan interval (s) for polling fallback. Min 10 |
| `restart_delay_seconds` | `10` | Wait before restarting watcher after error |

### logging

| Field | Default | Description |
|---|---|---|
| `level` | `info` | `debug` / `info` / `warn` / `error` / `silent` |
| `path` | `tictack.log` | Log file path, dir created automatically |
| `max_size_mb` | `10` | Rotate log after N MB |
| `max_files` | `5` | Keep N rotated logs (`tictack.log`, `.1`, `.2`...) |
| `console` | `false` | Also write to stdout (auto-enabled in `--cli`/`--once`) |
| `alert_path` | exe dir | Where `TicTack-WARN/ERROR-*.txt` alert files are written. On Windows, Desktop is `C:\Users\User\Desktop`; on Linux, `~/Desktop` |

### watchdog

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Periodic heartbeat log entry to prove the service is alive |
| `interval_minutes` | `30` | Minutes between heartbeats |

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
