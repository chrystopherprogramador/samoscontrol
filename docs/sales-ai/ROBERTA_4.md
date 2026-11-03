# Roberta 4.0 — Agente comercial adaptativa

Evolução incremental da 3.0 (commit `0a5629d`). Nada foi reescrito; a 3.0 continua recuperável (ver Rollback).

## O que ainda era mecânico na 3.0
- Fluxo quase linear: Ask* → DeepenPain → Pitch → Demo → Handoff.
- Sem tratamento de objeções (caro, "vou pensar", "já tenho sistema", etc.).
- Sem distinção entre fato, hipótese e inferência.
- Escolha de ação sem memória do que já foi tentado.
- Modo Shadow gravava no estado comercial real.
- Follow-up por tempo, sem política de frequência.
- Prompt proibia admitir ser IA.

## Arquitetura
- **Motor de decisão (`RobertaEngine.Decide`)**: regras duras primeiro (opt-out, recusa, fora do ICP, insistência, irritação, "é IA?", pedido de humano, desconto, demo, rede grande), depois `Objection(...)` e, no fluxo comercial, `Choose()` com pontuação e penalidade por repetição (histórico `ActionHistory`).
- **Ações novas**: BuildRapport, ExploreImpact, ShareInsight, TellUseCase, DifferentiateCompetitor, CreateCuriosity, ConfirmUnderstanding, AskForMeeting, SendUsefulInformation, PauseConversation, AnswerIdentity.
- **Plano estruturado (`CommercialPlan`)**: JSON validável (`PlanValidator`). A LLM não decide sozinha; o plano é hoje produzido de forma determinística e já está pronto para um planejador LLM futuro.
- **Memória**: impacto, hipóteses (separadas de fatos), compromissos, canal/horário preferido, estágio da oportunidade, sinais de perda, log de objeções, histórico de ações. `StateVersion` (`ConcurrencyCheck`) + semáforo por contato + dedupe de inbound (30s, textos ≥ 12 caracteres).
- **Shadow isolado**: usa `ShadowConversationStates` (JSON); nunca toca `ConversationStates`.
- **Follow-up (`FollowUpPolicy`)**: máximo 3 tentativas, intervalo mínimo 20h, respeita opt-out/recusa; negação dura encerra, branda adia 4h.
- **Áudio**: só com TTS configurado; "só texto" é respeitado; ações fixas/de registro são sempre texto.
- **Identidade**: se perguntarem, resposta transparente (template fixo, sem LLM) com oferta de atendimento humano. Validador bloqueia falsas alegações humanas e experiências inventadas.
- **Métricas (`RobertaMetrics`)**: resposta, qualificação, demos, transferências, abandono, objeções, estratégias, por versão de prompt. Shadow excluído. Sem UI ainda.
- **Prompt v3**: auto-upgrade somente de prompt criado pelo `sistema` com marcador 3.0/legado; edição humana e rollback manual são respeitados.

## 3.0 × 4.0
| Tema | 3.0 | 4.0 |
|---|---|---|
| Escolha da ação | sequência | regras duras + pontuação com anti-repetição |
| Objeções | nenhuma | 9 tipos, 1ª resposta trata, 2ª escala/encerra |
| Memória | fatos | fatos, hipóteses, impacto, compromissos, preferências |
| Shadow | contaminava estado | isolado |
| Follow-up | por tempo | política de frequência |
| "Você é IA?" | proibido admitir | transparente |
| Concorrência | nenhuma | versão + semáforo + dedupe |
| Métricas | nenhuma | computadas por versão de prompt |

## Exemplos reais (simulações automatizadas)
- "O meu sistema já faz tudo isso" → `DifferentiateCompetitor`; repetido com irritação → `CloseConversation`.
- "Sempre fura o estoque" → `DeepenPain` → (resposta) `PitchFeature` → (interesse) `OfferDemo` → `AskForMeeting`.
- Monossílabos → AskERP, AskStores, AskPain, `PauseConversation`, `CloseConversation`.
- Volta após dias → `ConfirmUnderstanding` retomando o ponto parado.

Transcrições completas: `dotnet test --logger "console;verbosity=detailed" --filter Roberta4`.

## Configuração pendente
- ElevenLabs (chave + Voice ID) para ativar voz.
- Material aprovado na base de conhecimento (sem ele `SendUsefulInformation` cai em KB ou humano).
- Aplicar migration `Roberta4` (deploy não foi feito) e validar em Shadow/Review antes de Auto.

## Rollback
Reative a versão anterior do prompt no painel; `Roberta4` possui `Down`; `git revert` do commit volta ao 3.0. Colunas novas são aditivas.

## Limitações
- Sem planejador LLM (plano determinístico).
- Concorrência: xmin do Npgsql não é usado; proteção é em processo + `StateVersion`.
- Qualidade real do texto da LLM só se valida em produção Shadow/Review.
- Sem tela de métricas.
