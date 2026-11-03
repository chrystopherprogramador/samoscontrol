# Changelog

## Roberta 3.0 — SDR vendedora (sobre `222805d`)
- Venda consultiva: dor declarada → `DeepenPain` → `PitchFeature` → demo/transferência (não transfere mais ao achar a dor). Ver `ROBERTA_3.md`.
- "Feeling comercial" persistido (temperatura, intenção, engajamento, dor principal, estilo, objetivo) + migration `RobertaSalesFeeling`.
- Texto × áudio contextual (`FormatAdvisor`), áudio + texto, rebaixamento seguro.
- Mensagens de transferência com valor, follow-up contextual, prompt v2 sem revelar IA, novas regras no validador.
- 24 novos testes (260 no total), incluindo os 10 cenários simulados.

## Entrega 1 — Roberta 2.0 (sobre `202cc2b`)
- Estado conversacional persistido, motor de decisão determinístico, anti-repetição, FATO/INFERÊNCIA/NÃO SABEMOS.
- Validador de respostas (regras + juiz opcional), regeneração única e escalonamento ao humano.
- `ILlmService`/`LlmService` com timeout, retry, circuit breaker e log em `LlmExecutions`.
- Prompts versionados com regressão na ativação.
- Modos Shadow (padrão) / Revisão / Automático (exige `AUTORIZO`), telas `/Sdr/Suggestions` e `/Sdr/Prompts`.
- Removido o handoff por regex ampla (`valor`, `contrato`) e o score decidido pela LLM.
- 157 novos testes (236 no total).
