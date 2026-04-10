#!/usr/bin/env bash
# MEMSHACK PRE-COMPACT HOOK
#
# Emergency save before compaction. This always blocks so the assistant can save
# everything important before context is compressed away.

set -u

STATE_DIR="${STATE_DIR:-$HOME/.mempalace/hook_state}"
MEMSHACK_COMMAND="${MEMSHACK_COMMAND:-mems}"
MEMSHACK_MODE="${MEMSHACK_MODE:-convos}"
MEMPAL_DIR="${MEMPAL_DIR:-}"

mkdir -p "$STATE_DIR"

if command -v python3 >/dev/null 2>&1; then
    PYTHON_BIN="python3"
elif command -v python >/dev/null 2>&1; then
    PYTHON_BIN="python"
else
    cat <<'HOOKJSON'
{
  "decision": "block",
  "reason": "COMPACTION IMMINENT. Save all important topics, decisions, quotes, code, and context from this session to MemShack before compaction proceeds."
}
HOOKJSON
    exit 0
fi

INPUT="$(cat)"
SESSION_ID="$(echo "$INPUT" | "$PYTHON_BIN" -c "import sys,json; print(json.load(sys.stdin).get('session_id','unknown'))" 2>/dev/null || echo unknown)"

echo "[$(date '+%H:%M:%S')] PRE-COMPACT triggered for session $SESSION_ID" >> "$STATE_DIR/hook.log"

if [ -n "$MEMPAL_DIR" ] && [ -d "$MEMPAL_DIR" ]; then
    "$MEMSHACK_COMMAND" mine "$MEMPAL_DIR" --mode "$MEMSHACK_MODE" >> "$STATE_DIR/hook.log" 2>&1
fi

cat <<'HOOKJSON'
{
  "decision": "block",
  "reason": "COMPACTION IMMINENT. Save all important topics, decisions, quotes, code, and context from this session to MemShack before compaction proceeds."
}
HOOKJSON
