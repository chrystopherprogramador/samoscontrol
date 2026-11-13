#!/bin/bash
cd /opt/roberta-ai
T=$(grep ^OPENCLAW_GATEWAY_TOKEN .env | cut -d= -f2 | tr -d '\r\n')
H="Authorization: Bearer $T"
echo "--- models (auth)"
curl -sS -m 15 -w '\nHTTP %{http_code}\n' -H "$H" http://192.168.48.1:18789/v1/models | cut -c1-500
echo "--- bad token"
curl -sS -m 15 -o /dev/null -w 'HTTP %{http_code}\n' -H "Authorization: Bearer wrong" http://192.168.48.1:18789/v1/models
