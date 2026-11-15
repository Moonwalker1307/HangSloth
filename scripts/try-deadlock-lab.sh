#!/usr/bin/env bash
# Runs every DeadlockLab scenario (samples/DeadlockLab), points HangSloth at the hung
# process and compares what it found with what the scenario expects.
#
#   scripts/try-deadlock-lab.sh [scenario...]       default: all scenarios
#   VERBOSE=1 scripts/try-deadlock-lab.sh ...       also print HangSloth's full report
#
# Database scenarios need PostgreSQL; set DEADLOCKLAB_PG to an Npgsql connection string
# (default Host=localhost;Username=postgres;Password=postgres;Database=postgres), or
# SKIP_DB=1 to skip them. Dumps are captured in memory by HangSloth and never written.
set -uo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
work=${WORK_DIR:-$(mktemp -d)}

echo "Building HangSloth and DeadlockLab..."
dotnet build "$repo_root/src/HangSloth.Cli" -c Release -o "$work/hangsloth" -v quiet -nologo > /dev/null || exit 1
dotnet build "$repo_root/samples/DeadlockLab" -c Release -o "$work/lab" -v quiet -nologo > /dev/null || exit 1

mapfile -t rows < <(dotnet "$work/lab/DeadlockLab.dll" --list)
wanted=("$@")
pass=0; fail=0; skipped=0
printf '\n%-26s %-9s %-8s %-28s %s\n' SCENARIO AREA KIND EXPECTED FOUND
for row in "${rows[@]}"; do
  IFS=$'\t' read -r name area difficulty expected story <<< "$row"
  [ "$expected" = - ] && expected=
  if [ ${#wanted[@]} -gt 0 ] && [[ ! " ${wanted[*]} " == *" $name "* ]]; then continue; fi
  if [ "$area" = Database ] && [ -n "${SKIP_DB:-}" ]; then skipped=$((skipped+1)); continue; fi

  log="$work/$name.log"
  dotnet "$work/lab/DeadlockLab.dll" "$name" > "$log" 2>&1 &
  app=$!
  for _ in $(seq 150); do grep -q '^READY$' "$log" && break; kill -0 $app 2>/dev/null || break; sleep 0.2; done
  if ! grep -q '^READY$' "$log"; then
    printf '%-26s %-9s %-8s %-28s %s\n' "$name" "$area" "$difficulty" "${expected:--}" "ERROR: never became ready (see $log)"
    kill -9 $app 2>/dev/null; fail=$((fail+1)); continue
  fi

  "$work/hangsloth/hangsloth" "$app" --json > "$work/$name.json" 2>"$work/$name.err"
  json=$(cat "$work/$name.json")
  found=$(printf '%s' "$json" | python3 -c '
import json, sys
try:
    r = json.load(sys.stdin)
except Exception:
    print("ERROR"); sys.exit()
ids = [f["id"] for f in r["findings"] if f["severity"].lower() in ("warning", "critical")]
print(",".join(dict.fromkeys(ids)))')
  "$work/hangsloth/hangsloth" "$app" --no-color > "$work/$name.txt" 2>/dev/null
  [ -n "${VERBOSE:-}" ] && cat "$work/$name.txt"
  kill -9 $app 2>/dev/null; wait $app 2>/dev/null

  ok=yes
  for id in ${expected//,/ }; do [[ ",$found," == *",$id,"* ]] || ok=no; done
  [ -z "$expected" ] && [ -n "$found" ] && ok=no
  [ $ok = yes ] && pass=$((pass+1)) || fail=$((fail+1))
  printf '%-26s %-9s %-8s %-28s %s %s\n' "$name" "$area" "$difficulty" "${expected:--}" "${found:--}" "$([ $ok = yes ] && echo '✔' || echo '✖')"
done
echo
echo "$pass caught, $fail missed or wrong, $skipped skipped. Logs in $work"
