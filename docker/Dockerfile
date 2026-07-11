# Bakes the published plugin into a Jellyfin server image. The plugin lands in
# /opt/jellyfin-pgsql/plugin, NOT /config/plugins: /config is a volume in real
# deployments and image content there would be shadowed by the mount. An init
# container (or entrypoint wrapper) copies it into place:
#   cp -r /opt/jellyfin-pgsql/plugin /config/plugins/PostgreSQL
#
# Build context expects the dotnet publish output at upstream/publish
# (see build/assemble.sh and the CI pipeline).
ARG BASE_IMAGE=jellyfin/jellyfin:10.11.11
FROM ${BASE_IMAGE}

COPY upstream/publish/ /opt/jellyfin-pgsql/plugin/
