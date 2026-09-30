#!/usr/bin/env bash
set -euo pipefail
ACTION="${1:-status}"
ASR_ROOT="${2:-/home/spark_root_001/neeeva-voice/asr}"
[[ "$ASR_ROOT" == /home/*/neeeva-voice/asr ]] || { echo 'Unexpected ASR root'; exit 2; }
RUNTIME="$ASR_ROOT/.runtime"
PIDFILE="$RUNTIME/asr.pid"
owned_pid() {
    test -f "$PIDFILE" || return 1
    ASR_PID="$(cat "$PIDFILE")"
    [[ "$ASR_PID" =~ ^[0-9]+$ ]] || return 1
    test -r "/proc/$ASR_PID/cmdline" || return 1
    python3 - "$ASR_PID" "$ASR_ROOT" <<'PY'
import pathlib,sys
args=pathlib.Path('/proc/'+sys.argv[1]+'/cmdline').read_bytes().split(b'\0')
expected=(sys.argv[2]+'/spark/run.py').encode()
raise SystemExit(0 if expected in args else 1)
PY
}
case "$ACTION" in
    status)
        if owned_pid; then echo "ASR owned process running pid=$ASR_PID"; else echo 'ASR owned process not running'; fi
        ;;
    start)
        if owned_pid; then echo "ASR reused pid=$ASR_PID"; exit 0; fi
        test -x "$ASR_ROOT/.venv/bin/python"
        test -f "$ASR_ROOT/spark/run.py"
        python3 - <<'PY'
import socket
with socket.socket() as s:
 s.bind(('0.0.0.0',9881))
PY
        mkdir -p "$RUNTIME"
        nohup "$ASR_ROOT/.venv/bin/python" -u "$ASR_ROOT/spark/run.py" --root "$ASR_ROOT" > "$RUNTIME/asr.out.log" 2> "$RUNTIME/asr.err.log" < /dev/null &
        echo "$!" > "$PIDFILE"
        echo "ASR started pid=$(cat "$PIDFILE"); health still requires acceptance"
        ;;
    stop)
        if owned_pid; then kill -- "$ASR_PID"; echo "ASR SIGTERM sent pid=$ASR_PID"; else echo 'No matching ASR process to stop'; fi
        ;;
    *) echo 'Expected status/start/stop'; exit 2 ;;
esac
