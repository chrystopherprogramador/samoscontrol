# Estado atual (auditoria em 2026-11-03)

## Repositório
- Branch `main`, commit `202cc2b` = baseline de produção. Working tree limpo, `origin/main` sem commits novos. Nenhuma diferença local antes desta entrega.
- Rollback: ver `ROLLBACK.md`.

## Solution (`SamosControl.sln`)
| Projeto | Papel |
|---|---|
| Domain | Entidades EF (`Crm.cs`, `Sdr.cs`, `Sellers.cs`, `Operations.cs`...) |
| Infrastructure | `AppDbContext` + migrations (PostgreSQL, aplicadas no start) |
| Integrations | Evolution, ElevenLabs, Asaas, Efí, Google Maps Scraper, `IAiProvider` (Ollama) |
| Application | Serviços (`SdrService`, `KnowledgeService`, `WhatsAppService`, `LeadService`, `MapService`, workers) |
| Web | MVC + Razor + Bootstrap 5, `RequirePermission`, CSP `script-src 'self'` |
| Worker | host de background |
| Tests | xUnit com `TestHost` |

Padrões: serviços Scoped registrados em `DependencyInjection.cs`; `BaseEntity` (Id, CreatedAt, UpdatedAt); configurações via `SettingsService`; jobs via `SchedulerWorker`/`MonitoringWorker` (`BackgroundService`).

## Fluxo da Roberta/SDR antes desta entrega (`Application/Sdr.cs`)
1. `SchedulerWorker` chama `SdrService.RunAsync`: primeiro contato (template com variantes) e follow-ups.
2. Resposta do lead: `WhatsAppService.StoreIncomingAsync` → `DispatchToSdrAsync` → `SdrService.HandleReplyAsync`.
3. `HandleReplyAsync`: opt-out por regex → `AskAiAsync` (1 prompt grande, JSON com `reply/score/handoff/...`) → handoff se `Handoff`, regex de "preço/valor/contrato" ou score ≥ 70 → senão envia `reply`.
4. Estado = apenas `SdrContact` (Stage, Attempts, AiTurns, Score, Summary) + últimas 12 mensagens como texto.
5. Base de conhecimento: `AiKnowledge` (pergunta/resposta), busca por palavras-chave.

## Problemas encontrados e causas prováveis
| Sintoma | Causa provável no código |
|---|---|
| Perguntas repetidas / loop | Não há registro de perguntas feitas. O prompt manda "qualifique aos poucos, uma pergunta por vez", então a LLM escolhe sozinha a próxima pergunta e reaproveita áreas já negadas. Histórico de 12 mensagens sem estado estruturado. |
| Insistência após "está tudo bem" | Nenhuma regra de mudança de estratégia; a LLM é instruída a qualificar sempre. |
| Alucinação | A LLM decide score, handoff, conteúdo e persona no mesmo prompt; sem contexto Axis; KB fraca só gera um aviso em texto ("não invente"). Nenhuma validação da saída. |
| Handoff falso | `HandoffWords` dispara em "valor", "contrato" etc. mesmo em frases como "o valor do meu sistema atual está ok". |
| Resposta duplicada | Corrigido em `202cc2b` (dedupe de webhook + checagem da última mensagem). |
| Sem observabilidade | Não se registra prompt, modelo, latência nem versão. Prompt fixo no código. |
| Sem modo seguro | Campanha ativa envia respostas da IA direto. |

Estas causas vêm da leitura do código; não foram reproduzidas com a conversa original.

## Capacidades existentes (reaproveitadas)
CRM/leads (dedupe por telefone, Place ID, site, nome+endereço), scraper Google Maps, validação de número no WhatsApp, opt-out, distribuição por rodízio/região (`PickSellerAsync`), `HandoffAsync` (notificação + resumo), follow-up, métricas por variante, áudio ElevenLabs, auditoria, permissões.

## Capacidades faltantes (cobertas nas entregas)
Estado conversacional estruturado, motor de decisão, validador, versionamento de prompt, shadow/review mode, LLM service com retry/circuit breaker, enrichment, decisores, buying signals, lead score, dossiê, experimentos A/B, learning loop.

## Testes
79 testes xUnit passando no baseline (`dotnet test -c Release`).

## Campanha
Pausada por decisão do dono. Nenhuma entrega ativa campanha automaticamente.
