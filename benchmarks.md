# TicTack Benchmarks

> Moved here from `Projects/perf.md` (2026-09-12). All runs on the same ext4/LVM
> disk; `TicTack.csproj:11` sets `<TieredCompilation>false</TieredCompilation>`.

## PGO verdict — TieredPGO + R2R buy nothing (2026-09-12)

- Enabling PGO = flip `TieredCompilation` to `true` + `DOTNET_TieredPGO=1` at runtime; R2R = publish flag. Neither is set — deliberately.
- SDK: .NET 10.0.401 installed rootless via `dotnet-install.sh` to `~/.dotnet` (621MB; needs `PATH="$HOME/.dotnet:$PATH"`, telemetry off via `DOTNET_CLI_TELEMETRY_OPTOUT=1`). Full suite passes on Linux: **188/188 in 12s** (Debug).
- PGO experiment (csproj untouched — `-p:TieredCompilation=true` override + `DOTNET_TieredPGO=1`): StressTests **18/18 in 11s, twice**. Delta vs baseline = **0**. Verdict: PGO buys nothing on this workload, as predicted (idle file-watcher, I/O-bound bursts). Leave `TieredCompilation=false`.
- Workload mismatch (still true): TicTack is zero-CPU-idle by design (`SemaphoreSlim` sleeps until OS file events); PGO optimizes hot steady-state code, an idle service has none. Burst paths (copy/hash) are I/O- and algorithm-bound.
- Tests exist and are runnable: `tests/` (19 files, incl. 22.8KB `StressTests.cs`) — use them as the measurement workload before any csproj change.
- Rule: demonstrate a CPU hotspot first, then flip.

## TicTack vs raw copy — shootout (2026-09-12)

Fixture: 10,010 files / 2.0GB mixed (9k small text + 1k×1MB + 10×100MB), same ext4/LVM disk. Release `TicTackSv.dll` via bench config (`date_and_size`, mirror deletions, no versioning). Warm page cache throughout (sequential runs, single run each) — relative numbers, not cold-cache absolutes.

| Round | TicTack | `cp` rival | Notes |
|---|---|---|---|
| Initial sync | **56.8s** (`--once`, `diff -r` clean) | **8.5s** (`cp -a --reflink=never`) | ~6.7x integrity tax: per-file probe, temp+rename, validation, SQLite, logging. (First `cp -a` timing 1.9s was a reflink artifact — disclosed, discarded.) |
| Incremental (100 mod + 10 new + 2 del) | **1.2s** (`--once`, `diff -r` clean, deletions mirrored) | **0.12s** (`cp -au`) | cp leaves deleted files behind — doesn't mirror deletions. StateDb skip-known works: 9.9k files skipped. |
| Idle | **34MB RSS, ~0% CPU** (production `/opt/tictack/TicTackSv --cli`, root, 5s CPU over ~40min uptime) | n/a | Zero-CPU-idle claim holds in production, not just design. |

Verdict: TicTack does **not** take the raw-speed crown — `cp` is 6–7x faster at initial copy, as expected (cp does zero integrity work). TicTack wins its actual niche: mirrored deletions, skip-known state, per-file validation, near-zero idle. Fast *enough* for a safety-first local mirror; "top1" at raw throughput was never the design goal. Not raced: robocopy (no Windows), Syncthing (needs pair setup). FFS batch-compare unraceable (0-items bug, below); FFS **RealTimeSync half raced below**.

## P4 stat collapsing (2026-09-12)

- Added `FileSnapshot` reuse across cache lookup, comparison, copy timestamping, validation, and StateDb upsert in all initial-sync paths (`SyncPipeline`, `--once`, and `--rebuild`). Validation receives a fresh post-copy snapshot, so pre-copy metadata is never used to validate the source against itself.
- Fixture: 10,010 files / 2,097,402,893 bytes from `benchmarks/create-fixture.sh`; Release build, warm page cache, one `--once` run.
- Result: **55.893s**, `diff -r /home/user/sync-bench/src /home/user/sync-bench/dst/src` clean. Recorded baseline: **56.8s**. P4 alone is a small improvement; the larger gains remain P1-P3.

