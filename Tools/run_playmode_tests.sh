#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY_EXECUTABLE="${UNITY_EXECUTABLE:-/Applications/Unity/Hub/Editor/6000.4.6f1/Unity.app/Contents/MacOS/Unity}"
LOG_PATH="${PLAYMODE_TEST_LOG:-$PROJECT_ROOT/Logs/playmode-test.log}"
RESULT_PATH="${PLAYMODE_TEST_RESULTS:-$PROJECT_ROOT/TestResults/playmode-results.xml}"

if [[ ! -x "$UNITY_EXECUTABLE" ]]; then
  echo "Unity executable not found: $UNITY_EXECUTABLE" >&2
  echo "Set UNITY_EXECUTABLE to the Unity 6000.4.6f1 binary and retry." >&2
  exit 127
fi

mkdir -p "$(dirname "$LOG_PATH")" "$(dirname "$RESULT_PATH")"
rm -f "$RESULT_PATH"

echo "Running PlayMode tests with $UNITY_EXECUTABLE"
echo "Log: $LOG_PATH"
echo "Results: $RESULT_PATH"

set +e
# Do not add -quit here. Unity Test Framework exits when the run completes,
# while -quit can stop Unity before PlayMode test execution writes XML results.
"$UNITY_EXECUTABLE" \
  -batchmode \
  -nographics \
  -projectPath "$PROJECT_ROOT" \
  -runTests \
  -testPlatform PlayMode \
  -testResults "$RESULT_PATH" \
  -logFile "$LOG_PATH"
unity_status=$?
set -e

if [[ $unity_status -ne 0 ]]; then
  echo "Unity PlayMode test run failed with exit code $unity_status." >&2
  echo "See log: $LOG_PATH" >&2
  exit "$unity_status"
fi

if [[ ! -s "$RESULT_PATH" ]]; then
  echo "Unity exited successfully but did not write PlayMode results." >&2
  echo "See log: $LOG_PATH" >&2
  exit 1
fi

echo "PlayMode test results written to $RESULT_PATH"
