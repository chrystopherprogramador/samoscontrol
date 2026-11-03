# Rollback

Baseline de produção: commit `202cc2b` (79 testes). Esta entrega é um commit separado em cima dele.

- **Código**: `git revert <commit-da-entrega>` (ou novo ZIP gerado a partir de `202cc2b` e enviado ao EasyPanel).
- **Banco**: a migration só adiciona (coluna `SdrCampaigns.Mode` e 4 tabelas). Código antigo ignora esses itens, então voltar ao `202cc2b` sem reverter o banco funciona. Para remover: `dotnet ef database update 20261030163731_SdrAndKnowledge` (descarta sugestões, estados e logs da Roberta 2.0).
- **Comportamento**: o código antigo respondia com IA direto; nesta entrega o padrão é Shadow (não envia).
