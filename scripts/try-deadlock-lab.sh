#!/usr/bin/env bash
# Runs every DeadlockLab scenario (samples/DeadlockLab), points HangSloth at the hung
# process and compares what it found with what the scenario expects.
#
#   scripts/try-deadlock-lab.sh [scenario...]       default: all scenarios
#   VERBOSE=1 scripts/try-deadlock-lab.sh ...       also print HangSloth's full report
#
# Database scenarios need PostgreSQL; set DEADLOCKLAB_PG to an Npgsql connection string
# (default Host=localhost;Username=postgres;Password=postgres;Database=postgres), or
# SKIP_DB=1 to skip them. Needs the .NET 10 SDK and python3; works with bash 3.2 (stock macOS).
# Each scenario writes one full dump (100 to 200 MB) to the work folder and deletes it after the
# analysis. The exit status is 0 when every scenario that ran found what it expects, 1 otherwise.
set -uo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
work=${WORK_DIR:-$(mktemp -d)}
app=

# Ctrl+C or a kill must not leave the sample (or its child process) running.
stop_app() {
  [ -n "$app" ] || return 0
  pkill -9 -P "$app" 2>/dev/null
  kill -9 "$app" 2>/dev/null
  wait "$app" 2>/dev/null
  app=
}
trap stop_app EXIT
trap 'stop_app; exit 130' INT TERM

echo "Building HangSloth and DeadlockLab..."
dotnet build "$repo_root/src/HangSloth.Cli" -c Release -o "$work/hangsloth" -v quiet -nologo > /dev/null || exit 1
dotnet build "$repo_root/samples/DeadlockLab" -c Release -o "$work/lab" -v quiet -nologo > /dev/null || exit 1

# Read the scenario list first: a failing --list is an error, and every name asked for must be in it.
list=$(dotnet "$work/lab/DeadlockLab.dll" --list) || { echo "DeadlockLab --list failed" >&2; exit 1; }
[ -n "$list" ] || { echo "DeadlockLab --list printed no scenarios" >&2; exit 1; }
unknown=
for want in "$@"; do
  printf '%s\n' "$list" | cut -f1 | grep -qxF -- "$want" || unknown="$unknown $want"
done
if [ -n "$unknown" ]; then echo "Unknown scenario:$unknown (see --list)" >&2; exit 1; fi

wanted=" $* "
pass=0; fail=0; skipped=0
printf '\n%-28s %-9s %-8s %-28s %s\n' SCENARIO AREA KIND EXPECTED FOUND
# The scenario list is read on descriptor 3 so that the commands in the loop can't consume it.
while IFS= read -r -u 3 row; do
  IFS=$'\t' read -r name area difficulty expected story <<< "$row"
  [ "$expected" = - ] && expected=
  if [ "$wanted" != "  " ] && [[ "$wanted" != *" $name "* ]]; then continue; fi
  if [ "$area" = Database ] && [ -n "${SKIP_DB:-}" ]; then skipped=$((skipped+1)); continue; fi
  if [ "$area" = SqlServer ] && [ -n "${SKIP_SQLSERVER:-}" ]; then skipped=$((skipped+1)); continue; fi

  log="$work/$name.log"
  dotnet "$work/lab/DeadlockLab.dll" "$name" > "$log" 2>&1 < /dev/null &
  app=$!
  for _ in $(seq 150); do grep -q '^READY$' "$log" && break; kill -0 $app 2>/dev/null || break; sleep 0.2; done
  if ! grep -q '^READY$' "$log"; then
    printf '%-28s %-9s %-8s %-28s %s\n' "$name" "$area" "$difficulty" "${expected:--}" "ERROR: never became ready (see $log)"
    stop_app; fail=$((fail+1)); continue
  fi

  # One capture per scenario: keep the dump, stop the sample, and run both reports on the file.
  dump="$work/$name.dmp"
  "$work/hangsloth/hangsloth" "$app" --json --keep-dump "$dump" > "$work/$name.json" 2>"$work/$name.err"
  stop_app
  json=$(cat "$work/$name.json")
  found=$(printf '%s' "$json" | python3 -c '
import json, sys
try:
    r = json.load(sys.stdin)
except Exception:
    print("ERROR"); sys.exit()
ids = [f["id"] for f in r["findings"] if f["severity"].lower() in ("warning", "critical")]
print(",".join(dict.fromkeys(ids)))')
  "$work/hangsloth/hangsloth" "$dump" --no-color > "$work/$name.txt" 2>/dev/null
  rm -f "$dump"
  [ -n "${VERBOSE:-}" ] && cat "$work/$name.txt"

  ok=yes
  for id in ${expected//,/ }; do [[ ",$found," == *",$id,"* ]] || ok=no; done
  [ -z "$expected" ] && [ -n "$found" ] && ok=no
  [ $ok = yes ] && pass=$((pass+1)) || fail=$((fail+1))
  printf '%-28s %-9s %-8s %-28s %s %s\n' "$name" "$area" "$difficulty" "${expected:--}" "${found:--}" "$([ $ok = yes ] && echo '✔' || echo '✖')"
done 3<<< "$list"
echo
echo "$pass caught, $fail missed or wrong, $skipped skipped. Logs in $work"
[ "$fail" -eq 0 ]
