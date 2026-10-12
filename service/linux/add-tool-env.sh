#!/bin/sh
# add-tool-env.sh <name-or-path> — make a tool resolvable for TicTack jobs
# (the systemd unit runs with a minimal PATH, not your interactive one).
# Symlinks into /usr/local/bin. Restart the service after.
#   add-tool-env.sh restic                 -- locate via command -v, link it
#   add-tool-env.sh /opt/restic            -- link every executable in the dir
#   add-tool-env.sh /opt/restic/restic     -- link this file
set -eu

usage() {
    echo "Usage: $0 <command-name-or-path>"
    echo "  $0 restic               -- locate via command -v, link it"
    echo "  $0 /opt/restic          -- link every executable in the dir"
    echo "  $0 /opt/restic/restic   -- link this file"
    exit 2
}

[ $# -eq 1 ] || usage
ARG=$1
DIRMODE=""

# --- resolve to a file, or flag dir mode ---
case "$ARG" in
    */*)
        if [ -f "$ARG" ]; then
            FILE=$ARG
        elif [ -d "$ARG" ]; then
            DIRMODE=1
        else
            echo "ERROR: path not found: $ARG" >&2
            exit 1
        fi
        ;;
    *)
        FILE=$(command -v "$ARG" || true)
        if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
            echo "ERROR: '$ARG' not found via command -v. Pass its location instead:" >&2
            echo "  $0 /path/to/dir   or   $0 /path/to/$ARG" >&2
            exit 1
        fi
        ;;
esac

# --- already visible to services? (systemd minimal PATH, not ours) ---
SYSPATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
if [ -z "$DIRMODE" ]; then
    base=$(basename "$FILE")
    if PATH=$SYSPATH command -v "$base" >/dev/null 2>&1; then
        echo "Already resolvable on the service PATH: $base -> $(PATH=$SYSPATH command -v "$base")"
        exit 0
    fi
fi

# --- /usr/local/bin needs root ---
if [ "$(id -u)" -ne 0 ]; then
    exec sudo "$0" "$@"
fi

link_one() {
    src=$1
    base=$(basename "$src")
    dest=/usr/local/bin/$base
    if [ -e "$dest" ] && { [ ! -L "$dest" ] || [ "$(readlink "$dest")" != "$src" ]; }; then
        echo "ERROR: $dest already exists and points elsewhere, refusing to overwrite." >&2
        return 1
    fi
    if [ ! -x "$src" ]; then
        echo "ERROR: not executable: $src" >&2
        return 1
    fi
    ln -sf "$src" "$dest"
    echo "Linked $dest -> $src"
}

if [ -n "$DIRMODE" ]; then
    found=0
    for f in "$ARG"/*; do
        [ -f "$f" ] && [ -x "$f" ] || continue
        link_one "$f" || exit 1
        found=1
    done
    if [ "$found" -eq 0 ]; then
        echo "ERROR: no executables in $ARG" >&2
        exit 1
    fi
else
    link_one "$FILE" || exit 1
fi

echo "Restart the service so jobs pick it up (env is snapshotted at start):"
echo "  sudo systemctl restart tictack"
