#!/usr/bin/env bash
# Save alcorats-bot image to AlcoRats.Bot/docker/
# Usage:
#   ./AlcoRats.Bot/docker-save.sh
#   ./AlcoRats.Bot/docker-save.sh alcorats-bot:1.0.0

set -euo pipefail

TAG="${1:-alcorats-bot:latest}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/docker"
SAFE_NAME="$(echo "${TAG}" | tr ':/\\' '-')"
OUT_FILE="${OUT_DIR}/${SAFE_NAME}.tar"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is not available in PATH" >&2
  exit 1
fi

if ! docker info >/dev/null 2>&1; then
  echo "Docker daemon is not running. Start Docker, then retry." >&2
  exit 1
fi

if ! docker image inspect "${TAG}" >/dev/null 2>&1; then
  echo "Image '${TAG}' not found. Build it first: ./docker-build.sh" >&2
  exit 1
fi

mkdir -p "${OUT_DIR}"

echo "Tag:    ${TAG}"
echo "Output: ${OUT_FILE}"
echo

docker save -o "${OUT_FILE}" "${TAG}"

SIZE_MB="$(du -m "${OUT_FILE}" | cut -f1)"
echo
echo "OK: saved ${TAG} -> ${OUT_FILE} (${SIZE_MB} MB)"
echo "Load: docker load -i ${OUT_FILE}"
