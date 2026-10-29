#!/bin/sh
set -e
# The persistent volume is usually created as root by the host/EasyPanel: fix ownership, then drop privileges.
if [ "$(id -u)" = "0" ]; then
  mkdir -p "${STORAGE_PATH:-/data}"
  chown -R samos:samos "${STORAGE_PATH:-/data}" 2>/dev/null || true
  exec setpriv --reuid=samos --regid=samos --init-groups "$@"
fi
exec "$@"
