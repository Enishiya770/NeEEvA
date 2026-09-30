#!/usr/bin/env bash
# Explicit future deployment step; never called by inventory/preparation.
set -euo pipefail
ASR_ROOT="${1:-/home/spark_root_001/neeeva-voice/asr}"
WHEELHOUSE="${2:-}"
[[ "$ASR_ROOT" == /home/*/neeeva-voice/asr ]] || { echo 'Unexpected ASR root'; exit 2; }
[[ "$(uname -m)" == aarch64 ]] || { echo 'Expected Linux aarch64'; exit 2; }
for tool in python3 gcc g++ cmake make; do command -v "$tool" >/dev/null; done
python3 -c 'import sys; assert sys.version_info[:2] == (3,12), "Expected Python 3.12"'
test -f "$ASR_ROOT/spark/requirements-spark.txt"
test ! -e "$ASR_ROOT/.venv" || { echo 'Refusing to modify an existing ASR venv; review it explicitly'; exit 2; }
python3 -m venv "$ASR_ROOT/.venv"
if [[ -n "$WHEELHOUSE" ]]; then
    test -d "$WHEELHOUSE"
    # Include every transitive dependency and pyopenjtalk build dependency. Missing
    # assets fail explicitly; offline setup never silently reaches another index.
    export PIP_NO_INDEX=1
    export PIP_FIND_LINKS="$WHEELHOUSE"
    "$ASR_ROOT/.venv/bin/python" -m pip install torch==2.11.0+cu130 torchaudio==2.11.0+cu130
else
    "$ASR_ROOT/.venv/bin/python" -m pip install torch==2.11.0+cu130 torchaudio==2.11.0+cu130 --index-url https://download.pytorch.org/whl/cu130
fi
# Explicit origin avoids inheriting a host-wide mirror. PIP_NO_INDEX still wins
# for the deliberate offline wheelhouse branch above.
"$ASR_ROOT/.venv/bin/python" -m pip install -r "$ASR_ROOT/spark/requirements-spark.txt" -c "$ASR_ROOT/spark/constraints-spark.txt" --index-url https://pypi.org/simple
"$ASR_ROOT/.venv/bin/python" -m pip check
"$ASR_ROOT/.venv/bin/python" -m pip freeze > "$ASR_ROOT/asr-installed.txt"
echo 'ASR environment prepared. No service was started.'
