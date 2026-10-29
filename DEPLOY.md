# Deploy no EasyPanel

## 1. PostgreSQL
Crie um serviço **Postgres** no projeto (ex.: nome `samos-db`). Anote usuário, senha e banco. O host interno é o nome do serviço (ex.: `<projeto>_samos-db`; confira na tela do serviço).

## 2. Aplicação
Crie um serviço **App** com origem Git (ou upload do ZIP) e build por **Dockerfile** (raiz do projeto).

| Item | Valor |
|---|---|
| Porta da aplicação | **8080** |
| Health check | HTTP `GET /health` (porta 8080) |
| Volume persistente | montar em **`/data`** (contratos, PDFs, uploads, chaves de proteção de dados) |
| Instâncias | **1** (scheduler/monitoramento rodam no processo) |

### Variáveis de ambiente (mínimo)
```
DATABASE_CONNECTION_STRING=Host=<host-interno-postgres>;Port=5432;Database=samos;Username=samos;Password=<senha>
APP_URL=https://control.seudominio.com.br
ADMIN_EMAIL=voce@seudominio.com.br
ADMIN_PASSWORD=<senha forte, mín. 10 caracteres com letras e números>
STORAGE_PATH=/data
TIMEZONE=America/Sao_Paulo
ASAAS_WEBHOOK_TOKEN=<gere um valor aleatório>      # se usar Asaas
EVOLUTION_WEBHOOK_TOKEN=<gere um valor aleatório>  # se usar WhatsApp
```
As demais (Asaas, Evolution, Ollama, Scraper, SMTP) estão em `.env.example` e podem ser definidas depois pelo painel.

> **Deploy automático (recomendado):** no EasyPanel crie um serviço do tipo **Compose** (não "App") apontando para este projeto. O `docker-compose.yml` sobe o PostgreSQL junto, liga a connection string e já traz valores padrão (admin `admin@samos.local` / `Samos@12345`, senha do banco `samos_change_me`). Nada precisa ser configurado para a primeira subida. **Troque as senhas** definindo `ADMIN_PASSWORD`, `ADMIN_EMAIL`, `POSTGRES_PASSWORD` e `APP_URL` na aba Ambiente; defina `POSTGRES_PASSWORD` antes da primeira subida, pois o banco guarda a senha inicial. Depois aponte o domínio para o serviço `samos-control`, porta 8080.
>
> **Obrigatórias (somente se usar serviço "App" separado):** `DATABASE_CONNECTION_STRING`, `ADMIN_EMAIL` e `ADMIN_PASSWORD`. Sem a connection string a aplicação encerra com "DATABASE_CONNECTION_STRING não configurada". Em *Ambiente* do serviço, cole as variáveis (uma por linha, sem aspas). Também é aceita a URL `postgres://usuario:senha@host:5432/banco` (ou `DATABASE_URL`) copiada do serviço Postgres.

## 3. Domínio, proxy reverso e HTTPS
Em *Domínios*, adicione `control.seudominio.com.br` apontando para a porta 8080 e ative HTTPS (Let's Encrypt). O Traefik do EasyPanel termina o TLS; a aplicação confia nos cabeçalhos `X-Forwarded-*` (já configurado) e emite cookies `Secure` em HTTPS. `APP_URL` deve ser a URL pública com `https://` (usada nos links de assinatura e webhooks).

## 4. Primeiro boot
O banco é migrado e o admin criado automaticamente. Verifique `https://<domínio>/health` → `Healthy`. Entre com `ADMIN_EMAIL`/`ADMIN_PASSWORD` e troque a senha.

## 5. Webhooks
- Asaas: `https://<domínio>/api/webhooks/asaas`, com o mesmo token em `authToken` (header `asaas-access-token`).
- Evolution: `https://<domínio>/api/webhooks/evolution?token=<EVOLUTION_WEBHOOK_TOKEN>` (ou header `apikey`), eventos `MESSAGES_UPSERT`, `MESSAGES_UPDATE`, `SEND_MESSAGE`.

## 6. Backup
Faça backup do banco Postgres **e** do volume `/data`. A pasta `/data/keys` guarda as chaves que descriptografam os segredos salvos no banco: perdê-la exige recadastrar os segredos pelo painel.

## Atualização
Novo deploy do serviço. Migrations rodam sozinhas ao iniciar.
