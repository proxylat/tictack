<div align="center">

<pre>
 _____ _     _____          _    
|_   _(_) __|_   _|_ _  ___| | __
  | | | |/ __|| |/ _` |/ __| |/ /
  | | | | (__ | | (_| | (__|   &lt; 
  |_| |_|\___||_|\__,_|\___|_|\_\
</pre>

**One-way file synchronization, system image backup, and third-party software scheduler for Windows and Linux.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Linux-informational.svg)](docs/installation.html)

</div>

---

## Why TicTack

TicTack combines three roles in a single process:
1. **File synchronization:** Runs as a Windows service, a `systemd` unit, or directly from the CLI. It watches your user folders and mirrors changes to a backup drive with version history and deletion archiving.
2. **System image and volume backup (`Corpus`):** Captures live Windows volumes and disks at the block level into an incremental, deduplicated chunk store or bootable VHDX and WIM images.
3. **Third-party software scheduler (`jobs:` and `external_drives:`):** Schedules and sandboxes external tools like `restic`, `rclone`, and `Corpus`, so one process coordinates local mirroring, disk imaging, and offsite cloud backups.

I built it because I could not find a tool that stayed idle when it had nothing to do while still matching the safety guards and feature set of closed-source backup suites.

## Design goals

- **Do one thing well and obey least astonishment:** TicTack handles one-way file synchronization, block-level volume imaging, and scheduled job execution. If a setting in your configuration file is misspelled or duplicated, startup fails immediately and names the bad key instead of running with silent defaults.
- **Zero-CPU idle, flexible sync timing, and EcoQoS scheduling:** You can mirror changes in real time, after a configurable debounce delay, on a periodic polling interval, or as a one-shot CLI pass. Between file events, the service sleeps on OS kernel notifications at 0% CPU. When a sync or backup runs, optional EcoQoS efficiency mode and low I/O priority keep background transfers off foreground CPU cores so active work or games do not stutter.
- **Continuous Data Protection (near-zero RPO) and GFS retention:** Live file changes mirror after your configured delay, keeping your Recovery Point Objective (RPO, the amount of recent work you could lose if a drive fails) near zero. Overwritten and deleted files rotate into separate history (`.versions`) and deleted-items (`.archive`) folders with independent Grandfather-Father-Son (GFS) retention (`keep_daily`, `keep_weekly`, `keep_monthly`, `keep_yearly`, `keep_last`, `keep_within`, `--clean-all`) and `.pin` locks.
- **Crash consistency, sparse preservation, and power-loss recovery:** Existing backup files are never overwritten in place. New data streams to a temporary file beside the target, flushes to physical disk along with the parent directory entry, and swaps in atomically. Thin-provisioned virtual machine disks (sparse files) copy only their allocated data blocks without inflating to their full virtual size, and interrupted large transfers resume from where they stopped.
- **Fail fast, fail loud, and circuit-break anomalies:** A pre-read check catches file lock and permission failures before copying begins. Pre-flight disk fit checks verify the entire backup fits on the destination drive, and passive local network checks verify routing, link health, packet error deltas, and local gateway latency before cloud jobs start. Mass-deletion holds and sliding-window churn circuit-breakers pause ransomware-like spikes during a cooling-off period and write warning files to your Desktop.
- **Sandboxed job orchestration and low-latency remote sync:** Scheduled third-party tools (`restic`, `rclone`, `Corpus`) run inside OS process sandboxes (Windows JobObjects that block UI popups and terminate orphan child processes on timeout). Built-in remote LAN and VM transfers run over authenticated TCP inside a Low-Integrity or restricted OS token locked to the destination folder.

## Architecture

```
  PILLAR 1: File Sync (TicTackSv / CLI - Windows 10/11 & Linux)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ 1. Startup & Pre-flight                                                 │
  │    SrcLock (.tictack.lock) ──► PowerGuard (recover .tmp / keep .resume) │
  │    ──► Parallel Walk + Filter + Comparer + StateDb (skip unchanged)     │
  │    ──► DiskSpaceGuard (whole-sync fit check + min_free_gb floor)        │
  │                                                                         │
  │ 2. Live or Delayed Event Pipeline                                       │
  │    OS Monitor (Watcher / NTFS USN Journal / Polling / Composite)        │
  │    ──► Configurable Debounce & ReadyDrain (scan | ready_queue)          │
  │    ──► Circuit Breakers: ChurnMonitor (spike hold) & DeletionGuard      │
  │    ──► Versioning (.versions) ──► CopyAction (1B probe ──► .tictack.tmp │
  │        with Sparse & Resume) ──► Completer (fsync + atomic rename +     │
  │        DirSync) ──► Validator (size | hash) ──► StateDb (SQLite WAL)    │
  │    ──► Source Deletes: Archive (.archive) | Mirror | DeferredHold (.db) │
  │                                                                         │
  │ 3. Periodic & Retention Maintenance                                     │
  │    ParityScanner (6h) · Scrubber (sample hash verify) · Watchdog        │
  │    Independent GFS Pruners: --forget (.versions) & --archive-forget     │
  └─────────────────────────────────────────────────────────────────────────┘

  PILLAR 2: System Image & Volume Backup (Corpus - Windows 10/11)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ pre_freeze hook ──► VSS Snapshot / Multi-Volume Consistency Group       │
  │ (VSS + EventSystem + swprv + volsnap/volmgrx + GatherWriterStatus)      │
  │ ──► finally: post_thaw hook                                             │
  │ ──► NTFS $Bitmap (skip free space) + USN / MFT Digest Incremental Diff  │
  │ ──► Parallel Chunker (4 MiB fixed | Buzhash CDC + zstd + depth-1 delta) │
  │ ──► 16 MiB CPK1 Packs (+ optional Encrypt & RS .ecc) + SQLite Index     │
  │ ──► Outputs: Dedup Store (GFS --gc) · Dynamic VHDX · WIM/ESD · --extract│
  └─────────────────────────────────────────────────────────────────────────┘

  PILLAR 3: Third-Party Software Scheduler (jobs: & external_drives:)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │ Triggers: Calendar (daily/weekly/monthly/once) · Interval (every:)      │
  │           External Drive Marker Discovery (.tictack-target)             │
  │ ──► Pre-flight: NetCheck (local route + NIC error delta + gateway RTT)  │
  │                 Power Gates (on_ac_only + prevent_sleep) + pre_command  │
  │ ──► Execution:  ProcessRunner inside Windows JobObject Sandbox          │
  │                 (KILL_ON_JOB_CLOSE + UILIMIT_ALL + EcoQoS/low_priority) │
  │ ──► Targets:    Corpus.exe · restic · rclone · rsync · RemoteAgent (TCP)│
  │ ──► Follow-up:  post_command & on_failure hooks                         │
  └─────────────────────────────────────────────────────────────────────────┘
