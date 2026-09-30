#!/usr/bin/env bash
set -euo pipefail

# Linux/Spark foreground launcher for the native ARDY HTTP service. The local
# PowerShell launcher remains available on Windows; this file deliberately
# contains no model downloads or silent fallbacks.
ROOT="${NEEEVA_ARDY_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
PYTHON="${NEEEVA_ARDY_PYTHON:-$ROOT/Server/ARDY/.venv/bin/python}"
if [[ ! -x "$PYTHON" ]]; then
  if command -v python3 >/dev/null 2>&1; then
    PYTHON="$(command -v python3)"
  else
    echo "ARDY Python environment is missing: $ROOT/Server/ARDY/.venv/bin/python" >&2
    exit 2
  fi
fi
for vendor in "$ROOT/Server/ARDY/vendor/ardy-693f74d13b3d04a0a22ce127ee79c929dd89756b" \
              "$ROOT/vendor/ardy-693f74d13b3d04a0a22ce127ee79c929dd89756b"; do
  if [[ -d "$vendor" ]]; then
    export PYTHONPATH="$vendor:$ROOT${PYTHONPATH:+:$PYTHONPATH}"
    break
  fi
done
export PYTHONPATH="${PYTHONPATH:-$ROOT}"
exec "$PYTHON" -m Server.ARDY.motion_service.app "$@"
