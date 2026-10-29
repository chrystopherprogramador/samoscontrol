# SAMOS CONTROL

Painel operacional central da Samos: CRM/leads, contratos com assinatura eletrônica, cobranças (Asaas), WhatsApp (Evolution API), assistente de IA (Ollama), automações, monitoramento de servidores, notificações, auditoria e administração — em um **monólito modular** ASP.NET Core 8 + PostgreSQL.

## Módulos
| Módulo | O que faz |
|---|---|
| Dashboard | KPIs e gráficos (leads, contratos, cobranças, serviços, WhatsApp, automações, falhas) |
| CRM | Leads, clientes, contatos, interações, tags, pipeline (Novo → Ganho/Perdido), importação do Google Maps Scraper com deduplicação |
| Contratos | Modelos com `{{PLACEHOLDERS}}`, geração de PDF, assinatura eletrônica com trilha de auditoria (hash, IP, user-agent, aceite) |
| Financeiro | Cobranças PIX/boleto via Asaas, QR Code/copia-e-cola, webhook idempotente |
| WhatsApp | Conversas, envio de texto/documentos/links, webhook Evolution idempotente |
| IA | `SamosAgent` com ferramentas pré-definidas, confirmação para operações sensíveis, auditoria completa; funciona pelo painel e pelo WhatsApp |
| Automações | Jobs manuais/agendados/recorrentes com execuções e logs |
| Monitoramento | Servidores e serviços HTTP/HTTPS/TCP, latência, SSL, uptime, incidentes, alertas WhatsApp com cooldown/recuperação |
| Administração | Usuários, perfis/permissões granulares, auditoria, configurações |

## Requisitos
- .NET SDK 8.0 (desenvolvimento) — ou apenas Docker
- PostgreSQL 14+ (o `docker-compose.yml` sobe um)

## Desenvolvimento
```powershell
$env:DATABASE_CONNECTION_STRING = "Host=localhost;Database=samos;Username=samos;Password=..."
$env:ADMIN_EMAIL = "admin@exemplo.com"; $env:ADMIN_PASSWORD = "UmaSenhaForte123"
dotnet restore
dotnet build
dotnet test
dotnet run --project src/SamosControl.Web
```
Migrations são aplicadas automaticamente na inicialização. Para criar uma nova:
```powershell
dotnet ef migrations add NomeDaMigration -p src/SamosControl.Infrastructure -s src/SamosControl.Web
```
(defina `DATABASE_CONNECTION_STRING` antes — a factory de design-time a exige.)

## Docker
```bash
cp .env.example .env   # preencha POSTGRES_PASSWORD, ADMIN_EMAIL, ADMIN_PASSWORD, APP_URL
docker compose up -d --build
```
Acesse `http://localhost:8080`. Health: `/health`. Dados persistem nos volumes `samos_data` (contratos, PDFs, chaves de criptografia) e `pg_data`.

## EasyPanel
Veja **[DEPLOY.md](DEPLOY.md)**.

## Variáveis de ambiente
Todas listadas e comentadas em `.env.example`. Integrações são **opcionais**: sem configuração aparecem como "Não configurado" e a aplicação funciona normalmente. Valores salvos no painel (Configurações) têm precedência sobre variáveis de ambiente; segredos salvos são criptografados e nunca exibidos.

## Primeiro acesso
1. Suba com `ADMIN_EMAIL` e `ADMIN_PASSWORD` definidos (não há senha padrão; o admin só é criado se não existir nenhum usuário).
2. Entre em `APP_URL`, faça login e **troque a senha** em seguida (Usuários → Editar).
3. Configure integrações em *Administração → Configurações*.
4. Opcional: `SEED_DEMO_DATA=true` insere dados demonstrativos (use somente em testes).

## Documentação
[ARCHITECTURE.md](ARCHITECTURE.md) · [DEPLOY.md](DEPLOY.md) · [SECURITY.md](SECURITY.md) · [INTEGRATIONS.md](INTEGRATIONS.md)

## Licenças de terceiros
Os PDFs usam **QuestPDF (licença Community)**, gratuita para organizações com receita anual abaixo do limite definido pelo fabricante. Confirme se sua empresa se enquadra em https://www.questpdf.com/license/ ou adquira a licença comercial.

## Contratos (implantação + mensalidade)

- Cada contrato aceita implantação (com desconto e parcelamento em 1–24x) e mensalidade (prazo fixo ou indeterminado).
- Ao ser **totalmente assinado**, o sistema gera as cobranças no Asaas de forma idempotente (implantação inteira; mensalidades numa janela de 45 dias, renovada pelo job `CheckBilling`). Falha no Asaas não invalida a assinatura; é registrada no contrato e reexecutada pelo job.
- Ao enviar para assinatura, o link é enviado por WhatsApp (Evolution). Com WhatsApp configurado, a assinatura exige código OTP de 6 dígitos enviado ao telefone do signatário, além de CPF/CNPJ válido, IP, user-agent, hash do documento e aceite explícito.
- O PDF final inclui resumo, cronograma de pagamentos, cláusulas, assinaturas, certificado de evidências e código de verificação pública em `/verify`.
- Novos placeholders: VALOR_IMPLANTACAO, DESCONTO, VALOR_IMPLANTACAO_LIQUIDO, PARCELAS_IMPLANTACAO, VALOR_PARCELA_IMPLANTACAO, PRIMEIRO_VENCIMENTO_IMPLANTACAO, VALOR_MENSALIDADE, INICIO_MENSALIDADE, DIA_VENCIMENTO, PRAZO_MESES, FORMA_PAGAMENTO, CONDICOES_FINANCEIRAS, CODIGO_VERIFICACAO.
- A migration roda automaticamente na inicialização. Os modelos de contrato são minutas: **revise com um advogado** (Lei 14.063/2020, MP 2.200-2/2001, LGPD). Rollback: reimplante `samos-control-scraper-fix.zip` (o novo esquema é só aditivo).

## Cobrança: Asaas ou Efí Bank

- Em Configurações → Cobrança, escolha o provedor ativo (BILLING_PROVIDER=asaas|efi). Contratos, cobranças avulsas e o agente de IA usam o provedor escolhido; cada cobrança guarda o provedor de origem.
- Efí: configure Client_Id/Secret, chave Pix e certificado .p12 (Base64 ou arquivo em /data). Sem webhook: status por polling a cada 30 min e botão Atualizar. Detalhes e limitações em INTEGRATIONS.md.
- A migration (Charges.Provider, padrão ASAAS) é aditiva e automática. Rollback: reimplante samos-control-contracts.zip.
