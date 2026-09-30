#!/usr/bin/env bash
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$DIR/../../benchmarks/bin" "$DIR/../../benchmarks/obj" \
  "$DIR/../../src/bin" "$DIR/../../src/obj" \
  "$DIR/../../tests/bin" "$DIR/../../tests/obj" \
  "$DIR/../../tests/CrashWorker/bin" "$DIR/../../tests/CrashWorker/obj" \
  /var/lib/tictack
