#!/bin/bash
cd /opt/roberta-ai
pkill -f login-github-copilot 2>/dev/null
nohup docker compose exec -T openclaw-gateway node dist/index.js models auth login-github-copilot > /opt/roberta-ai/copilot-login.log 2>&1 &
sleep 25
grep -viE 'deprecat|trace-dep' /opt/roberta-ai/copilot-login.log | head -20
