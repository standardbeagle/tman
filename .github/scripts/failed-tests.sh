#!/usr/bin/env bash
# Print the fully qualified names and first message lines of the tests that
# failed in the trx files under the given directory, so the tail of a failed CI
# test step names the failure instead of a wall of skip lines.
#
# Usage: failed-tests.sh <results-dir>
#
# Exits 0 whether or not failures were found (a directory with no trx, or a
# green run, prints nothing and exits 0): the caller owns the step's exit code.
set -uo pipefail

dir="${1:-}"

if [ -z "$dir" ] || [ ! -d "$dir" ]; then
  exit 0
fi

# Cap the summary so it fits the 40-line tail a watcher reads.
limit=15

trx_list=$(find "$dir" -type f -name '*.trx' 2>/dev/null)
[ -z "$trx_list" ] && exit 0

# trx is XML. UnitTestResult carries testName and outcome; a failed result is
# followed by a ResultSummary whose Output/ErrorInfo/Message holds the error.
# Keep document order and print each testName once.
printf '%s\n' "$trx_list" | while IFS= read -r trx; do
  awk -v limit="$limit" '
    function first_line(s) { sub(/\r?\n.*/, "", s); return s }
    /<UnitTestResult/ {
      if (match($0, /testName="[^"]*"/)) {
        name = substr($0, RSTART + 10, RLENGTH - 11)
        outcome = ""
        rest = $0
        if (match(rest, /outcome="[^"]*"/)) {
          outcome = substr(rest, RSTART + 9, RLENGTH - 10)
        }
        if (outcome == "Failed" && name != "" && !(name in seen)) {
          seen[name] = 1
          order[++n] = name
        }
      }
      next
    }
    /<Message>/ && n > 0 && !(order[n] in got) {
      m = $0
      sub(/.*<Message>/, "", m)
      sub(/<\/Message>.*/, "", m)
      got[order[n]] = first_line(m)
    }
    END {
      shown = 0
      for (i = 1; i <= n; i++) {
        if (shown >= limit) { more++; continue }
        printf "FAILED: %s\n", order[i]
        shown++
        if (got[order[i]] != "") printf "  %s\n", got[order[i]]
      }
      if (more > 0) printf "... and %d more failed test(s)\n", more
    }
  ' "$trx" 2>/dev/null
done
