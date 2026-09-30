#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
name="${NEEEVA_LEARNING_NAME:-neeeva-spark-learning}"
image="${NEEEVA_LEARNING_IMAGE:-neeeva-spark-learning:local-v1}"
port="${SINGING_LEARNING_PORT:-9884}"
device="${SINGING_LEARNING_DEVICE:-cpu}"
threads="${SINGING_LEARNING_TORCH_THREADS:-2}"
[[ "$name" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ && "$port" =~ ^[0-9]+$ && "$threads" =~ ^[1-4]$ ]]
[[ "$device" = cpu || "$device" = cuda ]]
test -f "$root/.neeeva-learning-owner" || { echo 'Missing learning deployment ownership marker' >&2; exit 2; }
owner() {
  test "$(docker inspect --format '{{index .Config.Labels "neeeva.owner"}}' "$name")" = singing-learning
  test "$(docker inspect --format '{{index .Config.Labels "neeeva.root"}}' "$name")" = "$root"
}
case "${1:-status}" in
  build)
    args=()
    if test -n "${NEEEVA_BUILD_PROXY:-}"; then
      args=(--build-arg "HTTP_PROXY=$NEEEVA_BUILD_PROXY" --build-arg "HTTPS_PROXY=$NEEEVA_BUILD_PROXY")
    fi
    docker build --network host "${args[@]}" -f "$root/Dockerfile" -t "$image" "$root"
    ;;
  verify)
    docker run --rm --network none -e SINGING_LEARNING_DEVICE=cpu -v "$root:$root:ro" -w "$root" "$image" "$root/run.py" --verify
    ;;
  start)
    if docker container inspect "$name" >/dev/null 2>&1; then
      owner || { echo 'Refusing an unrelated container' >&2; exit 2; }
      docker start "$name" >/dev/null
    else
      if ss -H -ltn "sport = :$port" | grep -q .; then echo "Port $port occupied; owner unchanged" >&2; exit 2; fi
      mkdir -p "$root/data"
      gpu=(); if test "$device" = cuda; then gpu=(--gpus all); fi
      docker run -d --name "$name" --label neeeva.owner=singing-learning --label "neeeva.root=$root" \
        --restart unless-stopped --network host "${gpu[@]}" --shm-size 1g \
        -e "SINGING_LEARNING_DEVICE=$device" -e "SINGING_LEARNING_PORT=$port" \
        -e "SINGING_LEARNING_TORCH_THREADS=$threads" -e "OMP_NUM_THREADS=$threads" \
        -e "OPENBLAS_NUM_THREADS=$threads" -e "MKL_NUM_THREADS=$threads" \
        -v "$root:$root:ro" -v "$root/data:$root/data" -w "$root" "$image" "$root/run.py"
    fi
    ;;
  stop) if docker container inspect "$name" >/dev/null 2>&1; then owner && docker stop --time 120 "$name"; fi ;;
  logs) owner && docker logs --tail 80 "$name" ;;
  health) curl --noproxy '*' --fail --silent --show-error --max-time 10 "http://127.0.0.1:$port/health" ;;
  status)
    docker ps -a --filter "name=^/${name}$" --format '{{.Names}} {{.Status}} {{.Image}}'
    curl --noproxy '*' --silent --show-error --max-time 5 "http://127.0.0.1:$port/health" || true
    ;;
  *) echo 'Usage: manage.sh build|verify|start|stop|health|status|logs' >&2; exit 2 ;;
esac
