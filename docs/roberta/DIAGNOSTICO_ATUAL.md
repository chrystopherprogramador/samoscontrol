# Roberta 2.0 — Diagnóstico da implementação atual

Auditoria estática do código; nada foi executado em produção. Referências são arquivos em `src/` (SamosControl, .NET 8, EF Core/PostgreSQL).
Certeza: **[verificado]** = lido no código; **[não verificado]** = precisa de inspeção adicional antes de agir.

Estado do Git no início: branch `main`, HEAD `fe8300e`, com trabalho **não commitado** de outras entregas (integração Axis, cobrança automática, caixa central de WhatsApp). O proprietário deve criar um ponto de reversão (commit/branch) antes da fase B.

## 1. Arquitetura encontrada

Cinco projetos: `Domain`, `Infrastructure` (AppDbContext, migrations), `Integrations` (Evolution, Ollama, ElevenLabs, SMTP...), `Application` (serviços), `Web` (MVC + Razor, Bootstrap 5). Testes em `tests/SamosControl.Tests`. Os workers (`SchedulerWorker`, `MonitoringWorker`) só são registrados com `runWorkers` verdadeiro (`DependencyInjection.cs`). `SettingsService`: o banco (AppSettings) vence, a variável de ambiente é fallback; segredos em `SettingKeys.Secrets` (`Integrations/Common.cs`).

Existem **três "Robertas"** no código:

| Peça | Arquivos | Propósito |
|---|---|---|
| **SamosAgent** (assistente da equipe) | `SamosAgent.cs`, `AgentTools.cs`, `Personal.cs`, `CoreControllers.cs` (Assistant) | Chat do painel e WhatsApp para usuários com `Perm.AiUse` |
| **Roberta SDR/vendas** | `Sdr.cs` (SdrService), `RobertaEngine/Service/Sales/Strategy/Support.cs`, `Domain/Sdr.cs`, `Domain/SalesAi.cs`, `SdrControllers.cs` | Prospecção, qualificação, follow-up e resposta a leads |
| **Roberta Voz** | `Voice/*`, `Integrations/VoiceCallGateway.cs`, `VoiceSpeech.cs`, `VoiceControllers.cs` | Ligações WhatsApp em tempo real (prova de conceito, desligada por padrão) |

## 2. Mapa dos módulos

CRM (Leads, Customers), Vendedores, Contratos, Cobrança (Charges, Efí/Asaas, `ChargeNoticeService`), WhatsApp (caixa central + Evolution), Monitoramento (servidores/serviços), Automação (jobs), Pessoal (tarefas, notas, gastos), Administração (usuários, permissões `Perm.*`, configurações, auditoria `AuditService`), Integração Axis, SDR/Treinamento, Voz.

## 3. Fluxo atual das mensagens

1. Evolution chama `/api/webhooks/evolution` → `WhatsAppService.HandleWebhookAsync`: registra o evento (deduplicação persistida) e chama `StoreIncomingAsync`.
2. `StoreIncomingAsync`: guarda de duplicidade em memória (`FirstTime`) + checagem no banco por ExternalId/SellerId; cria/atualiza a conversa; se áudio, transcreve (ElevenLabs).
3. Despacho (`WhatsApp.cs` ~L230), ambos com `Task.Run`:
   - `sellerId == null && !FromMe` → `DispatchToAgentAsync`: só responde se o telefone bate com um **usuário ativo** com `Perm.AiUse`; chama `SamosAgent.HandleAsync` e responde por `SendReplyAsync` (áudio se a entrada foi áudio).
   - `!FromMe` (qualquer escopo) → `DispatchToSdrAsync` → `SdrService.HandleReplyAsync`.
4. **Problema comprovado:** mensagem do proprietário no número da empresa dispara **os dois fluxos**.

### SamosAgent [verificado em `SamosAgent.cs`]
- O LLM (`IAiProvider`) devolve intenção JSON `{tool,args}` ou `{reply}`; prompt fixo em código com 8 regras (L237), enriquecido com 20 notas de memória (`PersonalNotes`, tag de memória) e 8 tarefas.
- Histórico: últimas 16 mensagens do usuário/canal (`AiMessages`).
- Execução: valida ferramenta, permissão por usuário e argumentos; se `RequiresConfirmation`, grava `AiAction` `AguardandoConfirmacao` (TTL 10 min) e exige "CONFIRMAR/SIM". Tudo vai para `AiActions` e `AuditService`.
- Ferramentas (`AgentTools.cs`): consultar_leads/clientes/cobrancas/contratos/servidores/servicos, resumo_empresa, criar_job, sincronizar_leads, criar_pesquisa_leads*, criar_cobranca*, enviar_whatsapp*, tarefas/notas/gastos/lembrar_fato (*exigem confirmação).
- Limitações: confirmação por palavra livre, sem vínculo aos parâmetros nem nonce; sem classificação de risco além de um booleano; sem schema de parâmetros; sem cobertura de vendedores, comissões, integrações, usuários.

