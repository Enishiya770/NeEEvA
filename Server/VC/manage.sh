#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
name=neeeva-spark-vc
image=neeeva-spark-vc:local-frozen-v1
case "${1:-status}" in
 build)
   expected=sha256:3430d6614a8e2925f34d059af6caf05aff42387326db4d05639a60f10f2654d8
   test "$(docker image inspect ghcr.io/anemll/dspark-vllm-gx10:0.1.1 --format '{{.Id}}')" = "$expected"
   args=()
   if test -n "${NEEEVA_BUILD_PROXY:-}"; then args=(--build-arg "HTTP_PROXY=$NEEEVA_BUILD_PROXY" --build-arg "HTTPS_PROXY=$NEEEVA_BUILD_PROXY"); fi
   docker build --network host "${args[@]}" -f "$root/Dockerfile.spark" -t "$image" "$root"
   ;;
 start)
   if docker container inspect "$name" >/dev/null 2>&1; then
     test "$(docker inspect --format '{{index .Config.Labels "neeeva.owner"}}' "$name")" = vc
     docker start "$name" >/dev/null
   else
     if ss -H -ltn sport = :9882 | grep -q .; then echo '9882 occupied; owner left untouched' >&2; exit 2; fi
     docker run -d --name "$name" --label neeeva.owner=vc --restart unless-stopped \
       --gpus all --network host --shm-size 1g -e OMP_NUM_THREADS=1 -e MKL_NUM_THREADS=1 \
       -v "$root:$root" -w "$root" "$image" "$root/serve.py"
   fi
   ;;
 stop)
   if docker container inspect "$name" >/dev/null 2>&1; then
     test "$(docker inspect --format '{{index .Config.Labels "neeeva.owner"}}' "$name")" = vc
     docker stop --time 30 "$name"
   fi
   ;;
 status)
   docker ps -a --filter "name=^/${name}$" --format '{{.Names}} {{.Status}} {{.Image}}'
   curl --silent --show-error --max-time 5 http://127.0.0.1:9882/health || true
   ;;
 logs) docker logs --tail 80 "$name" ;;
 *) echo 'Usage: manage.sh build|start|stop|status|logs' >&2; exit 2 ;;
esac
