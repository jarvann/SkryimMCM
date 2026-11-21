#!/usr/bin/env bash
# Print each new "## Tester" heading appended to the shared dev/Tester notes file (Downloads\skylink-dev-tester.md).
# For a Claude Code Monitor: one stdout line per Tester reply. Polls every 5 s (inotify doesn't see Windows-side writes).
f="${1:-/mnt/c/Users/cory/Downloads/skylink-dev-tester.md}"
count() { local n; n=$(grep -c '^## Tester' "$f" 2>/dev/null); echo "${n:-0}"; }   # grep -c already prints 0 on no match
seen=$(count)
while true; do
  n=$(count)
  if [ "$n" -gt "$seen" ]; then grep '^## Tester' "$f" | tail -n $((n - seen)); seen=$n; fi
  sleep 5
done
