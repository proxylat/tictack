Corpus recovery (offline, no network): boot this stick, you land here.
1. Find your disk: Corpus\Corpus.exe --list-disks (note number + size).
2. Find your snapshot: this stick (Corpus\store) or a second disk via --store <path>.
3. Plan first: --restore --snapshot GEN --target DISK# --confirm-type GUESS --dry-run shows the want-TOKEN.
4. Restore: same command with the right --confirm-type TOKEN (typed, never stored).
5. Verify re-reads the whole target: wall time is ~2x sequential device speed.
6. Boot disk targets also need --allow-boot-disk; BitLocker-locked: manage-bde -unlock first.
7. System volume restored? Run: bcdboot W:\Windows /s S: /f UEFI (fix letters first).
8. Physical possession of this stick = access to the store (id.key travels with it).
9. If verify reports offsets, do NOT boot it: re-run restore; --skip-verify is last resort.
10. Whole disk: --restore-disk --layout GEN --target DISK# --confirm-type TOKEN restores every non-skipped extent at its recorded offset (MSR gaps untouched, target table must already exist).
11. Bare disk first: same command with --dry-run prints the diskpart script and writes <target>.diskpart.txt; review and apply it by hand (v1 never partitions automatically), then run the real restore.
