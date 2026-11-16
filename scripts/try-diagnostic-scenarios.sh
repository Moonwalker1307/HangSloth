#!/usr/bin/env bash
# Runs HangSloth against Microsoft's DiagnosticScenarios sample (dotnet/samples, MIT):
# the app used in the official "debug a deadlock" and "debug thread pool starvation" tutorials.
# Nothing is committed: the sample is cloned and built in a temp folder, and dumps are not kept.
#
#   scripts/try-diagnostic-scenarios.sh [scenario...]
#
# Scenarios: deadlock, taskwait, tasksleepwait, taskasyncwait (the healthy control). Default: all.
# Needs the .NET 10 SDK, git and curl. Linux or macOS (works with bash 3.2, the stock macOS one).
# The exit status is 0 when every scenario gave the expected result (2 for the three hanging
# scenarios, 0 for the healthy control) and 1 otherwise. PORT sets the port (default 5055).
set -euo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
work=${WORK_DIR:-$(mktemp -d)}
port=${PORT:-5055}
scenarios=("${@:-deadlock taskwait tasksleepwait taskasyncwait}")
read -r -a scenarios <<< "${scenarios[*]}"

echo "Building HangSloth..."
dotnet build "$repo_root/src/HangSloth.Cli" -c Release -o "$work/hangsloth" -v quiet -nologo > /dev/null

if [ ! -d "$work/samples" ]; then
  echo "Fetching DiagnosticScenarios from dotnet/samples..."
  git clone -q --depth 1 --filter=blob:none --sparse https://github.com/dotnet/samples.git "$work/samples"
  git -C "$work/samples" sparse-checkout set core/diagnostics/DiagnosticScenarios
fi
app_src="$work/samples/core/diagnostics/DiagnosticScenarios"
# The sample targets net9.0; HangSloth only needs a runtime it can read, so build it on .NET 10.
sed -i.bak -E 's#<TargetFramework>[^<]+#<TargetFramework>net10.0#' "$app_src/DiagnosticScenarios.csproj"
dotnet build "$app_src" -c Debug -o "$work/app" -v quiet -nologo > /dev/null

load_pids=()
app=
stop_load() {
  local p
  if [ ${#load_pids[@]} -gt 0 ]; then for p in "${load_pids[@]}"; do kill "$p" 2>/dev/null || true; done; fi
  load_pids=()
}
# Also on Ctrl+C or a kill: no load generator and no app may be left running.
stop_all() {
  stop_load
  if [ -n "$app" ]; then kill -9 "$app" 2>/dev/null || true; wait "$app" 2>/dev/null || true; app=; fi
}

run() {
  local name=$1 concurrency=$2 settle=$3 expected=$4 exit_code=0 ready=
  echo
  echo "=================== $name ==================="
  if curl -s -o /dev/null -m 2 "http://127.0.0.1:$port/"; then
    echo "Something already answers on port $port; set PORT to a free port." >&2
    return 1
  fi
  ASPNETCORE_URLS="http://127.0.0.1:$port" "$work/app/DiagnosticScenarios" > "$work/$name.app.log" 2>&1 &
  app=$!
  for _ in $(seq 50); do
    if curl -s -o /dev/null "http://127.0.0.1:$port/api/values"; then ready=1; break; fi
    kill -0 "$app" 2>/dev/null || break
    sleep 0.2
  done
  if [ -z "$ready" ]; then
    echo "The sample did not start; its log ($work/$name.app.log):" >&2
    tail -n 20 "$work/$name.app.log" >&2
    stop_all
    return 1
  fi

  # Starvation only shows under load, like the bombardier step in Microsoft's tutorial.
  for _ in $(seq "$concurrency"); do
    ( while true; do curl -s -o /dev/null -m 60 "http://127.0.0.1:$port/api/diagscenario/$name" || true; done ) &
    load_pids+=($!)
  done
  sleep "$settle"

  "$work/hangsloth/hangsloth" "$app" || exit_code=$?
  stop_all
  if [ "$exit_code" -ne "$expected" ]; then
    echo "UNEXPECTED: hangsloth exited with $exit_code, expected $expected." >&2
    return 1
  fi
}

trap stop_all EXIT
trap 'stop_all; exit 130' INT TERM
failed=0
for s in "${scenarios[@]}"; do
  case $s in
    deadlock) run deadlock 1 12 2 || failed=$((failed+1)) ;;
    taskwait|tasksleepwait) run "$s" 200 20 2 || failed=$((failed+1)) ;;
    taskasyncwait) run "$s" 200 20 0 || failed=$((failed+1)) ;;
    *) echo "unknown scenario: $s" >&2; exit 1 ;;
  esac
done
echo
echo "Work folder: $work"
[ "$failed" -eq 0 ] || { echo "$failed scenario(s) did not give the expected result." >&2; exit 1; }
