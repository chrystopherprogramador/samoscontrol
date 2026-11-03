# Arquitetura Sales AI

```
Mensagem do lead (WhatsApp/Evolution)
  → WhatsAppService.StoreIncomingAsync → SdrService.HandleReplyAsync
  → InboundClassifier        regras determinísticas (opt-out, recusa, humano, preço, demo, satisfeito, irritado, ERP, nº de lojas)
  → ConversationState        persistido no PostgreSQL (1 por lead)
  → CommercialDecisionEngine ação permitida (determinística)
  → RobertaService           a LLM só redige a ação (prompt versionado, FATO/INFERÊNCIA/NÃO SABEMOS)
  → ResponseValidator        regras + juiz semântico opcional; regenera 1x; senão escala ao humano
  → Modo da campanha         Shadow | Review | Auto
  → SdrService.ExecuteAsync  envio / repasse (HandoffAsync existente) / encerramento / opt-out
```

Princípios: integrar > instalar; sem novos containers; tudo no mesmo serviço e no mesmo deploy (ZIP → EasyPanel).
Código: `Application/RobertaEngine.cs` (classificador, estado, motor), `Application/RobertaSupport.cs` (validador, `LlmService`, `PromptService`), `Application/RobertaService.cs` (orquestração), `Application/Sdr.cs` (modos, execução, revisão).
Entregas seguintes (enrichment, decisores, sinais, score, dossiê, A/B) reutilizam `ConversationState` e a tabela `Leads`.
