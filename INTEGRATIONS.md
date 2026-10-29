# Integrações

Todas são **opcionais** e desacopladas (interfaces em `SamosControl.Integrations`). Sem configuração aparecem como "Não configurado".

## Asaas (`AsaasIntegration`)
- Variáveis: `ASAAS_API_URL` (sandbox `https://sandbox.asaas.com/api/v3`, produção `https://api.asaas.com/v3`), `ASAAS_API_KEY`, `ASAAS_WEBHOOK_TOKEN`.
- Usa a API v3 oficial (header `access_token`): clientes (criar/atualizar), cobranças (PIX/boleto), QR Code PIX e copia-e-cola, consulta de cobrança/pagamento, cancelamento.
- Webhook: `POST /api/webhooks/asaas` com header `asaas-access-token` igual a `ASAAS_WEBHOOK_TOKEN`. Eventos tratados: `PAYMENT_RECEIVED`, `PAYMENT_CONFIRMED` (pago), `PAYMENT_OVERDUE` (vencido), `PAYMENT_DELETED` (cancelado), `PAYMENT_REFUNDED` (estornado); demais são registrados e ignorados. Eventos repetidos não são reprocessados.
- ⚠️ Não testado contra o Asaas real (sem credenciais neste ambiente). Valide primeiro no sandbox.

## Efí Bank (`EfiIntegration`) — provedor de cobrança alternativo
- Escolha o provedor em Configurações → "Cobrança (provedor)" (`BILLING_PROVIDER` = `asaas` ou `efi`; padrão `asaas`). Só um provedor emite cobranças novas por vez; cobranças já criadas continuam sendo atualizadas pelo provedor de origem (campo `Provider`).
- Variáveis: `EFI_CLIENT_ID`, `EFI_CLIENT_SECRET`, `EFI_SANDBOX` (`true` = homologação), `EFI_PIX_KEY`, `EFI_CERT_BASE64` (conteúdo do .p12 em Base64) **ou** `EFI_CERT_PATH` (ex.: `/data/efi.p12`), `EFI_CERT_PASSWORD` (normalmente vazia).
- Passo a passo na conta Efí: criar aplicação com as APIs **Pix** (escopos `cobv.write`, `cobv.read`, `payloadlocation.write`, `payloadlocation.read`) e **Cobranças** (boleto); gerar o certificado .p12 (produção/homologação) e cadastrar a chave Pix.
- Rotas (conforme SDK oficial `efipay/sdk-dotnet-apis-efi`): Pix (mTLS) `POST /oauth/token`, `PUT|GET|PATCH /v2/cobv/{txid}`, `GET /v2/loc/{id}/qrcode`; Boleto `POST /authorize`, `POST /charge/one-step`, `GET /charge/{id}`, `PUT /charge/{id}/cancel`.
- Tipo PIX → cobrança com vencimento (`cobv`); BOLETO → boleto bancário. Cartão não é suportado na Efí. CPF/CNPJ do cliente é obrigatório.
- **Sem webhook**: o webhook Pix da Efí exige mTLS no servidor receptor (inviável atrás do Traefik). O status é consultado a cada 30 min pelo job `CheckBilling` e no botão "Atualizar status".
- ⚠️ Não testado contra a Efí real. Teste antes em `EFI_SANDBOX=true`: o formato do cliente PJ no boleto e o cancelamento Pix (`PATCH` padrão BCB) não constam nos exemplos do SDK.

## Evolution API (`EvolutionApiIntegration`)
- Variáveis: `EVOLUTION_API_URL`, `EVOLUTION_API_KEY`, `EVOLUTION_INSTANCE`, `EVOLUTION_WEBHOOK_TOKEN`.
- Implementado conforme Evolution API v2: header `apikey`, `/message/sendText/{instance}` e `/message/sendMedia/{instance}`.
- Webhook: `POST /api/webhooks/evolution?token=...` (ou header `apikey`). Eventos: `MESSAGES_UPSERT`, `SEND_MESSAGE`, `MESSAGES_UPDATE`. Grupos e status são ignorados. Mensagens duplicadas são ignoradas.
- ⚠️ O parser do webhook foi escrito pelo formato documentado da v2, mas **não foi validado contra sua instância real**. Se sua versão enviar formato diferente, ajuste `EvolutionWebhookParser`.

