# Plano de implementação — Sales AI no Samos Control

Princípio: incremental, dentro do mesmo serviço e do mesmo deploy (ZIP → EasyPanel → Docker build). Sem novos containers.

## 1. Pesquisa open source (classes A–F)
Conferida em 2026-11-03 nas páginas dos projetos. O que não consegui verificar está marcado.

| Projeto | Licença | Classe | Decisão |
|---|---|---|---|
| OpenGTM (debpalash) | AGPL-3.0 | D | Ideias: waterfall de enrichment por custo, buying signals. Copiar código obrigaria abrir o Samos Control (AGPL). Não instalar: seria outro CRM/painel. |
| OpenOutreach (eracle) | GPL-3.0, Python, focado em LinkedIn | D/F | Copyleft e depende de LinkedIn (risco de ToS). Só inspiração do fluxo "explicar por que o lead serve". |
| OpenProspector, OpenEnrich, OpenOutFind | não verificado | D | Sem avaliação de licença/atividade ainda; revisar antes da Entrega 2. |
| Langfuse | MIT (pasta `ee` sob licença comercial) | D | Compose pesado (Postgres + ClickHouse + Redis + S3). A observabilidade mínima é nativa (`LlmExecution`). Reavaliar se o volume justificar. |
| Promptfoo | MIT, CLI Node | C | Útil no desenvolvimento para comparar prompts. Não entra no deploy. |
| Opik | Apache-2.0 | D | Mesma lógica do Langfuse. |
| Phoenix (Arize) | Elastic License 2.0 | F | Licença restritiva e stack Python separada. |

Conclusão: nada entra como serviço. Tudo vira código C# nativo, sem novas dependências NuGet na Entrega 1.

## 2. Arquitetura (Entrega 1)
```
Mensagem do lead
  → InboundClassifier (regras: opt-out, recusa, humano, preço, demo, satisfeito, irritado, loja/ERP)
  → ConversationState (PostgreSQL) atualizado
  → CommercialDecisionEngine → ação recomendada + ações permitidas
  → RobertaService → a LLM só redige a ação (prompt versionado)
  → ResponseValidator (regras + juiz semântico opcional) → passa / regenera 1x / escala
  → Modo da campanha: Shadow (só registra) | Review (humano aprova) | Auto (envia)
  → WhatsApp / Handoff existente
```

## 3. Banco (migration `RobertaV2`)
Novas tabelas: `ConversationStates` (1 por lead), `PromptVersions`, `SdrSuggestions` (shadow/review), `LlmExecutions` (observabilidade). Coluna nova `SdrCampaigns.Mode` (default Shadow). Nada de "LeadsV2": tudo referencia `Leads.Id` e `SdrContacts.Id`.

## 4. Classes
`InboundClassifier`, `CommercialDecisionEngine`, `ConversationStateService`, `ResponseValidator`, `RobertaService`, `PromptService`, `LlmService` (retry, timeout, circuit breaker, log), `SuggestionService` (review).

## 5. Telas
- `/Sdr` campanha: seletor de modo (Auto exige digitar AUTORIZO).
- `/Sdr/Suggestions`: fila Shadow/Review com resposta proposta, ação, contexto, validação, aprovar/editar/rejeitar.
- `/Sdr/Prompts`: versões, nova versão, ativar (com teste de regressão).

## 6. Testes
Meta: ≥100 cenários novos em `RobertaTests`. Os 79 existentes continuam passando.

## 7. Riscos
1. Migration em produção (roda no start; o `Up` foi revisado).
2. Regex em português é frágil: por isso Shadow/Review antes do Auto.
3. A LLM ainda redige o texto: o validador reduz, não elimina, o risco.
4. Roteamento de modelo por tarefa depende de mudança no `IAiProvider` (pendente, Entrega 2).
5. A versão que o dono diz ter alterado no servidor não está no Git; um deploy sobrescreve.

## 8. Ordem
1. Entrega 1 — Roberta 2.0 (esta).
2. Entrega 2 — Sales Intelligence (enrichment, decisores, sinais, score determinístico, dossiê).
3. Entrega 3 — Personalização (dossiê → decisões da Roberta).
4. Entrega 4 — Learning loop (analytics, A/B por prompt/abordagem, conversão por venda).
