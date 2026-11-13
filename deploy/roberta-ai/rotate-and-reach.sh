#!/bin/bash
cd /opt/roberta-ai
NEW=$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')
sed -i "s/^OPENCLAW_GATEWAY_TOKEN=.*/OPENCLAW_GATEWAY_TOKEN=$NEW/" .env
chmod 600 .env
docker compose up -d --force-recreate openclaw-gateway >/dev/null 2>&1
sleep 60
bash check.sh | tail -3
echo "--- from samos-control container"
C=$(docker ps --format '{{.Names}}' | grep '^systems_samos-control' | head -1)
docker exec "$C" sh -c 'cat < /dev/null; (command -v curl >/dev/null && curl -s -m 6 -o /dev/null -w "curl HTTP %{http_code}\n" http://192.168.48.1:18789/v1/models) || (command -v wget >/dev/null && wget -q -T 6 -O- http://192.168.48.1:18789/v1/models 2>&1 | head -c 100) || echo "sem curl/wget no container"'
