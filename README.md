# TicTack — Real-Time File Sync + Restic Drive Backup

One-way file synchronisation service for Windows (.NET 10). Monitors source directories, copies changes to a destination, with optional versioning, deletion handling, validation, and crash recovery. Also includes an on-demand mode to auto-discover external drives and run Restic backups.

---

## Quick Start

1. Run `service/install-service.bat` as Administrator — compiles, registers, and starts `TicTackSv` service.
2. Edit `service/config.yaml` to point to your source and destination paths.
3. `sc stop TicTackSv` / `sc start TicTackSv` to restart after config changes.
4. Or run manually: `service\TicTackSv.exe --cli` (interactive) or `service\TicTackSv.exe --once` (single pass).

---

## CLI Flags

| Flag | Description |
|---|---|
| `--cli` | Interactive console mode — live monitoring with per-event logging |
| `--once` | Single sync pass over all sources, then exit |
| `--validate` | Load and validate config, exit with code 0/1 |
| `--restic-drives` | Auto-discover external drives with marker file, run restic on each |
| `--service` | Run as Windows Service (auto-detected when non-interactive) |
| `--config <path>` | Path to config file (default: `config.yaml` in exe dir) |

---

## Configuration

All paths support `[VolumeLabel]` syntax (e.g., `[Backup-Disk]\Sync`) — resolves to the actual drive letter at startup. Use absolute paths for source folders (e.g., `C:\Users\YourName\Desktop`).

### sources

```yaml
sources:
  - paths:
      - 'C:\Users\YourName\Desktop'
      - 'C:\Users\YourName\Pictures'
      - 'C:\Users\YourName\Downloads'
    destination: '[TicTack]\Sync'
    debounce_seconds: 10
    filter:
      max_file_size_mb: no-limit
      exclude:
        - "*.iso"
        - "*.tmp"
    sync:
      verification: date_and_size
      retry:
        max_attempts: 5
        delay_ms: 1000
        backoff: 2.0
      lock_handling: retry
      retry_lock_minutes: 10
      delete_threshold_count: 1000
      delete_threshold_size_gb: 50
      delete_threshold_percent: 50
      delete_hold_days: 7
      rename_detection: true
      versioning:
        max_versions: 10
        path: '[TicTack]\Sync\.versions'
      deletion:
        mode: archive
        path: '[TicTack]\Sync\.archive'
```

| Field | Default | Description |
|---|---|---|
| `paths` | — | List of source folders to monitor **(required)**. Each entry becomes its own source at `destination + foldername` |
| `destination` | — | Drive+folder to sync into, supports `[VolumeLabel]` **(required)** |
| `debounce_seconds` | `10` | Wait time (s) after last change before triggering sync |
| `filter.max_file_size_mb` | `no-limit` | Skip files larger than this (MB). `no-limit` = all files |
| `filter.exclude` | `[]` | Case-insensitive glob patterns to skip (`*.iso`, `*.tmp`, `temp/*`) |
| `sync.verification` | `date_and_size` | Pre-copy compare + post-copy check: `size` / `date_and_size` / `hash` / `full` |
| `sync.retry.max_attempts` | `5` | Max retries on failed copy |
| `sync.retry.delay_ms` | `1000` | Initial retry delay (ms) |
| `sync.retry.backoff` | `2.0` | Delay multiplier per retry (1s → 2s → 4s) |
| `sync.lock_handling` | `retry` | `retry` = wait and retry when lock is held / `ignore` = proceed without lock |
| `sync.retry_lock_minutes` | `10` | Minutes to retry when lock is held before giving up (only when `lock_handling: retry`) |
| `sync.delete_threshold_count` | `1000` | Block deletion if one burst contains >= N files |
| `sync.delete_threshold_size_gb` | `50` | Block deletion if one burst total size >= N GB |
| `sync.delete_threshold_percent` | `50` | Block deletion if one burst >= N% of known files |
| `sync.delete_hold_days` | `7` | Days to hold blocked deletions before syncing; daily warnings sent |
| `sync.rename_detection` | `true` | Track renames (vs delete+re-create, saves bandwidth) |
| `sync.versioning.max_versions` | `10` | Keep up to N old versions per file |
| `sync.versioning.path` | — | Where archived versions go (timestamp suffix) |
| `sync.deletion.mode` | `ignore` | On source deletion: `mirror` = delete dest too / `archive` = move to .archive / `ignore` = leave dest alone |
| `sync.deletion.path` | — | Target dir in `archive` mode |

### monitor

```yaml
monitor:
  type: composite
  watcher_buffer_kb: 512
  polling_interval_seconds: 300
  restart_delay_seconds: 10
```

| Field | Default | Description |
|---|---|---|
| `type` | `composite` | `watcher` = P/Invoke ReadDirectoryChangesW (instant, zero CPU). `polling` = periodic dir scan (no missed events). `composite` = both (watcher for speed, polling as safety net) |
| `watcher_buffer_kb` | `64` | Raw NTFS notify buffer in KB. **Larger = survives bursts (git clone, npm install, unzip) without event loss.** Use 512+ for heavy churn |
| `polling_interval_seconds` | `3600` | Full directory scan interval (s) for polling fallback. Min 10 |
| `restart_delay_seconds` | `10` | Wait before restarting watcher after error |

