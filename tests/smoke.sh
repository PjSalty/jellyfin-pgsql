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
# containers, so a post-mortem step in CI would find nothing. Health-check
# spam is filtered out; it repeats every 2s and drowns the real error.
cleanup() {
    rc=$?
    if [ "${rc}" -ne 0 ]; then
        echo "[smoke] jellyfin logs (last 300 lines, health spam filtered):" >&2
        ${COMPOSE} logs jellyfin 2>/dev/null | grep -viE 'healthcheckservice|health check' | tail -300 >&2 || true
    fi
    ${COMPOSE} down -v >/dev/null 2>&1 || true
    exit "${rc}"
}
trap cleanup EXIT

wait_healthy() {
    # The body must say Healthy: ASP.NET health endpoints return HTTP 200 for
    # Degraded too, and first boot spends minutes in EF migrations + seeding
    # while reporting Degraded ("Server is still starting up").
    for _ in $(seq 1 240); do
        if [ "$(curl -s "${BASE_URL}/health")" = "Healthy" ]; then
            return 0
        fi
        sleep 2
    done
    return 1
}

valkey() { ${COMPOSE} exec -T valkey valkey-cli "$@"; }
keyspace_hits() { valkey INFO stats | tr -d '\r' | awk -F: '/^keyspace_hits/ {print $2}'; }

info "starting stack (builds the plugin image: base + postgresql-client + plugin)"
${COMPOSE} up -d --build
wait_healthy || die "server never became healthy on postgres"

info "asserting the cache bootstrapped"
# No grep -q here: it closes the pipe at first match and docker compose logs
# then dies with SIGPIPE, which pipefail turns into a failure DESPITE the
# match. Plain grep drains the stream to EOF.
${COMPOSE} logs jellyfin | grep "EF second level cache enabled" > /dev/null || die "cache enable log line missing"

info "completing the startup wizard"
wizard() {
    step="$1"; shift
    if ! curl -sf "$@" > /dev/null; then
        die "wizard step failed: ${step}"
    fi
}
wizard configuration -X POST "${BASE_URL}/Startup/Configuration" -H 'Content-Type: application/json' \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
wizard first-user-get "${BASE_URL}/Startup/User"
wizard first-user-set -X POST "${BASE_URL}/Startup/User" -H 'Content-Type: application/json' \
    -d '{"Name":"smoke","Password":"smoketest"}'
wizard complete -X POST "${BASE_URL}/Startup/Complete"

info "authenticating"
auth_header='X-Emby-Authorization: MediaBrowser Client="smoke", Device="ci", DeviceId="ci", Version="1"'
token="$(curl -sf -X POST "${BASE_URL}/Users/AuthenticateByName" \
    -H 'Content-Type: application/json' -H "${auth_header}" \
    -d '{"Username":"smoke","Pw":"smoketest"}' | jq -r '.AccessToken')"
if [ -z "${token}" ] || [ "${token}" = "null" ]; then
    die "authentication failed"
fi
user_id="$(curl -sf "${BASE_URL}/Users/Me" -H "X-Emby-Token: ${token}" | jq -r '.Id')"

info "browsing twice and asserting cache hits"
hits_before="$(keyspace_hits)"
curl -sf "${BASE_URL}/Users/${user_id}/Items?Recursive=true&IncludeItemTypes=Movie" -H "X-Emby-Token: ${token}" > /dev/null
curl -sf "${BASE_URL}/Users/${user_id}/Items?Recursive=true&IncludeItemTypes=Movie" -H "X-Emby-Token: ${token}" > /dev/null
hits_after="$(keyspace_hits)"
[ "${hits_after}" -gt "${hits_before}" ] || die "no cache hits after repeated browse (before=${hits_before} after=${hits_after})"
[ "$(valkey DBSIZE)" -gt 0 ] || die "valkey holds no cache entries"

info "asserting the pool params reached the connection string (patch 0004)"
# The provider logs the resolved connection string at startup (password nulled),
# so the pool params are directly observable and deterministically checkable --
# no load, no timing, no assumptions about Npgsql's lazy pool warmup. Assert the
# canonical Npgsql keywords with the values patch 0004 sets. An unpatched build
# logs none of them (the defaults are implicit, not serialized).
# Full behaviour (warm floor surviving idle pruning on a DNS-flaky network) is
# verified in prod by the before/after 500-rate comparison, not here.
conn_log="$(${COMPOSE} logs jellyfin 2>&1 | grep -F 'PostgreSQL connection string' | tail -1)"
[ -n "${conn_log}" ] || die "provider never logged its connection string"
case "${conn_log}" in
    *"Maximum Pool Size=15"*) : ;;
    *) die "MaxPoolSize not in connection string (pool params not applied): ${conn_log#*PostgreSQL connection string: }" ;;
esac
case "${conn_log}" in
    *"Minimum Pool Size=3"*) : ;;
    *) die "MinPoolSize not in connection string: ${conn_log#*PostgreSQL connection string: }" ;;
esac
case "${conn_log}" in
    *"Keepalive=30"*) : ;;
    *) die "Keepalive not in connection string: ${conn_log#*PostgreSQL connection string: }" ;;
esac

info "asserting writes invalidate and reads stay fresh"
display_prefs_url="${BASE_URL}/DisplayPreferences/usersettings?userId=${user_id}&client=emby"
curl -sf "${display_prefs_url}" -H "X-Emby-Token: ${token}" > /dev/null
curl -sf -X POST "${display_prefs_url}" -H "X-Emby-Token: ${token}" -H 'Content-Type: application/json' \
    -d '{"Id":"usersettings","SortBy":"SortName","SortOrder":"Descending","RememberIndexing":false,"PrimaryImageHeight":250,"PrimaryImageWidth":250,"CustomPrefs":{"smoke":"1"},"ScrollDirection":"Horizontal","ShowBackdrop":true,"RememberSorting":false,"ShowSidebar":false,"Client":"emby"}' > /dev/null
fresh="$(curl -sf "${display_prefs_url}" -H "X-Emby-Token: ${token}" | jq -r '.CustomPrefs.smoke')"
[ "${fresh}" = "1" ] || die "stale read after write: expected CustomPrefs.smoke=1, got ${fresh}"

info "killing valkey and asserting fail open"
${COMPOSE} stop valkey > /dev/null
# Fail-open is EVENTUAL, not instant: a request whose reader the interceptor
# already consumed when the cache died errors until the availability probe
# marks the provider down (5s re-probe interval). Assert recovery, allowing
# the bounded blip.
code=""
for _ in $(seq 1 12); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/Users/${user_id}/Items?Recursive=true" -H "X-Emby-Token: ${token}")"
    if [ "${code}" = "200" ]; then
        break
    fi
    sleep 5
done
[ "${code}" = "200" ] || die "server never failed open without valkey (last http ${code})"

info "PASS"
