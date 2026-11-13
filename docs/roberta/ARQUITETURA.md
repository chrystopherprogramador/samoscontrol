# Roberta 2.0 — Arquitetura proposta

Evolução do SamosControl, sem novo backend. OpenClaw e Letta são serviços em rede Docker privada.

```
WhatsApp (Evolution) ─┐
Painel Roberta ───────┼─► RobertaOrchestrator (Application)
Voz (ElevenLabs) ─────┘        ├─► IMemoryProvider ─► Letta (PoC) / fallback PostgreSQL
                               ├─► IAgentRuntime ───► OpenClaw /v1/responses ─► Copilot | Ollama
                               │        ▲ client tools (function_call)
                               └─► ToolRegistry ─► ToolExecutionService ─► serviços do Samos
                                       (risco, permissão, confirmação, idempotência, auditoria)
RobertaEventWorker (BackgroundService) ─► política proativa ─► Orchestrator ─► WhatsApp/painel
```

## Decisões

1. **Runtime**: `IAgentRuntime` com `OpenClawRuntime` e `LegacyOllamaRuntime` (o `SamosAgent` atual, só como fallback técnico quando o OpenClaw cair; fora da UI). Sessão: campo `user` do OpenResponses = `samos:{ambiente}:{userId}`.
2. **Ferramentas ficam no Samos.** Definições (JSON schema) vão como tools na requisição; OpenClaw devolve `function_call`; o Samos valida, autoriza, executa e devolve `function_call_output`. OpenClaw não recebe Docker, shell, segredos nem acesso ao PostgreSQL; ferramentas nativas dele (exec, fs, browser) desativadas no agente dedicado.
3. **Modelos**: catálogo via `GET /v1/models`; escolha persistida (`ROBERTA_MODEL`); fallback configurável para Ollama. Nunca nomes fixos.
4. **Copilot**: device flow feito pelo proprietário no gateway; o Samos só exibe o status.
5. **Memória**: dados operacionais sempre da fonte oficial via ferramentas; memória pessoal/episódica/projetos/temporal no Letta (PoC, serviço dedicado chamado pelo backend) com origem, data, escopo, relevância e validade, editável no painel; sem senhas/tokens; mensagens e auditoria em PostgreSQL (`AiMessages`, `AiActions`, nova `RobertaAuthorization`). Letta fora do ar → segue sem memória longa.
6. **Autorização**: `RobertaAuthorization` (userId, ferramenta, hash dos parâmetros, expiração, nonce de uso único, status); botão no painel / código vinculado no WhatsApp; consumida uma vez.
7. **Risco**: N1 leitura (auto); N2 só com política; N3 confirmação; N4 bloqueado.
8. **Proatividade**: `RobertaEventWorker` lê fontes existentes (cobranças vencidas, leads parados, erros de integração, tarefas) → `RobertaEvent` (origem, tipo, severidade, ocorrência, chave de dedup, contexto, ação recomendada) → política (imediato / resumo diário / só registro; limite de frequência; silêncio). O LLM só redige; não executa ações a partir de eventos.
9. **Webhook**: roteamento único. Usuário cadastrado com `Perm.AiUse` → Roberta 2.0; demais → só caixa central, sem resposta automática.
10. **Prompt injection**: conteúdo externo entra como dado delimitado; ações N≥2 originadas de conteúdo externo exigem confirmação.

## Achados da documentação oficial (validados)
- **OpenClaw em Docker**: imagem oficial `ghcr.io/openclaw/openclaw` (Compose v2, `scripts/docker/setup.sh` com onboarding que gera o token do gateway). Porta padrão 18789. Requer revisar o hardening de rede (cadeia `DOCKER-USER`) em VPS.
- **Letta em Docker (`letta/letta`)**: a documentação marca a imagem Docker como **depreciada/sem manutenção ativa** e recomenda o Letta App Server / modo local. Exige modelo de embedding explícito e proteção por senha em produção. **Decisão pendente**: antes de instalar, avaliar o Letta App Server; enquanto isso a memória usa o fallback em PostgreSQL por trás de `IMemoryProvider`.
- Servidor 72.60.250.7: Docker Swarm + EasyPanel, 9 GB RAM livres, nenhuma instância de OpenClaw/Letta; deploy previsto como projeto Compose isolado, sem portas públicas.

## Configuração proposta (sem valores)
`OPENCLAW_BASE_URL`, `OPENCLAW_TOKEN` (segredo), `OPENCLAW_AGENT_ID`, `ROBERTA_MODEL`, `ROBERTA_FALLBACK_PROVIDER`, `ROBERTA_TIMEOUT_SECONDS`, `LETTA_BASE_URL`, `LETTA_API_KEY` (segredo), `ROBERTA_PROACTIVE_ENABLED`, `ROBERTA_QUIET_HOURS`, `ROBERTA_V2_ENABLED` (flag temporária de desenvolvimento).
