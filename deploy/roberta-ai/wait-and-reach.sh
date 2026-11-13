#!/bin/bash
cd /opt/roberta-ai
for i in $(seq 1 20); do
  s=$(docker compose ps --format '{{.Status}}')
  case "$s" in *healthy*) break;; esac
  sleep 10
done
echo "status: $s"
bash check.sh | tail -4
C=$(docker ps --format '{{.Names}}' | grep '^systems_samos-control' | head -1)
echo "--- from $C"
docker exec "$C" sh -c 'curl -s -m 6 -o /dev/null -w "samos->openclaw HTTP %{http_code}\n" http://192.168.48.1:18789/v1/models'
