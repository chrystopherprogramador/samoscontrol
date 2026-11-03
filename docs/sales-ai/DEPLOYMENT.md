# Deploy

Inalterado: ZIP do projeto → upload no mesmo serviço `systems/samos-control` do EasyPanel → build Docker → deploy. A migration roda no start. Não há novos containers, variáveis de ambiente ou dependências NuGet.

Antes do primeiro deploy desta entrega:
1. Confirme se existe versão alterada direto no servidor (o upload sobrescreve o código do serviço).
2. Deploy; abra `/Sdr/Prompts` (semeia o prompt v1) e `/Sdr`.
3. As campanhas existentes ficam em **Shadow** e pausadas. Não ative nem mude para Automático sem decisão do dono.
