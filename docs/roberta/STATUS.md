# Roberta 2.0 — status real

| Item | Status |
|---|---|
| SDR automático desligado (job, despacho, menu); tabelas/dados preservados | implementado e testado |
| OpenClaw no servidor 72.60.250.7 (só em 192.168.48.1:18789, token rotacionado) | validado com integração real |
| GitHub Copilot via OpenClaw (`github-copilot/claude-sonnet-5.5`) | validado com integração real |
| `OpenClawProvider` + `RoutingAiProvider` (fallback Ollama, desligável) | testado com mocks (`OpenClawTests`) |
| Provedor ativo em produção (`ROBERTA_PROVIDER=openclaw`) | pendente — exige autorização |
| Confirmação de ações com código vinculado a id/usuário/ferramenta/parâmetros | implementado e testado (`AgentTests`) |
| Nonce de uso único persistido / `RobertaAuthorization` | pendente (hoje: AiAction única, TTL 10 min, status muda ao executar) |
| Classificação de risco N1–N4 | não implementado |
| Memória em camadas (`IMemoryProvider`) / Letta | não implementado (Letta não instalado) |
| Proatividade (`RobertaEventWorker`) | não implementado |
| Painel Roberta unificado | não implementado |
| Agente OpenClaw enxuto (cada chamada gasta ~26k tokens) | pendente |
| Remoção do código SDR morto | pendente |
| Migrations `ChargeNotices`/`CentralInbox` aplicadas em banco | pendente |

Testes: 369 aprovados.
