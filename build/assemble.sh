#!/usr/bin/env bash
#
# Script: assemble.sh
# Description: Rebuild the plugin source tree from pristine upstream plus patches and overlay.
# Usage: ./build/assemble.sh [output-dir]
#

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPO_ROOT
UPSTREAM_REPO="${UPSTREAM_REPO:-https://github.com/JPVenson/Jellyfin.Pgsql}"
UPSTREAM_REF="$(tr -d '[:space:]' < "${REPO_ROOT}/UPSTREAM_REF")"
OUT="${1:-${REPO_ROOT}/upstream}"

rm -rf "${OUT}"
if [[ "${UPSTREAM_REF}" =~ ^[0-9a-f]{40}$ ]]; then
    # A commit pin: clone --branch takes only tags and branches.
    git init --quiet "${OUT}"
    git -C "${OUT}" remote add origin "${UPSTREAM_REPO}"
    git -C "${OUT}" fetch --quiet --depth 1 origin "${UPSTREAM_REF}"
    git -C "${OUT}" checkout --quiet FETCH_HEAD
    if [ "$(git -C "${OUT}" rev-parse HEAD)" != "${UPSTREAM_REF}" ]; then
        echo "fetched $(git -C "${OUT}" rev-parse HEAD), expected ${UPSTREAM_REF}" >&2
        exit 1
    fi
else
    git clone --quiet --depth 1 --branch "${UPSTREAM_REF}" "${UPSTREAM_REPO}" "${OUT}"
fi

cd "${OUT}"
git -c user.name="assemble" -c user.email="assemble@localhost" am "${REPO_ROOT}"/patches/*.patch
cp -r "${REPO_ROOT}/overlay/." "${OUT}/"

patch_count="$(find "${REPO_ROOT}/patches" -name '*.patch' | wc -l)"
echo "assembled ${UPSTREAM_REF} + ${patch_count} patches + overlay into ${OUT}"
