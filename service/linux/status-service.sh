#!/usr/bin/env bash
systemctl status tictack --no-pager || true
echo ""
echo "=== Recent logs (journalctl -u tictack) ==="
journalctl -u tictack -n 30 --no-pager
