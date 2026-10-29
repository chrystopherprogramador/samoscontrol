# Segurança

## Implementado
- Senhas com PBKDF2 (ASP.NET Core Identity hasher); política mínima de 10 caracteres com letras e números; bloqueio de 15 min após 5 falhas; mensagem de erro genérica; inativos não logam e sessões ativas de usuários desativados são invalidadas.
- Admin inicial via `ADMIN_EMAIL`/`ADMIN_PASSWORD` — sem senha padrão no código.
- Cookie `HttpOnly`, `SameSite=Lax`, `Secure` em HTTPS; CSRF (antiforgery) global em requisições não-GET; webhooks isentos mas autenticados por token.
- Autorização por permissão granular em todos os controllers; páginas públicas apenas `/sign/{token}` (token aleatório de 192 bits) e webhooks.
- Rate limiting: login, assinatura pública e webhooks.
- CSP restritiva (`script-src 'self'`, sem scripts inline), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`.
- Webhooks exigem token (comparação em tempo constante); sem token configurado respondem 503. Idempotência por `WebhookEvents`.
- Segredos de integração salvos no painel são criptografados (DataProtection) e nunca reexibidos; logs e auditoria não registram segredos.
- Agente de IA: apenas ferramentas pré-definidas, checagem de permissão por usuário, confirmação explícita para criar cobrança/enviar mensagens etc., auditoria completa. No WhatsApp só usuários cadastrados (telefone) e com permissão `ai.use`.
- Monitoramento sem SSH remoto.
- Auditoria (`AuditLog`): login, usuários, perfis, contratos, assinaturas, cobranças, configurações, ações da IA.

## Assinatura eletrônica — aviso jurídico
O sistema registra uma trilha técnica (identificação informada, IP, user-agent, data/hora, aceite explícito, hash do documento e da assinatura, eventos de acesso e PDF final com hash). **Isso não garante, por si só, validade jurídica em todos os cenários** (por exemplo, exigência de certificado ICP-Brasil ou de verificação de identidade mais forte). A Samos deve validar com assessoria jurídica se o mecanismo atende aos contratos que pretende firmar.

## Recomendações de operação
- Use HTTPS (EasyPanel/Traefik) e `APP_URL` com `https://`.
- Defina tokens aleatórios longos para `ASAAS_WEBHOOK_TOKEN` e `EVOLUTION_WEBHOOK_TOKEN`.
- Não exponha Postgres publicamente; faça backup do banco e de `/data`.
- Mantenha `SEED_DEMO_DATA=false` em produção.
- Reporte vulnerabilidades ao responsável pelo projeto de forma privada.
