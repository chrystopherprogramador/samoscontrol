#!/bin/bash
cd /opt/roberta-ai
echo "--- login log"
grep -viE 'deprecat|trace-dep' copilot-login.log | tail -8 | cut -c1-160
echo "--- models"
docker compose exec -T openclaw-gateway node dist/index.js models list --provider github-copilot 2>&1 | grep -viE 'deprecat|trace-dep' | head -40 | cut -c1-160
echo "--- status"
docker compose exec -T openclaw-gateway node dist/index.js models status 2>&1 | grep -viE 'deprecat|trace-dep' | head -20 | cut -c1-160