```

## Getting started

### 1. Build and install the service (or run from the CLI)

```bash
# Windows 10/11 (run cmd.exe as Administrator; supports --jit or --aot)
copy service\win\config_win.yaml.example service\win\config.yaml
service\win\build-service.bat --jit

# Linux (publishes to /opt/tictack and enables the systemd unit)
cp service/linux/config_linux.yaml.example service/linux/config.yaml
cd service/linux && ./build-service.sh

# Or run directly from the CLI (interactive watch or single-pass):
TicTackSv --cli
TicTackSv --once
```

### 2. Configure sources and scheduled backup jobs (`config.yaml`)

```yaml
sources:
  - paths:
      - "C:\\Users\\Alice\\Documents"
      - "C:\\Users\\Alice\\Projects"
    destination: "[Backup-Disk]\\Mirror"
    debounce_seconds: 10
    sync:
      verification: date_and_size
      durability: full
      min_free_gb: 20
      strict_fit_check: true
      versioning:
        path: "[Backup-Disk]\\Mirror\\.versions"
        keep_daily: 7
        keep_weekly: 4
        keep_monthly: 6
        keep_yearly: 1
      deletion:
        mode: archive
        path: "[Backup-Disk]\\Mirror\\.archive"
        keep_daily: 14
        keep_monthly: 3

jobs_defaults:
  sandbox: true
  timeout_minutes: 60

jobs:
  # Windows block-level VSS consistency group snapshot via Corpus
  - name: corpus-system-image
    time: "02:00"
    command: "Corpus.exe --config C:\\ProgramData\\TicTack\\config_corpus.yaml --image --volumes C:,D: --name nightly"

  # Deduplicated encrypted repository snapshot via restic
  - name: restic-repo
    time: "03:00"
    command: "restic -r [Backup-Disk]\\Restic backup {source} && restic -r [Backup-Disk]\\Restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --keep-yearly 1 --prune"

  # Cloud offload via rclone, gated by passive local network stability check
  - name: rclone-cloud
    time: "04:00"
    net_check: true
    command: "rclone sync [Backup-Disk]\\Restic s3:my-offsite-bucket/restic"
