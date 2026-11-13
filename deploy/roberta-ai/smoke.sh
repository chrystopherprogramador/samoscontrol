#!/bin/bash
cd /opt/roberta-ai
docker compose exec -T openclaw-gateway node dist/index.js models set github-copilot/claude-sonnet-5.5 2>&1 | grep -viE 'deprecat|trace-dep' | tail -3
sleep 8
T=$(grep ^OPENCLAW_GATEWAY_TOKEN .env | cut -d= -f2 | tr -d '\r\n')
cat > /tmp/req.json <<'EOF'
{"model":"openclaw/default","input":"Responda apenas com a palavra: pronto","user":"samos:teste:0"}
EOF
curl -sS -m 120 -w '\nHTTP %{http_code}\n' -H "Authorization: Bearer $T" -H "Content-Type: application/json" -d @/tmp/req.json http://192.168.48.1:18789/v1/responses | cut -c1-700
rm -f /tmp/req.json