## 4. Fluxo de áudio

- Entrada: Evolution `GetMediaAsync` → ElevenLabs STT (`ELEVENLABS_STT_MODEL`) → texto.
- Saída: `SendReplyAsync(asAudio)` → ElevenLabs TTS (≤1500 caracteres) → `SendAudioAsync`; em falha, texto. Controlado por `ELEVENLABS_VOICE_REPLIES`.
- O ElevenLabs **faz STT** hoje [verificado: chaves `ELEVENLABS_STT_*`, `TranscribeAsync`].
- Ligações: Evolution Go Calls via WebSocket, STT realtime/batch, `RobertaVoiceBrain`, allowlist `VoiceAllowedNumber`, kill switch.

## 5. Integrações existentes

Evolution API (empresa + instâncias legadas por vendedor), Ollama (`OllamaProvider`, única implementação de `IAiProvider`, com modelo de fallback), ElevenLabs, SMTP/MailKit, Efí/Asaas, Axis. **Não existem** OpenClaw, Letta nem GitHub Copilot.

## 6. Problemas identificados

1. Dois fluxos ativos para a mesma mensagem (agente + SDR).
2. `Task.Run` sem fila: falhas só em log, sem retentativa; mensagem perdida se o processo reiniciar.
3. Prompt fixo no código; ferramentas cobrem pouco do sistema.
4. Memória: 20 notas + 16 mensagens; sem categoria, origem, validade, relevância, edição, deduplicação.
5. Provedor único (Ollama), sem seleção de modelo na UI nem observabilidade do agente.
6. Confirmação sem vínculo aos parâmetros.
7. Sem motor de eventos nem política de silêncio/frequência.
8. SDR acoplado ao webhook e ao Lead (`LeadId` em `ConversationState`, `SdrSuggestion`, `VoiceCall`).
9. Menu mistura propósitos: "Ligações Roberta", "Assistente", "Treinamento da IA", "SDR automático" (`_Layout.cshtml` L58–65).
10. `docs/sales-ai/` descreve a Roberta de vendas e ficará obsoleta.

## 7. Preservar

`WhatsAppService` (webhook, dedup, mídia, envio), Evolution, ElevenLabs/`VoiceSpeech`, `OllamaProvider`/`IAiProvider`, `PermissionService`, `AuditService`, `AiAction`/`AiMessage`, `PersonalService`, caixa central, CRM, Vendedores, e todos os dados existentes (nenhuma tabela apagada sem autorização).

## 8. Refatorar

`SamosAgent` → orquestrador + `IAgentRuntime`; `IAgentTool` → registro tipado (módulo, schema, risco, idempotência); confirmação → autorização assinada aos parâmetros; memória → `IMemoryProvider`; roteamento do webhook → destino único; menu/telas → painel único "Roberta".

## 9. Dependências e riscos

- OpenClaw: `POST /v1/responses` vem **desabilitado por padrão** e, com auth por segredo compartilhado, equivale a **acesso total de operador** ao gateway (docs.openclaw.ai/gateway/openresponses-http-api). Exige rede Docker privada, token forte, sem porta pública, e as ferramentas do Samos como **client tools** executadas pelo Samos.
- Copilot via OpenClaw: device flow feito pelo proprietário (`openclaw models auth login-github-copilot`); catálogo lido do gateway (`GET /v1/models`); cotas do plano valem.
- Letta: integração ainda não validada; PoC obrigatória (hipótese: serviço dedicado chamado pelo backend).
- Remoção do SDR toca: `WhatsApp.cs`, `Automation.cs` L173, `DependencyInjection.cs`, `RobertaService`/`RobertaSupport` (usados pela voz), `_Layout.cshtml`, `AdminControllers.cs`, testes `SdrTests`/`Roberta*Tests`.
- Banco: só migrations aditivas.
- [não verificado] conteúdo de `RobertaEngine/Strategy/Sales`, intervalos de `SdrService.RunAsync`, uso de `AiKnowledge` fora do SDR, demais chamadores de `RobertaService`.

## 10. Proposta incremental

Ver `PLANO_IMPLEMENTACAO.md` e `ARQUITETURA.md`.
