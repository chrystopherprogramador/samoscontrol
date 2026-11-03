# Conversation State

Tabela `ConversationStates` (única por `LeadId`): Stage, InterestLevel (0-3), StoreCount, CurrentErp, Segment, PainPoints, Objections, QuestionsAsked/Answered (tópicos `erp,stores,pain`), PriceDiscussed, DemoOffered/Accepted, HumanRequested, OptOut, SatisfiedWithCurrent, NegativeStreak, TurnCount, LastAction, Summary, LastContactAt.

`ConversationStateRules.Apply` atualiza o estado a cada mensagem recebida (a resposta à última pergunta conta como respondida, mesmo "não"). `RecordAction` registra a ação proposta. Valores só são preenchidos a partir do que o lead disse ou do CRM; desconhecido = `null`.

## FATO / INFERÊNCIA / NÃO SABEMOS
O prompt recebe os dados rotulados:
- `FATO: ... (fonte: CRM | resposta do lead)`
- `INFERÊNCIA: ...` (nunca dita ao cliente como certeza)
- `NÃO SABEMOS: ...` (a LLM não pode afirmar nem presumir)
O validador reforça: números, URLs e nomes de ERP que não estejam nos fatos/KB/mensagem do lead reprovam a resposta.