## P1-P3 performance rounds (2026-09-12)

- **P1: 30.161s** — initial-sync StateDb upserts are committed in batches of 500; recursive diff clean.
- **P2: 27.059s** — explicit `durability: rename-only`, with buffered flush plus Linux destination-directory fsync; recursive diff clean. `full` remains the default.
- **P3: 20.186s** — explicit `initial_sync_workers: 2`, copy/validate phase parallelized while parity remains after the phase; recursive diff clean.
- Final result is **2.8x faster than the 56.8s baseline**. It does not reach the original 8-10s estimate on this filesystem; worker count remains conservative because higher parallelism needs disk-specific verification.

## Realtime race: RTS vs TicTack watcher (2026-09-12)

Same shape, both at 2s debounce/idle-delay: RTS 14.12 (`RealTimeSync_x86_64` + `.ffs_real`, `Delay=2`, trivial marker command, `DISPLAY=:0`) vs scratch TicTack (`--cli --config tt-bench.yaml`, `debounce_seconds: 2`, Release dll, `DOTNET_ROOT` pointed at project `.dotnet`). Scratch dirs under `/home/user/sync-bench/`. No TicTack code changes.

| Metric | RTS | TicTack scratch | Notes |
|---|---|---|---|
| Idle footprint | **55MB RSS, 8 threads, ~0% CPU** | **59–62MB RSS, 10 threads, ~1% CPU** | Dead heat; both GTK/CLR-runtimes-idling. (Prod TicTack idles at 34MB — scratch carries Debug-adjacent/warm state.) |
| Single-file latency | **2.05s** (touch → marker) | **2.04s** (touch → dst appears, 50ms poll) | Identical: both = debounce setting + ~50ms. Reaction speed is a config choice, not an engine gap. |
| Burst (100 files / ~20ms) | **1 trigger, +2.06s** (perfect coalescing; command itself trivial) | **24 files @+2s, all 100 @+3s** | Different philosophies: RTS debounces to ONE batch run; TicTack debounces then pipelines per-file (~100 small files/s after debounce — the fsync tax in miniature). |

Honest caveats: RTS fired once at startup (~25s after launch, empty watch dir — unexplained, possibly initial-scan trigger); RTS's trigger only proves *detection* — its batch then compares (and hits the FFS batch 0-items bug below, so end-to-end RTS→synced-bytes is unmeasured); TicTack's numbers are end-to-end (bytes landed + validated).
Verdict: **reaction path is a tie** (both debounce-bound); difference is downstream — RTS re-scans whole trees per burst, TicTack pipelines per-file events. For the benchmark's purpose (realtimesync principally): RTS idle/latency/burst-detection all healthy; its weakness is batch-compare, not the monitor.

### FFS batch-compare 0-items bug (open, deprioritized)

FFS 14.12 batch (`race.ffs_batch`, Mirror, TimeAndSize) exits 0/"success" but always `totalItems: 0` — even on 2-file smoke pairs, `-dirpair` CLI path, and TwoWay variant; strace proves roots are listed + children lstat'd, but nothing reaches the grid, no `.sync.ffs_db` written. Config reverse-engineered warning-free from 14.12 source (XmlFormat=23, flat, EmailNotification top-level); effective filter compiled-probe-verified to ACCEPT (`'*'` + db/lock excludes); traversal worker is a job-queue executor (stripped binary, GDB); `DefaultFilter` global excludes (`*/.Trash-*/`, `*/.recycle/`) found in `GlobalSettings.xml` but can't match test paths. Filenames with dots ruled out (extensionless control also 0). Deprioritized: blocks nothing in the realtime benchmark. State cleaned 2026-09-12 (was: `/tmp/opencode/ffs/` install+source+traces, fixture `/home/user/sync-bench/src`).
