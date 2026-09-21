#!/usr/bin/env bash
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT="$DIR/../.."
DEPLOY=/opt/tictack

if [ ! -f "$DIR/config.yaml" ]; then
  echo "ERROR: config.yaml not found. Copy config_linux.yaml.example to config.yaml and edit it."
  exit 1
fi

DOTNET="$(command -v dotnet || true)"
# A user-local SDK (e.g. ~/.dotnet) is visible to this shell but NOT to the
# root-run systemd service, so publishing framework-dependent against it
# produces an install that cannot start. Only trust a system-wide runtime.
SELF_CONTAINED=1
if [ -n "$DOTNET" ]; then
  DOTNET_REAL="$(readlink -f "$DOTNET" 2>/dev/null || echo "$DOTNET")"
  case "$DOTNET_REAL" in
    /usr/*|/opt/*|/snap/*) SELF_CONTAINED=0 ;;
  esac
fi
if [ -z "$DOTNET" ]; then
  DOTNET="$ROOT/.dotnet/dotnet"
  export DOTNET_ROOT="$ROOT/.dotnet"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  [ -x "$DOTNET" ] || { echo "ERROR: no dotnet found — install one or add the repo-local SDK."; exit 1; }
fi
if [ "$SELF_CONTAINED" = 1 ]; then
  echo "Publishing self-contained (no service-visible system .NET runtime at '$DOTNET')."
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -r linux-x64 --self-contained true -o "$DIR/"
else
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -o "$DIR/"
fi

sudo systemctl stop tictack 2>/dev/null || true
if sudo systemctl is-active --quiet tictack; then
  echo "ERROR: tictack did not stop cleanly; refusing to replace the live installation."
  exit 1
fi

STAGE="$DEPLOY.new.$$"
sudo rm -rf "$STAGE"
sudo mkdir -p "$STAGE"
sudo cp "$DIR"/TicTackSv "$DIR"/TicTackSv.dll "$DIR"/*.deps.json "$DIR"/*.runtimeconfig.json "$STAGE/"
sudo cp "$DIR"/*.dll "$DIR"/*.so "$STAGE/"
if sudo test -f "$DEPLOY/config.yaml"; then
  sudo cp "$DEPLOY/config.yaml" "$STAGE/config.yaml"
else
  sudo cp "$DIR/config.yaml" "$STAGE/config.yaml"
fi
sudo chmod +x "$STAGE/TicTackSv"
sudo rm -rf "$DEPLOY.old"
if sudo test -d "$DEPLOY"; then sudo mv "$DEPLOY" "$DEPLOY.old"; fi
sudo mv "$STAGE" "$DEPLOY"
sudo rm -rf "$DEPLOY.old"

rm -f "$DIR"/TicTackSv "$DIR"/TicTackSv.dll "$DIR"/*.dll "$DIR"/*.so "$DIR"/*.runtimeconfig.json "$DIR"/*.deps.json "$DIR"/*.pdb
rm -rf "$DIR"/runtimes
rm -f "$DIR"/createdump

sudo cp "$DIR/tictack.service" /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now tictack
echo "TicTack service installed in $DEPLOY and started."
