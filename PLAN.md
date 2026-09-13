# TicTack Performance Plan — matching `cp`

Goal: initial sync of the 10,010-file / 2.0GB fixture from **56.8s → ~8–10s**
(`cp -a --reflink=never` = 8.5s), keeping `diff -r` clean every round.
Baseline + method: `benchmarks.md` (§cp shootout). Non-goal: beating `cp`
at raw throughput — the integrity niche stays.

## Measurement protocol (every round)

1. Regenerate fixture (deleted during cleanup): 9k small text + 1k×1MB +
   10×100MB under `/home/user/sync-bench/src` (script to be recreated).
2. Release build, warm page cache, single timed `--once` run + `diff -r`.
3. Record time + `diff` result in `benchmarks.md` before next round.

## P4 — stat collapsing (~30–45 min, pure win, no tradeoff)

Fetch `FileInfo` ONCE per file in `Pipeline.cs`, thread it through
comparer → copy → validator → upsert. Trap: never reuse a pre-copy stat
*after* the write — take a fresh stat at the validation boundary, else
validation compares the file against itself.
Risk: stale-FileInfo self-validation. Mitigation: fresh stat at boundary.

## P1 — batch StateDb writes (~30 min, rework-only)

Wrap InitialSync bulk upserts (`Pipeline.cs:277/343/393`) in one
transaction (or defer to end); commit every ~500 files as checkpoint.
Risk: crash mid-batch loses ≤500 DB rows → those files get recopied
(or skipped by comparer — correctness is filesystem-truth, not DB).
Mitigation: the 500-file checkpoint + `PowerGuard.Cleanup` on restart.
Watcher/watchdog unaffected (per-event Upsert stays; restart reconciles).

## P2 — fsync durability dial (~20 min, the real tradeoff)

Config `durability: full` (default, today's behavior) vs `rename-only`
(temp+rename atomicity, no per-file `Flush(true)`; power-loss window only).
Risk: power loss can lose recently renamed files; validation may read
page cache instead of disk. Mitigation: default stays `full`; add
directory-fsync after renames in `rename-only`; document the window.

## P3 — parallel copy workers (~1–2 h, needs disk verification)

Semaphore-limited pipeline (default 2 workers), InitialSync-only first;
copy-before-delete phases preserved so ordering guarantees hold.
Risk: HDD seek storms, delete-before-copy races. Mitigation: phased
ordering, conservative default, verify on target disk before raising.

## Estimates

P1+P4 → ~20s. +P2 (rename-only) → ~12s. +P3 → ~8–10s ≈ `cp`.

## Status

Approved scope: FULL P1–P4 (user). Order: P4 → P1 → P2 → P3,
measure after each. Implementation complete.

Measured rounds: P4 55.893s, P1 30.161s, P2 27.059s, P3 20.186s. All rounds passed recursive diff verification. The 8–10s estimate was not reached; P3 remains at the conservative default of 2 workers pending disk-specific testing.
