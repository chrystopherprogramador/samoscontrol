# Roberta 2.0

A Roberta é a persona do SDR automático. Na 2.0 a LLM **não decide nada**: só escreve a mensagem da ação escolhida pelo `CommercialDecisionEngine`.

## Prioridade de decisão
1. Opt-out → `DoNotContact` (mensagem fixa).
2. Recusa clara ou fora do ICP → `CloseConversation` (mensagem fixa).
3. Irritado, pedido de humano, desconto, demo pedida/aceita, rede com 10+ lojas, preço sem base na KB, pergunta fora da KB, limite de 8 turnos → `HandoffToSeller`.
4. Pergunta do lead respondível pela KB → `ExplainFeature` / `ExplainStockControl` / `ExplainMultistore`; preço só se a KB tiver (`ExplainPricing`).
5. Satisfeito com a solução atual, ou 2 respostas negativas seguidas → `OfferDemo` leve (sem perguntar dores) e depois `CloseConversation`.
6. Qualificação, no máximo uma vez cada: `AskCurrentERP` → `AskStoreCount` → `AskPainPoint` (pulando o que o CRM/estado já sabe).
7. `OfferDemo` e, havendo interesse, `HandoffToSeller`.

## Anti-loop
Cada pergunta feita fica em `QuestionsAsked` e a resposta (inclusive "não") em `QuestionsAnswered`; nunca se repete. O validador também bloqueia pergunta repetida e mensagem muito parecida com as últimas 5 enviadas.

## Modos (`SdrCampaign.Mode`, padrão Shadow)
- **Shadow**: gera, valida e registra em `/Sdr/Suggestions`. Não envia, não repassa, não avisa vendedor. Opt-out é marcado.
- **Revisão**: sugestão fica Pendente; humano aprova, edita ou rejeita. Aprovar executa.
- **Automático**: envia sozinha. Só é habilitado digitando `AUTORIZO` no formulário da campanha (registrado na auditoria). Nunca é ligado automaticamente.

A campanha continua pausada até o dono ativar. Em shadow o estado conversacional avança como se a mensagem tivesse sido enviada (simulação); ao trocar para Revisão/Auto num lead em simulação, convém limpar `ConversationStates` desse lead.
