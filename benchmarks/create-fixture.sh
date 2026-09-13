#!/usr/bin/env bash
set -euo pipefail

root=${1:-/home/user/sync-bench}
if [[ "$root" == "/" || "$root" == "/home" || "$root" == "/tmp" ]]; then
    printf 'Refusing unsafe fixture root: %s\n' "$root" >&2
    exit 1
fi

src="$root/src"
dst="$root/dst"
rm -rf "$src" "$dst" "$root/state" "$root/archive"
rm -f "$root/tictack.log"
mkdir -p "$src/small" "$src/medium" "$src/large" "$dst"

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
