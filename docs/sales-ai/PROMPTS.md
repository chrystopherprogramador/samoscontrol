# Prompts

`PromptVersions` (Name, Version, Content, CreatedBy, IsActive, Notes, PassRate). O prompt inicial (v1) é semeado no primeiro uso. Cada `SdrSuggestion` e `LlmExecution` guarda a versão usada.

Tela: `/Sdr/Prompts`. Criar nova versão (inativa) → Ativar. A ativação roda a regressão com 6 cenários dourados (`PromptService.Golden`) pelo `ILlmService` e valida cada saída com o `ResponseValidator`. Exige ≥ 90% e não pior que a versão ativa. Sem IA configurada, só ativa com "forçar" (auditado).
O conteúdo do prompt define persona e regras; ação, fatos, KB e perguntas já feitas são adicionados dinamicamente pelo `RobertaService`.
Promptfoo pode ser usado no desenvolvimento para comparar versões; não faz parte do deploy.