## Ollama (`IAiProvider` / `OllamaProvider`)
- Ollama Cloud: basta o token (`OLLAMA_API_KEY`, criado em https://ollama.com/settings/keys). URL padrão `https://ollama.com`; modelo padrão `beam` (gratuito); contingência `gpt-oss:20b` (`OLLAMA_FALLBACK_MODEL`). Também aceita servidor próprio via `OLLAMA_BASE_URL`. Variáveis: `OLLAMA_API_KEY`, `OLLAMA_MODEL`, `OLLAMA_FALLBACK_MODEL`, `OLLAMA_BASE_URL`. Modelos disponíveis são listados em Configurações → IA.
- Novos providers: implemente `IAiProvider` e registre em `DependencyInjection`.

## Google Maps Scraper (`GoogleMapsScraperIntegration`)
Dois modos, escolhidos em `GOOGLE_SCRAPER_MODE`:

**`gosom`** — [gosom/google-maps-scraper](https://github.com/gosom/google-maps-scraper) v1.18.1 (confirmado em `web/web.go`, `web/job.go` e `gmaps/entry.go` da tag v1.18.1). Fluxo fixo, sem caminhos a configurar:
1. `POST /api/v1/jobs` → `{"id": "..."}`; o corpo (`name`, `keywords`, `lang`, `zoom`, `depth`, `max_time` em segundos, `fast_mode`, `email`) vem de `GOOGLE_SCRAPER_CREATE_BODY` ou do padrão interno. Placeholders: `{{search}}` (termo + local), `{{query}}`, `{{location}}`.
2. `GET /api/v1/jobs/{id}` → `status`: `pending`/`working` (aguarda), `ok` (importa), `failed` (marca a pesquisa como FALHA).
3. `GET /api/v1/jobs/{id}/download` → CSV (colunas `title, category, address, website, phone, place_id, emails, complete_address`). Cidade/UF só vêm de `complete_address` quando existirem.

A sincronização (job automático ou botão) só baixa o CSV quando o status é `ok`; depois a pesquisa fica CONCLUIDA. Autenticação: `GOOGLE_SCRAPER_BASIC_USER` + `GOOGLE_SCRAPER_BASIC_PASSWORD` (HTTP Basic, senha criptografada) e/ou API key por header.

**vazio (genérico)** — API JSON; nenhum endpoint presumido, tudo configurável:

| Variável | Função |
|---|---|
| `GOOGLE_SCRAPER_URL`, `GOOGLE_SCRAPER_API_KEY`, `GOOGLE_SCRAPER_API_KEY_HEADER` | base, chave e nome do header da chave (padrão `X-API-Key`) |
| `GOOGLE_SCRAPER_BASIC_USER` / `_BASIC_PASSWORD` | usuário e senha HTTP Basic (opcional, também no modo gosom) |
| `GOOGLE_SCRAPER_CREATE_PATH` / `_CREATE_BODY` | caminho e corpo JSON para criar pesquisa (use `{{query}}` e `{{location}}`) |
| `GOOGLE_SCRAPER_SEARCH_ID_PATH` | caminho JSON do ID na resposta |
| `GOOGLE_SCRAPER_RESULTS_PATH` | caminho para buscar resultados (`{{searchId}}` = ID da pesquisa) |
| `GOOGLE_SCRAPER_ITEMS_PATH` | caminho JSON da lista de itens |
| `GOOGLE_SCRAPER_MAP_*` | nome do campo JSON para NAME, PHONE, ADDRESS, WEBSITE, PLACE_ID, CITY, STATE, CATEGORY, EMAIL |
| `GOOGLE_SCRAPER_SYNC_MINUTES` | intervalo do job automático |

Deduplicação: telefone, Google Place ID, website e nome+endereço. Cada sincronização registra encontrados/criados/atualizados/erros.

## E-mail (SMTP)
Campos de configuração existem, mas **não há envio de e-mail implementado**: a recuperação de senha gera um link que o administrador copia (Usuários → chave).


## Vendedores, comissões e co-assinatura do dono

- **Dono assina todos os contratos:** preencha em Configurações → Empresa `OWNER_NAME`, `OWNER_DOCUMENT`, `OWNER_PHONE` (WhatsApp), `OWNER_EMAIL`, `OWNER_TITLE`. Todo contrato enviado para assinatura gera também um link por WhatsApp para o dono (parte CONTRATADA). Sem `OWNER_NAME` o contrato só exige o cliente.
- **Vendedores:** menu Vendedores. Cadastre Pix, % da implantação e % da mensalidade (e opcionalmente o número de meses de comissão recorrente). Clientes fechados ficam na carteira (contrato criado com o vendedor, cliente já vinculado ou lead atribuído ao usuário ligado ao vendedor). Os percentuais são congelados em cada contrato no momento da criação.
- **Contrato de parceria:** na tela do vendedor, "Gerar contrato" cria o contrato (modelo "Parceria comercial — vendedor") assinado pelo vendedor e pelo dono; não gera cobranças.
- **Comissões:** geradas automaticamente quando uma cobrança de IMPLANTACAO/MENSALIDADE é paga (sobre o valor efetivamente recebido), uma por cobrança. `PAYOUT_MODE=manual` (padrão): pague o Pix e use "Marcar paga". `PAYOUT_MODE=efi`: envio automático via Efí (`PUT /v3/gn/pix/:idEnvio`), exige escopo `pix.send`, webhook associado à chave pagadora (`EFI_PIX_KEY`), conta Efí Empresas e limite de envio liberado pela Efí. O Asaas não é usado para repasses.

## WhatsApp por vendedor (Evolution)

Cada vendedor pode ter o próprio número. Em **Vendedores → Conectar / gerenciar** (permissão whatsapp.manage) clique em *Conectar*: o sistema cria a instância `samos_seller_{id}` na Evolution, configura o webhook (`APP_URL/api/webhooks/evolution`, token em `EVOLUTION_WEBHOOK_TOKEN` gerado automaticamente se vazio) e mostra o QR code. As mensagens são roteadas pela instância do payload.

- `whatsapp.all`: vê as conversas da empresa e de todos os vendedores.
- Vendedor ligado a um usuário (campo *Usuário* do vendedor) vê só o próprio chat; demais usuários veem só o número da empresa.
- Chats de vendedores nunca acionam o agente de IA.

