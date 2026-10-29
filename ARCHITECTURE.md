# Arquitetura

Monólito modular, um único processo web que também hospeda os workers (`RUN_WORKERS=true`).

```
SamosControl.Domain          entidades, enums, permissões (Perm)
SamosControl.Infrastructure  AppDbContext (EF Core + Npgsql), migrations
SamosControl.Integrations    adaptadores: Asaas, Evolution, Ollama (IAiProvider), Google Scraper; SettingKeys
SamosControl.Application     serviços de negócio por módulo (Leads, Contracts, Billing, WhatsApp,
                             Monitoring, Automation, Dashboard, SamosAgent + AgentTools, Core)
SamosControl.Web             MVC + Razor, SignalR, auth, webhooks, rate limiting, /health
SamosControl.Worker          host opcional só de workers (use com RUN_WORKERS=false no Web)
tests/SamosControl.Tests     xUnit + EF InMemory
```

## Decisões
- **Scheduler**: `SchedulerWorker` consulta `AutomationJobs` no PostgreSQL (reserva `NextRunAt` antes de executar → sem execução dupla). Sem Hangfire/Quartz/Redis. Rode **uma** instância.
- **Monitoramento**: `MonitoringWorker` executa checks HTTP/HTTPS/TCP (sem SSH), grava `ServiceCheck`, abre/fecha `Incident` e alerta via WhatsApp com cooldown, deduplicação e mensagem de recuperação.
- **Configuração**: `SettingsService` resolve painel (DB, segredos criptografados com DataProtection) → variável de ambiente. Chaves DataProtection ficam em `/data/keys`.
- **Idempotência de webhooks**: tabela `WebhookEvents` com índice único (Provider, ExternalId); eventos duplicados são ignorados.
- **Permissões**: `Role` → `RolePermission` (strings `modulo.acao`). Controllers usam `[RequirePermission]`; a checagem é por permissão, não por nome de perfil. Cache curto invalidado ao editar usuário/perfil.
- **Agente de IA**: `IAiProvider` (Ollama) apenas **interpreta** a mensagem em `{tool,args}`. O `SamosAgent` só executa ferramentas registradas (`IAgentTool`), valida permissão do usuário, exige `CONFIRMAR` para operações sensíveis (expira), e audita tudo (`AiAction` + `AuditLog`). Sem SQL/shell arbitrário. Sem LLM configurado, um interpretador por regras atende comandos básicos.
- **Assinatura**: hash SHA-256 do texto no envio, trilha de eventos, hash da assinatura, PDF final com evidências armazenado em `/data` e seu hash registrado.
- **Tempo real**: SignalR (`/hubs/notifications`) para notificações.

## Fluxo de dados
Browser → Web (cookie auth + CSRF) → Application services → AppDbContext → PostgreSQL. Integrações externas apenas via interfaces em `Integrations`, configuráveis e opcionais.