### logging

```yaml
logging:
  level: info
  path: '[TicTack]\Sync\tictack.log'
  max_size_mb: 10
  max_files: 5
  console: false
```

| Field | Default | Description |
|---|---|---|
| `level` | `info` | `debug` / `info` / `warn` / `error` / `silent` |
| `path` | `tictack.log` | Log file path, dir created automatically |
| `max_size_mb` | `10` | Rotate log after N MB |
| `max_files` | `5` | Keep N rotated logs (`tictack.log`, `.1`, `.2`...) |
| `console` | `false` | Also write to stdout (auto-enabled in `--cli`/`--once`) |

### watchdog

```yaml
watchdog:
  enabled: true
  interval_minutes: 30
```

| Field | Default | Description |
|---|---|---|
| `enabled` | `true` | Periodic heartbeat log entry to prove the service is alive |
| `interval_minutes` | `30` | Minutes between heartbeats |

### restic_drives

```yaml
restic_drives:
  command: restic.exe backup --compression auto "{source}" -r "{drive}\restic-repo"
  require_marker_file: true
  marker_file_name: .restic-target
```

Template for `--restic-drives` CLI mode. Discovers external drives with a marker file and runs the command on each. Drives containing sync destinations are automatically excluded.

| Field | Default | Description |
|---|---|---|
| `command` | `restic.exe backup ...` | Template with `{source}` (all source paths) and `{drive}` (each discovered drive) |
| `working_dir` | exe dir | Working directory for the command |
| `require_marker_file` | `true` | Only run on drives with a marker file (safety gate) |
| `marker_file_name` | `.restic-target` | Marker file name to look for at drive root |
| `exclude_drives` | `[]` | Drive letters to always skip (system drive + sync destination drives excluded automatically) |

### jobs

```yaml
jobs:
  - name: restic_archive
    time: "14:00"
    command: restic.exe backup --compression auto "{source}" -r D:\ResticRepo
    working_dir: C:\ProgramData\TicTack
  - name: restic_archive_evening
    time: "21:00"
    command: restic.exe backup --compression auto "{source}" -r D:\ResticRepo
    working_dir: C:\ProgramData\TicTack
```

Scheduled commands run once per day via `cmd.exe /c`. Unaffected by `--restic-drives`. Define multiple entries to run the same command at different times.

| Field | Default | Description |
|---|---|---|
| `name` | — | Job label for logs **(required)** |
| `time` | — | Daily trigger time, `HH:mm` 24h format **(required)** |
| `command` | — | `cmd.exe /c` command. `{source}` = all source paths quoted **(required)** |
| `working_dir` | exe dir | Working directory for the command |

If the machine is off during a scheduled time, the job runs on next startup (catch-up within 30 seconds).

---

## Architecture

```
ReadDirectoryChangesW + PollingMonitor (composite mode)
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
   IDeletionStrategy — handle source deletions (mirror | archive | ignore)
```

### Crash Recovery (PowerGuard)
- CopyAction writes to `{dest}.tictack.tmp` first, then fsync + atomic rename.
- On startup, PowerGuard scans for orphaned `.tictack.tmp` files and recovers or removes them.

### Desktop Alerts
Warnings and errors create `TicTack-WARN-*.txt` / `TicTack-ERROR-*.txt` on the user's Desktop (30s cooldown between same-level alerts).

### Locked File Handling
FileAccessor uses `CreateFile` P/Invoke with `FILE_FLAG_BACKUP_SEMANTICS` + `FileShare.ReadWrite|Delete`, bypassing most locks for the SYSTEM account. When the destination is locked by another process, TicTack retries for `retry_lock_minutes` (default 10) before proceeding without the lock.

---

## Building from Source

Requires: .NET 10 SDK.

```
service\build.bat
```

Output: `service\TicTackSv.exe` + DLLs

Dependencies: **YamlDotNet 16.3.0**, **Microsoft.Data.Sqlite 10.0.9** (via NuGet; `dotnet restore` fetches automatically).

---

## Project Layout

```
TicTack/
├── src/                  # C# source (24 files)
├── tests/                # Unit tests + MinimalService
├── service/              # Service runtime + scripts
│   ├── build.bat         # Compile → service/
│   ├── install-service.bat
│   ├── start-service.bat
│   ├── stop-service.bat
│   ├── uninstall-service.bat
│   ├── restore-packages.ps1
│   ├── setup.iss         # Inno Setup installer
│   └── config.yaml       # Runtime config (copied to install dir)
├── AGENTS.md
└── README.md
```

---

## Service Management

| Script | Action | Admin req. |
|---|---|---|
| `service\install-service.bat` | Build + register + start | Yes |
| `service\start-service.bat` | Start | No |
| `service\stop-service.bat` | Stop | No |
| `service\uninstall-service.bat` | Stop + delete | Yes |

Service name: `TicTackSv` — runs as `LocalSystem`, auto-start.
