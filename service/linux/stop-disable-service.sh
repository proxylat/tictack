#!/usr/bin/env bash
set -e
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo: sudo $0"; exit 1; }
systemctl disable --now tictack
echo "tictack stopped and disabled."
