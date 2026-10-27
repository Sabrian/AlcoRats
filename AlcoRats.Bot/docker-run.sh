#!/usr/bin/env bash
# Run AlcoRats.Bot Release container (foreground by default — console logs stream here)
# Usage:
#   ./AlcoRats.Bot/docker-run.sh
#   DETACH=1 ./AlcoRats.Bot/docker-run.sh
#   TAG=alcorats-bot:latest NAME=alcorats-bot ./AlcoRats.Bot/docker-run.sh
#   CONFIG=./configuration.json ./AlcoRats.Bot/docker-run.sh
#   REBUILD=1 ./AlcoRats.Bot/docker-run.sh

set -euo pipefail

TAG="${TAG:-alcorats-bot:latest}"
NAME="${NAME:-alcorats-bot}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="${DATA_DIR:-${SCRIPT_DIR}/data}"
DETACH="${DETACH:-0}"
REBUILD="${REBUILD:-0}"
CONFIG="${CONFIG:-}"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is not available in PATH" >&2
  exit 1
fi

if [[ "${REBUILD}" == "1" ]]; then
  "${SCRIPT_DIR}/docker-build.sh" "${TAG}"
fi

mkdir -p "${DATA_DIR}/chromium-profile" "${DATA_DIR}/logs"

if docker ps -a --format '{{.Names}}' | grep -qx "${NAME}"; then
  echo "Removing existing container: ${NAME}"
  docker rm -f "${NAME}" >/dev/null
fi

ARGS=(
  run
  --name "${NAME}"
  --init
  --shm-size 1g
  --restart unless-stopped
  -e ChromiumExecutablePath=/opt/google/chrome/chrome
  -e JitsiUserDataDir=/data/chromium-profile
  -e JitsiRemoteDebuggingPort=9333
  -e JitsiHeadless=true
  -e LiteDbPath=/data/data.db
  -e LogDirectory=/data/logs
  -e DBUS_SESSION_BUS_ADDRESS=/dev/null
  -e DBUS_SYSTEM_BUS_ADDRESS=/dev/null
  -p 9333:9333
  -v "${DATA_DIR}:/data"
)

if [[ -n "${CONFIG}" ]]; then
  ARGS+=(-v "$(cd "$(dirname "${CONFIG}")" && pwd)/$(basename "${CONFIG}"):/app/configuration.json:ro")
elif [[ -f "${SCRIPT_DIR}/configuration.json" ]]; then
  ARGS+=(-v "${SCRIPT_DIR}/configuration.json:/app/configuration.json:ro")
elif [[ -f "${DATA_DIR}/configuration.json" ]]; then
  ARGS+=(-v "${DATA_DIR}/configuration.json:/app/configuration.json:ro")
fi

if [[ "${DETACH}" == "1" ]]; then
  ARGS+=(-d)
else
  ARGS+=(-it)
fi

ARGS+=("${TAG}")

echo "Image:     ${TAG}"
echo "Container: ${NAME}"
echo "Data dir:  ${DATA_DIR}"
echo

docker "${ARGS[@]}"

if [[ "${DETACH}" == "1" ]]; then
  echo
  echo "Started in background. Logs: docker logs -f ${NAME}"
fi
