#!/usr/bin/env bash
# Run every shipped plugin's harness against the plugin as it is in src/Scrye.App/plugins.
#
#   bash tests/plugins/run.sh            (from anywhere; needs lua5.4 on PATH)
#
# Each *_test.lua loads the real plugin into a fake host and exits non-zero when a check
# fails. This runs them all, prints one line each, and fails if any did - which is what CI
# needs. Run a single harness directly for its FAIL lines: lua5.4 tests/plugins/farmer_test.lua
set -u
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lua="${LUA:-lua5.4}"
command -v "$lua" >/dev/null || { echo "no $lua on PATH (set LUA=... to use another 5.4 binary)"; exit 2; }

failed=()
for t in "$here"/*_test.lua; do
  name="$(basename "$t")"
  out="$("$lua" "$t" 2>&1)"
  code=$?
  if [ $code -eq 0 ]; then
    echo "ok    $name  ($(printf '%s\n' "$out" | tail -n 1))"
  else
    echo "FAIL  $name  (exit $code)"
    # The failing checks; for a crash (no FAIL lines) the error and its traceback.
    fails="$(printf '%s\n' "$out" | grep -E '^FAIL' | head -n 40)"
    if [ -n "$fails" ]; then printf '%s\n' "$fails"; else printf '%s\n' "$out" | tail -n 15; fi | sed 's/^/        /'
    failed+=("$name")
  fi
done

echo
if [ ${#failed[@]} -gt 0 ]; then
  echo "${#failed[@]} harness(es) failed: ${failed[*]}"
  exit 1
fi
echo "all plugin harnesses passed"
