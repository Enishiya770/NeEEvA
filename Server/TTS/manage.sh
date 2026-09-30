#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
name=neeeva-spark-tts
image=neeeva-spark-tts:local-frozen-v1
case "${1:-status}" in
  build)
    expected=sha256:3430d6614a8e2925f34d059af6caf05aff42387326db4d05639a60f10f2654d8
    actual=$(docker image inspect ghcr.io/anemll/dspark-vllm-gx10:0.1.1 --format '{{.Id}}')
    test "$actual" = "$expected" || { echo 'Cached base image identity changed; refusing unreviewed rebuild.' >&2; exit 2; }
    proxy_args=()
    if test -n "${NEEEVA_BUILD_PROXY:-}"; then
      proxy_args=(--build-arg "HTTP_PROXY=$NEEEVA_BUILD_PROXY" --build-arg "HTTPS_PROXY=$NEEEVA_BUILD_PROXY")
    fi
    docker build --network host "${proxy_args[@]}" -f "$root/Dockerfile.spark" -t "$image" "$root"
    ;;
  start)
    if docker container inspect "$name" >/dev/null 2>&1; then
      test "$(docker inspect --format '{{index .Config.Labels "neeeva.owner"}}' "$name")" = tts || exit 2
      docker start "$name" >/dev/null
    else
      if ss -H -ltn sport = :9880 | grep -q .; then echo 'Port 9880 occupied; not stopping its owner.' >&2; exit 2; fi
      docker run -d --name "$name" --label neeeva.owner=tts --restart unless-stopped \
        --gpus all --network host --shm-size 1g \
        -e OMP_NUM_THREADS=1 -e MKL_NUM_THREADS=1 \
        -v "$root:$root" -v "$root/release:$root/release:ro" -w "$root" "$image" "$root/serve.py"
    fi
    ;;
  stop)
    if docker container inspect "$name" >/dev/null 2>&1; then
      test "$(docker inspect --format '{{index .Config.Labels "neeeva.owner"}}' "$name")" = tts || exit 2
      docker stop --time 60 "$name"
    fi
    ;;
  status)
    docker ps -a --filter "name=^/${name}$" --format '{{.Names}} {{.Status}} {{.Image}}'
    curl --silent --show-error --max-time 5 http://127.0.0.1:9880/health || true
    ;;
  logs) docker logs --tail 80 "$name" ;;
  *) echo 'Usage: manage.sh build|start|stop|status|logs' >&2; exit 2 ;;
esac
