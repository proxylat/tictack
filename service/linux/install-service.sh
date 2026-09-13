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
if [ -z "$DOTNET" ] && [ -x "$ROOT/.dotnet/dotnet" ]; then
  DOTNET="$ROOT/.dotnet/dotnet"
  export DOTNET_ROOT="$ROOT/.dotnet"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
fi
if [ -n "$(command -v dotnet || true)" ]; then
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -o "$DIR/"
else
  echo "No system .NET runtime found; publishing self-contained."
  "$DOTNET" publish "$ROOT/src/TicTack.csproj" -c Release -r linux-x64 --self-contained true -o "$DIR/"
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
