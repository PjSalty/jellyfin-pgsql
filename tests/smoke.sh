#!/usr/bin/env bash
#
# Script: smoke.sh
# Description: End to end smoke test. Boots Jellyfin on PostgreSQL with the Valkey
#              cache enabled, completes the startup wizard, browses twice and asserts
#              cache hits, writes state and asserts freshness, then kills Valkey and
#              asserts the server fails open.
# Usage: ./tests/smoke.sh   (expects ../upstream/publish to exist, see build/assemble.sh)
#

set -euo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TESTS_DIR
# JELLYFIN_HOST: localhost for a native docker daemon; the service alias (e.g.
# "docker") when the daemon runs as a docker-in-docker service.
readonly BASE_URL="http://${JELLYFIN_HOST:-localhost}:8096"
readonly COMPOSE="docker compose -f ${TESTS_DIR}/docker-compose.yml"

info() { echo "[smoke] $*"; }
die()  { echo "[smoke] FAIL: $*" >&2; exit 1; }

# Dump logs BEFORE teardown on any failure: the EXIT trap removes the
# containers, so a post-mortem step in CI would find nothing.
cleanup() {
    rc=$?
    if [ "${rc}" -ne 0 ]; then
        echo "[smoke] jellyfin logs (last 100 lines):" >&2
        ${COMPOSE} logs jellyfin 2>/dev/null | tail -100 >&2 || true
    fi
    ${COMPOSE} down -v >/dev/null 2>&1 || true
    exit "${rc}"
}
trap cleanup EXIT

wait_healthy() {
    for _ in $(seq 1 120); do
        if [ "$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/health")" = "200" ]; then
            return 0
        fi
        sleep 2
    done
    return 1
}

valkey() { ${COMPOSE} exec -T valkey valkey-cli "$@"; }
keyspace_hits() { valkey INFO stats | tr -d '\r' | awk -F: '/^keyspace_hits/ {print $2}'; }

info "starting stack"
${COMPOSE} up -d
wait_healthy || die "server never became healthy on postgres"

info "asserting the cache bootstrapped"
${COMPOSE} logs jellyfin | grep -q "EF second level cache enabled" || die "cache enable log line missing"

info "completing the startup wizard"
curl -sf -X POST "${BASE_URL}/Startup/Configuration" -H 'Content-Type: application/json' \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' > /dev/null
curl -sf "${BASE_URL}/Startup/User" > /dev/null
curl -sf -X POST "${BASE_URL}/Startup/User" -H 'Content-Type: application/json' \
    -d '{"Name":"smoke","Password":"smoketest"}' > /dev/null
curl -sf -X POST "${BASE_URL}/Startup/Complete" > /dev/null

info "authenticating"
auth_header='X-Emby-Authorization: MediaBrowser Client="smoke", Device="ci", DeviceId="ci", Version="1"'
token="$(curl -sf -X POST "${BASE_URL}/Users/AuthenticateByName" \
    -H 'Content-Type: application/json' -H "${auth_header}" \
    -d '{"Username":"smoke","Pw":"smoketest"}' | jq -r '.AccessToken')"
[ -n "${token}" ] && [ "${token}" != "null" ] || die "authentication failed"
user_id="$(curl -sf "${BASE_URL}/Users/Me" -H "X-Emby-Token: ${token}" | jq -r '.Id')"

info "browsing twice and asserting cache hits"
hits_before="$(keyspace_hits)"
curl -sf "${BASE_URL}/Users/${user_id}/Items?Recursive=true&IncludeItemTypes=Movie" -H "X-Emby-Token: ${token}" > /dev/null
curl -sf "${BASE_URL}/Users/${user_id}/Items?Recursive=true&IncludeItemTypes=Movie" -H "X-Emby-Token: ${token}" > /dev/null
hits_after="$(keyspace_hits)"
[ "${hits_after}" -gt "${hits_before}" ] || die "no cache hits after repeated browse (before=${hits_before} after=${hits_after})"
[ "$(valkey DBSIZE)" -gt 0 ] || die "valkey holds no cache entries"

info "asserting writes invalidate and reads stay fresh"
display_prefs_url="${BASE_URL}/DisplayPreferences/usersettings?userId=${user_id}&client=emby"
curl -sf "${display_prefs_url}" -H "X-Emby-Token: ${token}" > /dev/null
curl -sf -X POST "${display_prefs_url}" -H "X-Emby-Token: ${token}" -H 'Content-Type: application/json' \
    -d '{"Id":"usersettings","SortBy":"SortName","SortOrder":"Descending","RememberIndexing":false,"PrimaryImageHeight":250,"PrimaryImageWidth":250,"CustomPrefs":{"smoke":"1"},"ScrollDirection":"Horizontal","ShowBackdrop":true,"RememberSorting":false,"ShowSidebar":false,"Client":"emby"}' > /dev/null
fresh="$(curl -sf "${display_prefs_url}" -H "X-Emby-Token: ${token}" | jq -r '.CustomPrefs.smoke')"
[ "${fresh}" = "1" ] || die "stale read after write: expected CustomPrefs.smoke=1, got ${fresh}"

info "killing valkey and asserting fail open"
${COMPOSE} stop valkey > /dev/null
code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/Users/${user_id}/Items?Recursive=true" -H "X-Emby-Token: ${token}")"
[ "${code}" = "200" ] || die "server did not fail open without valkey (http ${code})"

info "PASS"
