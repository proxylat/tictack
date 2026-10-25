#!/usr/bin/env bash
# Build + install TicTack as a systemd service. Lanes: default (system
# dotnet = framework-dependent, else JIT), --jit,
# --aot (needs clang + zlib headers; must run on this Linux host).
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$DIR/../.."
EXE="$DIR/TicTackSv"
CFG="$DIR/config.yaml"
EXAMPLE="$DIR/config_linux.yaml.example"
TARGET_DIR="/opt/tictack"
SERVICE_NAME="tictack"

MODE="auto"
if [ "${1:-}" = "--jit" ]; then MODE="jit";
elif [ "${1:-}" = "--aot" ]; then MODE="aot";
elif [ -n "${1:-}" ]; then
  echo "Usage: $0 [--jit | --aot]"
  exit 1
fi

DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ]; then
  DOTNET="$ROOT/.dotnet/dotnet"
  export DOTNET_ROOT="$ROOT/.dotnet"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  [ -x "$DOTNET" ] || { echo "ERROR: no dotnet found — install one or add the repo-local SDK."; exit 1; }
fi

if [ "$MODE" = "aot" ]; then
  command -v clang >/dev/null 2>&1 || { echo "ERROR: --aot needs clang (e.g. sudo apt install clang zlib1g-dev)."; exit 1; }
  [ -f /usr/include/zlib.h ] || { echo "ERROR: --aot needs zlib headers (e.g. sudo apt install zlib1g-dev)."; exit 1; }
  echo "Publishing NativeAOT..."
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -p:PublishProfile=Aot -o "$DIR/"
elif [ "$MODE" = "jit" ]; then
  echo "Publishing JIT single-file (runtime bundled, compiles at startup)..."
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -r linux-x64 --self-contained true -o "$DIR/"
else
  # A user-local SDK is invisible to the root-run service, so only a
  # system-wide dotnet earns framework-dependent.
  SELF_CONTAINED=1
  DOTNET_REAL="$(readlink -f "$DOTNET" 2>/dev/null || echo "$DOTNET")"
  case "$DOTNET_REAL" in
    /usr/*|/opt/*|/snap/*) SELF_CONTAINED=0 ;;
  esac
  if [ "$SELF_CONTAINED" = 1 ]; then
    echo "Publishing JIT (no system .NET runtime visible at '$DOTNET')."
    "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -r linux-x64 --self-contained true -o "$DIR/"
  else
    echo "Publishing framework-dependent..."
    "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -o "$DIR/"
  fi
fi

[ -x "$EXE" ] || { echo "ERROR: publish did not produce $EXE"; exit 1; }

if [ ! -f "$CFG" ]; then
  cp "$EXAMPLE" "$CFG"
  echo "Created $CFG from the example - edit it for this machine, then run $0 again."
  exit 1
fi

# Install the service (always; needs root from here on).
if [ "$(id -u)" -ne 0 ]; then
  echo "ERROR: install needs root — re-run with sudo."
  exit 1
fi

id -u "$SERVICE_NAME" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_NAME"
usermod -aG systemd-journal "$SERVICE_NAME" || true

mkdir -p "$TARGET_DIR"
rsync -a --delete --exclude config.yaml --exclude 'tictack.service' "$DIR/" "$TARGET_DIR/"
cp -n "$CFG" "$TARGET_DIR/config.yaml"
chown -R "$SERVICE_NAME":"$SERVICE_NAME" "$TARGET_DIR"
chmod 755 "$TARGET_DIR"
chmod 750 "$TARGET_DIR/TicTackSv"

sed "s|{{TARGET_DIR}}|$TARGET_DIR|g" "$DIR/tictack.service" > "/etc/systemd/system/$SERVICE_NAME.service"

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
systemctl status "$SERVICE_NAME" --no-pager
echo "Installed. Config: $TARGET_DIR/config.yaml"
echo "Logs: journalctl -u $SERVICE_NAME -f"
