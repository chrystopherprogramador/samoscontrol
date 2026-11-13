# Roberta 2.0 — Plano de implementação incremental

Status atual de todos os itens: **Não implementado** (apenas o diagnóstico foi feito). Estados possíveis: Implementado / Testado com mocks / Homologado / Validado com integração real / Pendente de credenciais ou autorização / Não implementado.

Pré-requisito: ponto de reversão (commit/branch) do trabalho pendente, criado pelo proprietário.

| Fase | Entrega | Reversão | Autorização |
|---|---|---|---|
| A | Diagnóstico, arquitetura, plano (feito) | — | — |
| B | Abstrações (`IAgentRuntime`, `IToolRegistry`, `IMemoryProvider`, orquestrador) sobre o `SamosAgent`; `LegacyOllamaRuntime`; roteamento único do webhook; remoção do `DispatchToSdrAsync` e do job SDR | revert | não |
| C | `OpenClawRuntime` (OpenResponses, client tools, timeout, fallback), descoberta de modelos, tela Inteligência | flag | **sim**: subir OpenClaw; login Copilot pelo proprietário |
| D | `LettaMemoryProvider` (PoC), tela Memória, filtro de segredos | desligar provider | **sim**: subir Letta |
| E | Ferramentas por módulo (leitura primeiro; escrita N3 com `RobertaAuthorization`) | migration aditiva + flag | não; homologar em banco de teste |
| F | `RobertaEventWorker`, política proativa, resumo diário | `ROBERTA_PROACTIVE_ENABLED` | não |
| G | Painel Roberta único; remover SDR/Treinamento/Assistente antigo do menu e telas | revert | não |
| H | Testes integrados, segurança, remoção das flags | — | **sim**: qualquer deploy |

## Remoção do SDR (B/G)
Remover da execução e da UI: `SdrService.RunAsync` (Automation.cs), `DispatchToSdrAsync` (WhatsApp.cs), `SdrController`/`TrainingController` e views, menu, categorias de configuração, DI, testes de SDR. **Preservar** tabelas e dados; exclusão só com autorização. `RobertaService`/`RobertaSupport` ficam enquanto a voz depender deles.

## Testes por fase
B: roteamento único (sem resposta duplicada), regressão WhatsApp. C: seleção/fallback/timeout/indisponibilidade (mock HTTP). D: persistência, isolamento, Letta fora do ar. E: schema, permissão, risco, confirmação ligada aos parâmetros, expiração, repetição, idempotência. F: dedup, silêncio, frequência. Todas: prompt injection.

## Documentos restantes
`FERRAMENTAS_SAMOS.md`, `SEGURANCA.md`, `TESTES.md`, `OPERACAO.md` serão escritos junto das fases E, B/C, todas e C/D.
