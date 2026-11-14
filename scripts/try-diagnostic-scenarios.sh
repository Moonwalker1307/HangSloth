#!/usr/bin/env bash
# Runs HangSloth against Microsoft's DiagnosticScenarios sample (dotnet/samples, MIT):
# the app used in the official "debug a deadlock" and "debug thread pool starvation" tutorials.
# Nothing is committed: the sample is cloned and built in a temp folder, and dumps are not kept.
#
#   scripts/try-diagnostic-scenarios.sh [scenario...]
#
# Scenarios: deadlock, taskwait, tasksleepwait, taskasyncwait (the healthy control). Default: all.
# Needs the .NET 10 SDK, git and curl. Linux or macOS.
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
stop_load() { for p in "${load_pids[@]}"; do kill "$p" 2>/dev/null || true; done; load_pids=(); }

run() {
  local name=$1 concurrency=$2 settle=$3
  echo
  echo "=================== $name ==================="
  ASPNETCORE_URLS="http://127.0.0.1:$port" "$work/app/DiagnosticScenarios" > "$work/$name.app.log" 2>&1 &
  local app=$!
  for _ in $(seq 50); do curl -s -o /dev/null "http://127.0.0.1:$port/api/values" && break; sleep 0.2; done

  # Starvation only shows under load, like the bombardier step in Microsoft's tutorial.
  for _ in $(seq "$concurrency"); do
    ( while true; do curl -s -o /dev/null -m 60 "http://127.0.0.1:$port/api/diagscenario/$name" || true; done ) &
    load_pids+=($!)
  done
  sleep "$settle"

  "$work/hangsloth/hangsloth" "$app" || true
  stop_load
  kill -9 "$app" 2>/dev/null || true
  wait 2>/dev/null || true
}

trap 'stop_load' EXIT
for s in "${scenarios[@]}"; do
  case $s in
    deadlock) run deadlock 1 12 ;;
    taskwait|tasksleepwait|taskasyncwait) run "$s" 200 20 ;;
    *) echo "unknown scenario: $s" >&2; exit 1 ;;
  esac
done
echo
echo "Work folder: $work"
