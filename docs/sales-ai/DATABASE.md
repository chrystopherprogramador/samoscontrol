# Banco de dados

Migration `20261103111142_RobertaV2` (aplicada no start): 
- `SdrCampaigns.Mode` (texto, default `Shadow` para campanhas existentes);
- `ConversationStates` (índice único `LeadId`), `PromptVersions` (único `Name+Version`), `SdrSuggestions` (índices `Status+CreatedAt`, `ContactId`), `LlmExecutions` (índice `CreatedAt`).
Nada é removido nem alterado nas tabelas existentes. Tudo referencia `Leads`/`SdrContacts`; não há "LeadsV2".
Reverter: ver `ROLLBACK.md`.
