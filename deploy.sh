#!/usr/bin/env bash
# One-shot deploy + run for the OPA / FHIR sensitive-logging demo (Linux/macOS).
# Mirrors deploy.ps1; see that file for documentation.

set -euo pipefail
cd "$(dirname "$0")"

ACTION="${1:-up}"
NOCACHE="${2:-}"
CACHE_ARG=""
if [[ "${NOCACHE}" == "--no-cache" ]]; then CACHE_ARG="--no-cache"; fi

require_docker() {
  if ! docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
    echo "Docker is not running. Start it and try again." >&2
    exit 1
  fi
}

build()  {
  echo "==> Building Service B image ${CACHE_ARG}"
  docker compose build ${CACHE_ARG} serviceb
  echo "==> Building Service A image ${CACHE_ARG}"
  docker compose --profile sender build ${CACHE_ARG} servicea
}

up_bg()  {
  echo "==> Starting OPA + Service B in the background"
  docker compose up -d opa serviceb
}

run_a()  {
  echo "==> Sending sample Patient via Service A (one-shot)"
  docker compose --profile sender run --rm servicea
}

show_logs() {
  echo "==> Last masked log lines from Service B"
  docker compose logs --tail=80 serviceb | grep -E 'Received FHIR|Resource =' || true
}

down()   {
  echo "==> Stopping containers"
  docker compose --profile sender down
}

clean()  {
  echo "==> Removing local images and build cache"
  docker compose --profile sender down --rmi local -v
  docker builder prune -f >/dev/null
}

require_docker

case "${ACTION}" in
  build) build ;;
  run)   run_a; show_logs ;;
  logs)  show_logs ;;
  down)  down ;;
  clean) clean ;;
  up)
    build
    up_bg
    sleep 2
    run_a
    show_logs
    echo
    echo "Stack is running. Useful next commands:"
    echo "  ./deploy.sh run    # resend the patient"
    echo "  ./deploy.sh logs   # tail masked logs"
    echo "  ./deploy.sh down   # stop everything"
    ;;
  *)
    echo "Usage: ./deploy.sh [up|build|run|logs|down|clean] [--no-cache]" >&2
    exit 2
    ;;
esac
