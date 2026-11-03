# Roberta 3.0 — SDR vendedora

Roberta deixou de ser só qualificadora: agora conduz uma venda consultiva. O **código decide** (estado → ação → formato); a **IA só redige**; o **validador** barra o que não pode sair.

## Lógica de decisão (`CommercialDecisionEngine.Decide`)
1. Paradas duras (com `HandoffKind`): opt-out, recusa, fora do ICP, irritado → `irritado`, pediu humano → `humano`, desconto → `negociacao`, demo → `demo`, ≥10 lojas → `rede`, preço sem base → `preco`, limite de 14 turnos → `maduro`.
2. Sem tempo → `RespectBusy` (curto, sem pergunta); repetiu → encerra sem pressão.
3. Pergunta do lead: com base de conhecimento → `Explain*`; sem → transfere (`duvida`) e registra a lacuna.
4. Satisfeito / 2 negativas → oferta leve e encerra.
5. **Dor declarada** (e demo ainda não oferecida): `DeepenPain` (reconhece + 1 pergunta de aprofundamento) → `PitchFeature` (mini-pitch com UM ângulo do catálogo) → qualificação que faltar (sistema/lojas) → `OfferDemo` se houver interesse (sim/curioso/temperatura ≥55) ou 2 pitches; senão outro ângulo.
6. Sem dor: sistema → lojas → dor aberta → demo → transferência madura.

Uma dor como "sempre fura o estoque" **não transfere**: vai para `DeepenPain`.

## Feeling comercial (estado persistido em `ConversationStates`)
`Temperature` 0–100, `BuyingIntent` (baixa/media/alta), `Engagement`, `MainPain`, `PainIntensity`, `PainDepth`, `PitchCount`, `FeaturesPresented`, `CurrentGoal`, `ClientStyle` (objetivo, comunicativo, informal, tecnico, apressado), `LastFormat`, `AudioSentCount`, `ClientSentAudio`, `ReadyForHandoff`, `HandoffKind`, `LastQuestion`. Calculado por regras determinísticas (`FeelingRules`), nunca exibido ao cliente; aparece no `ContextJson` da sugestão (`feeling`).

## Texto × áudio (`FormatAdvisor`)
Contextual, sem cadência fixa. Texto quando: voz indisponível, perguntas simples, preço/handoff/encerramento/follow-up, cliente objetivo/técnico/apressado ou resposta curta, ou o último envio já foi áudio. Áudio quando o cliente mandou áudio ou em pitch/objeção/aprofundamento com conversa quente. `audio_text` quando há algo a registrar (roteiro `###` texto). Áudio com número, link ou R$, > 110 palavras ou sem texto-companheiro é rebaixado para texto. Sem ElevenLabs configurado tudo sai em texto.

## Catálogo (`ProductCatalog`)
Só funcionalidades do briefing: estoque (controle, movimentações, inventário, WMS, RFID, rastreabilidade), moda (grade/tamanho/cor), vendas (PDV, força de vendas, Axis Vendedor), clientes (CRM, fidelidade, cashback), fiscal (NF-e, NFC-e, NFS-e, MDF-e, CT-e), financeiro (financeiro, boleto, conciliação), redes (multiloja).

## Transferência
`HandoffMessages` gera a mensagem com valor ("Pelo que você me contou… vou chamar alguém do nosso time para te mostrar especificamente essa parte de estoque"), por motivo e dor. O resumo ao vendedor inclui dor, temperatura e intenção.

## Follow-up
`FollowUpTextAsync` (modo Auto) retoma pela dor e usa a LLM; sem estado ou sem IA cai no template da campanha.

## Validador (novas regras)
`ai_disclosure` (nunca diz que é IA/bot/assistente virtual), `banned_opener`, `repeated_opener`, `audio_too_long`, `audio_has_text_only_content`, `audio_text_missing_companion`; `DeepenPain` exige exatamente 1 pergunta; `PitchFeature`/`Explain*`/`OfferDemo` aceitam 0–1; `RespectBusy` não pode perguntar.

## Prompt
Nova versão (v2, "SDR vendedora") criada automaticamente na primeira chamada se o prompt ativo for o padrão `sistema` da 2.0; a versão anterior fica preservada (rollback pela tela de prompts). Prompts editados por pessoas nunca são alterados.

## Configuração
Nenhuma obrigatória. Para áudio: ElevenLabs API Key + Voice ID e "respostas em áudio" ligadas em Configurações. Migration: `RobertaSalesFeeling` (colunas novas com defaults seguros; sem dados destrutivos).

## Observação
No modo Shadow o estado da conversa avança mesmo sem envio. A redação real depende da LLM: valide em Shadow/Review antes do Auto.
