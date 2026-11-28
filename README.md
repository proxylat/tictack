<div align="center">

<pre>
 _____ _     _____          _    
|_   _(_) __|_   _|_ _  ___| | __
  | | | |/ __|| |/ _` |/ __| |/ /
  | | | | (__ | | (_| | (__|   &lt; 
  |_| |_|\___||_|\__,_|\___|_|\_\
</pre>

**One-way file synchronization, system image backup, and third-party automations.**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4.svg)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20%7C%20Linux-informational.svg)](https://proxylat.github.io/tictack/)
[![Docs](https://img.shields.io/badge/docs-proxylat.github.io%2Ftictack-blue.svg)](https://proxylat.github.io/tictack/)

</div>

---

## Why TicTack

The repository ships two dependency-light binaries — `TicTackSv.exe` and `Corpus.exe`:
1. **File synchronization (`TicTackSv`):** Runs as a Windows service, a `systemd` unit, or directly from the CLI. It watches your user folders and mirrors changes to a backup drive with version history and deletion archiving.
2. **Low-latency remote sync (`TicTackSv` `remote_sync:`):** Pulls live changes from remote machines or VMs over encrypted `TTRM` v1 TCP (TLS 1.2/1.3 + mutual PSK) with an OS-sandboxed `--remote-agent` and push change stream.
3. **Third-party automations (`TicTackSv` `jobs:` & `external_drives:`):** Puts your whole backup stack on autopilot — one engine schedules, sandboxes, and monitors tools like `Corpus.exe`, `restic`, and `rclone`, so local mirroring, disk imaging, and offsite cloud backups run themselves.
4. **System image and volume backup (`Corpus.exe`):** Standalone Windows 10/11 block-level imager that captures live NTFS volumes and 512e/4Kn GPT/MBR physical disks via VSS into a crash-safe, deduplicated chunk store (`--full`, `--incremental`, `--differential`, `--verify-blocks`, `encrypt:`/`decrypt:` hooks, instant `--serve-nbd` read-only NBD) or directly into bootable dynamic VHDX (`--export-vhdx --layout`), WIM/ESD, and streamOptimized VMDK/OVF/OVA (`--controller ahci|lsisas|nvme|scsi`), with bare-metal `--restore-disk` (`--repair-boot`, `--shrink-to-fit`) and Hyper-V `--boot-verify` (pre-boot `chkdsk /scan` + Gen 1/2 heartbeat).

I built it because I could not find a tool that stayed idle when it had nothing to do while still matching the safety guards and feature set of closed-source backup suites.

## Design goals

- **Zero-CPU idle, flexible sync timing, and EcoQoS scheduling:** You can mirror changes in real time, after a configurable debounce delay, on a periodic polling interval, or as a one-shot CLI pass. Between file events, the service sleeps on OS kernel notifications at 0% CPU. When a sync or backup runs, optional EcoQoS efficiency mode and low I/O priority keep background transfers off foreground CPU cores so active work or games do not stutter.
- **Continuous Data Protection (near-zero RPO) and GFS retention:** Live file changes mirror after your configured delay, keeping your Recovery Point Objective (RPO, the amount of recent work you could lose if a drive fails) near zero. Overwritten and deleted files rotate into separate history (`.versions`) and deleted-items (`.archive`) folders with independent Grandfather-Father-Son (GFS) retention (`keep_daily`, `keep_weekly`, `keep_monthly`, `keep_yearly`, `keep_last`, `keep_within`, `--clean-all`) and `.pin` locks.
- **Crash consistency, sparse preservation, and power-loss recovery:** Existing backup files are never overwritten in place. New data streams to a temporary file beside the target, flushes to physical disk along with the parent directory entry, and swaps in atomically. Thin-provisioned virtual machine disks (sparse files) copy only their allocated data blocks without inflating to their full virtual size, and interrupted large transfers resume from where they stopped.
- **Fail fast, fail loud, and circuit-break anomalies:** A pre-read check catches file lock and permission failures before copying begins. Pre-flight disk fit checks verify the entire backup fits on the destination drive, and passive local network checks verify routing, link health, packet error deltas, and local gateway latency before cloud jobs start. Mass-deletion holds and sliding-window churn circuit-breakers pause ransomware-like spikes during a cooling-off period and write warning files to your Desktop.
- **Sandboxed third-party automations and low-latency remote sync:** Your automations (`restic`, `rclone`, `Corpus`) run inside OS process sandboxes (Windows JobObjects that block UI popups and terminate orphan child processes on timeout). Built-in remote LAN and VM transfers run over authenticated TCP inside a Low-Integrity or restricted OS token locked to the destination folder.

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
| **Third-Party Automations (`jobs:` & `external_drives:`)** | Windows 10/11 & Linux | Any external CLI tool invoked by your automations (`restic`, `rclone`, `rsync`, `age`, `Corpus.exe`). Register user-installed tools on the service `PATH` once via `service\win\add-tool-env.bat <tool>` or `sudo service/linux/add-tool-env.sh <tool>`. |
| **Windows Event Log (`TicTackSv` & `Corpus` alerts)** | Windows 10/11 | **Service:** `EventLog` (Windows Event Log). Recommended so `Warn` and `Error` entries appear in Event Viewer under sources `TicTackSv` and `Corpus`; degrades gracefully to `tictack.log` / `corpus.log` if stopped. |

### JIT vs. NativeAOT (`--aot`)

- **TicTack (`TicTackSv`) & Corpus (`Corpus.exe`):** Both support `--aot` (including VSS via `[GeneratedComInterface]` + `[LibraryImport]` + `StrategyBasedComWrappers`). Keep `e_sqlite3.dll` next to the AOT binary.
- **Diagnostics & build:** NativeAOT binaries do not expose CoreCLR EventPipe (`dotnet-counters` / `dotnet-trace` / `dotnet-gcdump` require JIT; use ETW/PerfView or `perf`/`bpftrace` for AOT) and must be compiled on the target OS (MSVC on Windows, `clang` + `zlib` on Linux).

## Limitations

- **No macOS support for now:** TicTack targets Windows 10/11 and Linux. There is no macOS service, watcher, or snapshot integration yet.
- **Corpus is Windows-only for now:** Corpus depends on Windows VSS (`vssapi.dll`), direct NTFS `$MFT`, `$Bitmap`, and USN parsing, and VHDX/WIM APIs. TicTack file sync and third-party automations run on both Windows 10/11 and Linux.
- **One-way authority:** TicTack mirrors source to destination; it does not perform bidirectional merge conflict resolution.
- **No `%VAR%` or `~` expansion in config paths:** Because services run as `LocalSystem` or `root`, environment variables would resolve to the wrong profile. Use full paths (`C:\Users\Alice\Desktop`, `/home/alice/Pictures`) or Windows `[VolumeLabel]` syntax.

## Documentation

Live site: **https://proxylat.github.io/tictack/**

- [Overview](https://proxylat.github.io/tictack/index.html#page-overview) · [Requirements & constraints](https://proxylat.github.io/tictack/index.html#page-requirements) · [Quickstart](https://proxylat.github.io/tictack/index.html#page-setup) · [CLI (TicTack)](https://proxylat.github.io/tictack/index.html#page-cli) · [YAML (TicTack)](https://proxylat.github.io/tictack/index.html#page-configuration) · [Remote sync](https://proxylat.github.io/tictack/index.html#page-remote) · [Third-party automations](https://proxylat.github.io/tictack/index.html#page-jobs) · [Corpus (Architecture)](https://proxylat.github.io/tictack/index.html#page-corpus) · [CLI (Corpus)](https://proxylat.github.io/tictack/index.html#page-corpus-cli) · [YAML (Corpus)](https://proxylat.github.io/tictack/index.html#page-corpus-yaml) · [Diagnostics & profiling](https://proxylat.github.io/tictack/index.html#page-diagnostics)

## License

Licensed under [MIT](LICENSE).
