#!/usr/bin/env bash
# MEMSHACK SAVE HOOK
#
# Auto-save every N user messages by blocking the assistant long enough for it
# to write important memory. This mirrors the upstream MemPalace hook flow, but
# defaults to the C# tool command instead of the Python package entrypoint.

set -u

SAVE_INTERVAL="${SAVE_INTERVAL:-15}"
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
    echo "{}"
    exit 0
fi

INPUT="$(cat)"
SESSION_ID="$(echo "$INPUT" | "$PYTHON_BIN" -c "import sys,json; print(json.load(sys.stdin).get('session_id','unknown'))" 2>/dev/null || echo unknown)"
STOP_HOOK_ACTIVE="$(echo "$INPUT" | "$PYTHON_BIN" -c "import sys,json; print(json.load(sys.stdin).get('stop_hook_active', False))" 2>/dev/null || echo false)"
TRANSCRIPT_PATH="$(echo "$INPUT" | "$PYTHON_BIN" -c "import sys,json; print(json.load(sys.stdin).get('transcript_path',''))" 2>/dev/null || echo '')"
TRANSCRIPT_PATH="${TRANSCRIPT_PATH/#\~/$HOME}"

if [ "$STOP_HOOK_ACTIVE" = "True" ] || [ "$STOP_HOOK_ACTIVE" = "true" ]; then
    echo "{}"
    exit 0
fi

if [ -f "$TRANSCRIPT_PATH" ]; then
    EXCHANGE_COUNT="$("$PYTHON_BIN" - "$TRANSCRIPT_PATH" <<'PYEOF'
import json
import sys

count = 0
with open(sys.argv[1], encoding="utf-8") as handle:
    for line in handle:
        try:
            entry = json.loads(line)
        except Exception:
            continue
        message = entry.get("message", {})
        if isinstance(message, dict) and message.get("role") == "user":
            content = message.get("content", "")
            if isinstance(content, str) and "<command-message>" in content:
                continue
            count += 1
print(count)
PYEOF
)"
else
    EXCHANGE_COUNT=0
fi

LAST_SAVE_FILE="$STATE_DIR/${SESSION_ID}_last_save"
LAST_SAVE=0
if [ -f "$LAST_SAVE_FILE" ]; then
    LAST_SAVE="$(cat "$LAST_SAVE_FILE")"
fi

SINCE_LAST=$((EXCHANGE_COUNT - LAST_SAVE))
echo "[$(date '+%H:%M:%S')] Session $SESSION_ID: $EXCHANGE_COUNT exchanges, $SINCE_LAST since last save" >> "$STATE_DIR/hook.log"

if [ "$SINCE_LAST" -ge "$SAVE_INTERVAL" ] && [ "$EXCHANGE_COUNT" -gt 0 ]; then
    echo "$EXCHANGE_COUNT" > "$LAST_SAVE_FILE"
    echo "[$(date '+%H:%M:%S')] TRIGGERING SAVE at exchange $EXCHANGE_COUNT" >> "$STATE_DIR/hook.log"

    if [ -n "$MEMPAL_DIR" ] && [ -d "$MEMPAL_DIR" ]; then
        "$MEMSHACK_COMMAND" mine "$MEMPAL_DIR" --mode "$MEMSHACK_MODE" >> "$STATE_DIR/hook.log" 2>&1 &
    fi

    cat <<'HOOKJSON'
{
  "decision": "block",
  "reason": "AUTO-SAVE checkpoint. Save key topics, decisions, quotes, code, and important memory from this session to MemShack. Use verbatim quotes where helpful, organize it well, then continue."
}
HOOKJSON
else
    echo "{}"
fi
