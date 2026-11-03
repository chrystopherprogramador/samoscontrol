# Changelog

## Entrega 1 — Roberta 2.0 (sobre `202cc2b`)
- Estado conversacional persistido, motor de decisão determinístico, anti-repetição, FATO/INFERÊNCIA/NÃO SABEMOS.
- Validador de respostas (regras + juiz opcional), regeneração única e escalonamento ao humano.
- `ILlmService`/`LlmService` com timeout, retry, circuit breaker e log em `LlmExecutions`.
- Prompts versionados com regressão na ativação.
- Modos Shadow (padrão) / Revisão / Automático (exige `AUTORIZO`), telas `/Sdr/Suggestions` e `/Sdr/Prompts`.
- Removido o handoff por regex ampla (`valor`, `contrato`) e o score decidido pela LLM.
- 157 novos testes (236 no total).
