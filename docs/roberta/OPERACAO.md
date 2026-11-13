# Roberta 2.0 — Operação (OpenClaw)

Servidor: 72.60.250.7, diretório `/opt/roberta-ai` (Compose isolado `roberta-ai`; arquivos de referência em `deploy/roberta-ai/`).

## Estado
- OpenClaw `ghcr.io/openclaw/openclaw:latest` (2026.11.3), healthy. **Validado com integração real**: `/v1/models` exige Bearer (401 sem/errado, 200 correto) e é alcançável a partir do container `systems_samos-control`.
- Rede: publicado somente em `192.168.48.1:18789` (docker_gwbridge); nada em `0.0.0.0`; porta pública fechada. Sem Docker socket, sem acesso ao PostgreSQL.
- `gateway.mode=local`, `gateway.auth.mode=token`, `gateway.http.endpoints.responses.enabled=true`.
- O token fica só em `/opt/roberta-ai/.env` (chmod 600), variável `OPENCLAW_GATEWAY_TOKEN`; deve ser copiado para a configuração `OPENCLAW_TOKEN` do Samos, nunca para o código ou este repositório.
- `/v1/models` lista apenas alvos de agente (`openclaw`, `openclaw/default`, `openclaw/main`); o catálogo de modelos do Copilot vem do CLI (`openclaw models list`) — o Samos deve consultá-lo, não presumir nomes.

## Comandos
- Status: `cd /opt/roberta-ai && docker compose ps`
- Logs: `docker compose logs --tail 50 openclaw-gateway`
- Login Copilot (feito pelo proprietário): `docker compose exec openclaw-gateway node dist/index.js models auth login-github-copilot`
- Rollback: `docker compose down` (dados em `openclaw/`, preservados); o Samos continua funcionando sem o OpenClaw.

## Pendências
Seleção de modelo na UI do Samos, restrição de ferramentas nativas do agente, Letta App Server (PoC), integração `OpenClawRuntime` no Samos.

## Validado com integração real (2026-11-12)
- Login Copilot concluído pelo proprietário; 27 modelos descobertos via `openclaw models list --provider github-copilot`.
- Modelo padrão do gateway: `github-copilot/claude-sonnet-5.5` (trocável).
- `POST /v1/responses` (`model: openclaw/default`, campo `user` = sessão) respondeu 200 com resposta real do Copilot.
- Atenção: uma pergunta trivial consumiu ~26 mil tokens de entrada (prompt/ferramentas padrão do agente, 17k em cache). Reduzir com um agente dedicado enxuto e ferramentas nativas desativadas antes de uso em produção, por causa da cota do Copilot.