```

## Requirements and feature-specific dependencies

### Core requirements (always required)
- **Operating system:** **Windows 10/11** (`TicTackSv` service or CLI) or **Linux** (`systemd` for service mode, `inotify` for default folder watching, or CLI).
- **Build and runtime:** .NET 10 SDK (or published self-contained / NativeAOT binary). Four bundled NuGet packages: `YamlDotNet 16.3.0`, `Microsoft.Data.Sqlite 10.0.12`, `System.ServiceProcess.ServiceController`, and `System.Diagnostics.EventLog`.

### Feature-specific OS services, drivers, and tools (only required when using the feature)

| Feature | Platform | Underlying OS Services, Drivers & Binaries |
|---|---|---|
| **Live Volume / Disk Imaging (`Corpus`) & Locked-File VSS (`sync.file_access: vss`)** | Windows 10/11 (Admin / `LocalSystem`) | **Services:** `VSS` (Volume Shadow Copy), `EventSystem` (COM+ Event System), `swprv` (Software Shadow Copy Provider, must not be disabled).<br>**Kernel drivers:** `volsnap` (shadow copy driver), `volmgr` / `volmgrx` (Volume Manager / Dynamic Volume Manager).<br>**Inbox DLL / CLI:** `vssapi.dll` (requires JIT/framework build for COM interop), `vssadmin.exe` (writer diagnostics). Checked by `Corpus.exe --validate`. |
| **NTFS USN Journal Monitoring (`monitor.type: usn` & Corpus incremental USN)** | Windows 10/11 (Admin / `LocalSystem`) | NTFS volume + Administrator / `LocalSystem` privileges (falls back to standard folder watcher when unprivileged). |
| **BitLocker Volume Imaging (`Corpus` on encrypted drives)** | Windows 10/11 (Admin) | **Service & driver:** `BDESVC` (BitLocker Drive Encryption Service) and `fvevol` (BitLocker Filter Driver).<br>**Inbox CLI:** `manage-bde.exe` (suspends and resumes protection across the VSS freeze window). |
| **WIM / ESD / Bare-Metal Restore & Recovery Media (`Corpus` optional verbs)** | Windows 10/11 (or Linux with `wimlib`) | `dism.exe` (inbox) or `wimlib-imagex` (WIM/ESD capture, mount, and servicing); `diskpart.exe` and `bcdboot.exe` (inbox, for `--restore-disk` and `--repair-boot`); Windows ADK `oscdimg.exe` (only for `--build-media`); Hyper-V PowerShell module (only for `--optimize-vhd` and `--boot-verify`). |
| **Local Network Stability Check (`net_check: true` on `jobs:`)** | Windows 10/11 & Linux | Windows: in-box `.NET NetworkInformation` APIs. Linux: `/proc/net/route`, `/proc/net/dev`, `/sys/class/net`, and `ping` (when `net_check_icmp: true`). |
| **Third-Party Scheduled Jobs (`jobs:` & `external_drives:`)** | Windows 10/11 & Linux | Any external CLI tool invoked by your jobs (`restic`, `rclone`, `rsync`, `age`, `Corpus.exe`). Register user-installed tools on the service `PATH` once via `service\win\add-tool-env.bat <tool>` or `sudo service/linux/add-tool-env.sh <tool>`. |

## Limitations

- **No macOS support for now:** TicTack targets Windows 10/11 and Linux. There is no macOS service, watcher, or snapshot integration yet.
- **Corpus is Windows-only for now:** Corpus depends on Windows VSS (`vssapi.dll`), direct NTFS `$MFT`, `$Bitmap`, and USN parsing, and VHDX/WIM APIs. TicTack file sync and third-party job scheduling run on both Windows 10/11 and Linux.
- **One-way authority:** TicTack mirrors source to destination; it does not perform bidirectional merge conflict resolution.
- **No `%VAR%` or `~` expansion in config paths:** Because services run as `LocalSystem` or `root`, environment variables would resolve to the wrong profile. Use full paths (`C:\Users\Alice\Desktop`, `/home/alice/Pictures`) or Windows `[VolumeLabel]` syntax.

## Documentation

- [Overview & Design Goals](docs/index.html) · [Installation & Service Dependencies](docs/installation.html) · [Features, CLI & YAML Reference](docs/features.html) · [Third-Party Jobs & Cloud Integrations](docs/features.html#jobs) · [Diagnostics & Profiling](docs/features.html#diagnostics) · [Corpus (Windows System Imaging)](docs/corpus.html)

## License

Licensed under [MIT](LICENSE).
