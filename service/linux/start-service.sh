#!/usr/bin/env bash
set -e
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo: sudo $0"; exit 1; }
systemctl enable --now tictack
systemctl status tictack --no-pager
