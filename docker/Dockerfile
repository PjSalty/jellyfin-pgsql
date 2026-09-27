# Bakes the published plugin into a Jellyfin server image. The plugin lands in
# /opt/jellyfin-pgsql/plugin, NOT /config/plugins: /config is a volume in real
# deployments and image content there would be shadowed by the mount. An init
# container (or entrypoint wrapper) copies it into place:
#   cp -r /opt/jellyfin-pgsql/plugin /config/plugins/PostgreSQL
#
# Build context expects the dotnet publish output at upstream/publish
# (see build/assemble.sh and the CI pipeline).
ARG BASE_IMAGE=jellyfin/jellyfin:12.1
FROM ${BASE_IMAGE}

# pg_dump/psql: the provider shells out to them (migration backups before EF
# migrations run, restore, delete). Without them the server dies at FIRST
# BOOT with Win32Exception 'pg_dump ... No such file or directory'. PGDG repo
# because pg_dump refuses a server of a newer major and the base image's
# distro ships older. Keep the client major equal to the server's (18): the
# rollback restore stops on settings an older server does not know.
# Everything over https: build environments commonly block plain http egress
# (port 80), and the base image already trusts the system CA bundle. The
# PGDG key stays ASCII-armored (.asc): apt accepts it in Signed-By and the
# base image ships no gpg to dearmor with.
RUN sed -i 's|http://|https://|g' /etc/apt/sources.list.d/debian.sources \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
       -o /usr/share/keyrings/pgdg.asc \
    && echo "deb [signed-by=/usr/share/keyrings/pgdg.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo "${VERSION_CODENAME}")-pgdg main" \
       > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    # Pull the distro security point releases the base image predates; the
    # release Trivy gate fails on fixed-but-unpatched HIGH CVEs otherwise.
    && apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && rm -rf /var/lib/apt/lists/*

COPY upstream/publish/ /opt/jellyfin-pgsql/plugin/
