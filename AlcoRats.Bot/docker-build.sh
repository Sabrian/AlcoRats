#!/usr/bin/env bash
# Build Release Docker image for AlcoRats.Bot
# Usage:
#   ./AlcoRats.Bot/docker-build.sh
#   ./AlcoRats.Bot/docker-build.sh alcorats-bot:1.0.0

set -euo pipefail

TAG="${1:-alcorats-bot:latest}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
DOCKERFILE="${SCRIPT_DIR}/Dockerfile"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is not available in PATH" >&2
  exit 1
fi

echo "Repo root:  ${REPO_ROOT}"
echo "Dockerfile: ${DOCKERFILE}"
echo "Tag:        ${TAG}"
echo

docker build \
  -f "${DOCKERFILE}" \
  -t "${TAG}" \
  --build-arg BUILD_CONFIGURATION=Release \
  "${REPO_ROOT}"

echo
echo "OK: image built as ${TAG}"
echo "Run: ./AlcoRats.Bot/docker-run.sh"
