#!/usr/bin/env bash
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
DEPLOY=/opt/tictack

sudo systemctl disable --now tictack 2>/dev/null || true
if sudo systemctl is-active --quiet tictack; then
  echo "ERROR: tictack did not stop cleanly; refusing to remove live files."
  exit 1
fi
sudo rm -f /etc/systemd/system/tictack.service
sudo systemctl daemon-reload

if [ -d "$DEPLOY" ]; then
  sudo find "$DEPLOY" -xdev -mindepth 1 ! -name 'config.yaml' -delete
fi

rm -f "$DIR"/TicTackSv "$DIR"/TicTackSv.dll "$DIR"/*.dll "$DIR"/*.runtimeconfig.json "$DIR"/*.deps.json "$DIR"/*.pdb
echo "TicTack service removed. $DEPLOY/config.yaml kept."
