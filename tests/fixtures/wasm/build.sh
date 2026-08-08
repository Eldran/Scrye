#!/bin/sh
# Rebuild the checked-in fixture .wasm files. Needs clang with the wasm32 target and
# wasm-ld (Debian/Ubuntu: apt install clang lld). Deterministic given the same clang.
set -e
cd "$(dirname "$0")"
for f in test-plugin abi-v2; do
  clang --target=wasm32-unknown-unknown -O2 -nostdlib -Wl,--no-entry -o "$f.wasm" "$f.c"
done
ls -la *.wasm
