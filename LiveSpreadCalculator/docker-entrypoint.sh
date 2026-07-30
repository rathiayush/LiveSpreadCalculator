#!/bin/sh
set -e

# Runs as root (see the Dockerfile's `USER root` right before this) so it can fix ownership of
# whatever got mounted at /data - App_Data persistence for triggers.csv, users.csv, etc.; see
# LINUX_VPS_DEPLOYMENT.md §6. A freshly created Docker volume (or an empty bind-mount host
# directory) is root-owned by default, but this image otherwise never runs as root - without this,
# the app's very first attempt to write App_Data/triggers.csv fails with
# UnauthorizedAccessException/"Permission denied" and the container crash-loops forever, which is
# exactly what happened the first time this was actually deployed with a mounted volume. Only
# touches /data if something is actually mounted there, so a deployment with no volume (or one
# that's already correctly owned) pays nothing for this.
if [ -d /data ]; then
    chown -R "$APP_UID" /data 2>/dev/null || true
fi

# Resolve $APP_UID (a bare number - see the base image's own `USER $APP_UID`) to the matching
# username via NSS rather than assuming Microsoft's base image always calls it "app" - su looks
# users up by name, not by raw UID, so this is what makes the drop-privileges step correct
# regardless of what a given base image version happens to name that account.
APP_USER=$(getent passwd "$APP_UID" | cut -d: -f1)
if [ -z "$APP_USER" ]; then
    APP_USER="$APP_UID"
fi

# `su`, not gosu/su-exec (neither ships in this base image and adding one means another apt
# package to install and trust) - the tradeoff is that dotnet ends up as su's child rather than
# PID 1 itself, so graceful shutdown depends on this su build forwarding SIGTERM to it. If
# `docker stop`/a Dokploy redeploy ever seems to hang for the full stop-timeout before killing the
# old container, that's the first thing to revisit here.
exec su -s /bin/sh "$APP_USER" -c 'exec dotnet LiveSpreadCalculator.dll "$@"' -- "$@"
