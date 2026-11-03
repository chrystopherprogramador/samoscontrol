# Roberta Voice Calls 1.0 (prova de conceito)

Ligações de voz pelo WhatsApp conduzidas pela Roberta, reutilizando o motor comercial existente.
**Status honesto: o código interno está implementado e testado de forma simulada. Nada foi provado na rede do WhatsApp.**
A funcionalidade nasce **desligada** (`VOICE_CALLS_ENABLED` vazio = false).

## 1. Viabilidade do Evolution Go Calls

Investigado em `goippro-main/evolution-go-calls` e no PR #1 de `luis-kovi/evolution-go-calls`.

| Pergunta | Achado |
|---|---|
| Os endpoints existem? | No `main` do repositório de referência existe só `POST /call/reject`. `dial`, `answer`, `hangup` e `GET /call/stream/:callId` estão no **PR #1 aberto e não mesclado** (commit `e21ce0dd`). |
| Funcionam? | **Não verificado.** Go e Docker não existem na máquina de desenvolvimento, então o PR não foi compilado nem executado. Os formatos abaixo vêm da leitura do código. |
| Protocolo | Baseado em reverse engineering do VoIP do WhatsApp (`purpshell/meowcaller`, MLow, Go 1.25). Não é API oficial. Risco de bloqueio do número e de quebra a cada atualização do WhatsApp. O README do projeto diz que os endpoints não são estáveis. |
| Áudio | WebSocket JSON: `start`, `media` (`track` inbound/outbound, base64 PCM16 LE mono 16 kHz, quadros de 60 ms) e `stop`. Buffer de saída de ~3 s que descarta quadros ao encher. **Não há comando de limpar fila.** |
| Autenticação | REST: header `apikey`. Stream: `?apikey=` ou HMAC-SHA256 de `instance|callId|exp` com `CALL_STREAM_SIGNING_KEY`. |
| Fechar o WebSocket | Desliga a chamada. Reconectar não salva a ligação (cenário "WebSocket desconecta" termina como falha). |
| Webhooks | `CallOffer`, `CallAccept`, `CallTerminate`. Os nomes exatos dos campos do JSON **não foram confirmados**; o parser é tolerante e deve ser conferido com um webhook real. |
| Requisitos | Licença do Evolution Go (`/v1/register/auto`), segundo número remetente (aparelho vinculado não liga para a própria conta), `RejectCall=false`. |
| Alertas | Existe um binário de 62 MB versionado na raiz do repositório (não usar; compilar do fonte). Distinguir "rejeitou" de "não atendeu" pelo CallTerminate não é confiável. Não se sabe se há quadros de entrada antes de o cliente atender. |

**Veredito:** viável como experimento, **não** como componente de produção. Dá para construir e testar toda a camada interna agora; a prova real exige o teste autorizado abaixo.

**Alternativas (não implementadas, a decidir por você):**
1. Telefonia SIP/PSTN de um provedor (por exemplo Twilio ou Telnyx) para ligar ao celular. Perde o canal WhatsApp, mas é oficial e estável.
2. WhatsApp Business Calling API (Cloud API da Meta). Oficial, exige conta Business verificada e aprovação. Não foi avaliada em detalhe aqui.
3. Manter apenas áudio por mensagem de voz (já existe hoje).

## 2. Arquitetura

```
WhatsApp ⇄ Evolution Go Calls ⇄ (WebSocket PCM 16 kHz) ⇄ VoiceSession
   VAD (energia) → STT (ElevenLabs Scribe realtime, fallback em lote) → RobertaVoiceBrain
   → RobertaService (mesmo motor, mesma memória do WhatsApp) → TTS ElevenLabs (stream pcm_16000) → SpeechPlayer → chamada
```

- `Integrations/VoiceCallGateway.cs`: adaptador `ICallGateway`/`ICallStream` (trocável).
- `Integrations/VoiceSpeech.cs`: `IVoiceSpeech` (TTS em streaming e STT realtime/lote, mesma chave e voz já usadas).
- `Application/Voice/VoiceCore.cs`: VAD, máquina de estados, `SpeechPlayer` (ritmo em tempo real, cancelável).
- `Application/Voice/VoiceSession.cs`: orquestra a ligação (estados, barge-in, turnos, timers, latência).
- `Application/Voice/RobertaVoiceBrain.cs`: resolve lead/contato pelo telefone, chama `RobertaService.ProposeAsync(voiceCall: true)` e grava o resultado no CRM.
- `Application/Voice/VoiceCallManager.cs`: kill switch, lista de autorizados, limites, idempotência, webhook, órfãs e watchdog.
- Painel: menu **Voz → Ligações Roberta**; configurações em **Configurações → Ligações de voz (Roberta)**.

Estados: Idle, Dialing, Ringing, Connected, Listening, Thinking, Speaking, Interrupted, Ending, Completed, Failed.

Barge-in: fala sustentada (3 quadros, limiar ×2 enquanto a Roberta fala, como proteção contra eco) cancela reprodução, TTS e raciocínio pendente. Como o gateway não tem "limpar fila", o player envia com no máximo 2 quadros de antecedência.

Limitações assumidas: o VAD é por energia (qualidade em áudio de telefone real **não medida**); a proteção contra eco é só heurística de limiar; **não há streaming de tokens do LLM** (o motor devolve a resposta inteira; o streaming existe no TTS e na reprodução por frase).

## 3. Segurança e regras de contato
- Só números da lista de autorizados, com consentimento registrado (quem/quando). Sem discagem em massa.
- Limites: ligações simultâneas (padrão 1), duração (padrão 300 s), tentativas por número em 24 h (padrão 3), tempo de toque.
- Kill switch global (botão e configuração): bloqueia novas ligações e encerra as ativas em até ~2 s.
- Botão "Encerrar" por ligação; permissões `voice.view`, `voice.call`, `voice.manage` (só ADMIN recebe automaticamente; conceda aos demais perfis manualmente).
- Roberta se apresenta como "assistente virtual de vendas da Samos"; pedido de parar vira opt-out no CRM.
- **O áudio nunca é gravado nem armazenado.** A transcrição só é guardada se `VOICE_CALLS_STORE_TRANSCRIPT=true`. A base legal para gravar/transcrever e as regras de telemarketing devem ser validadas por você/jurídico.
- Credenciais mascaradas na tela e fora dos logs. Webhook autenticado por token (comparação em tempo constante).

## 4. Comparação de STT (pesquisa, **não benchmarkada aqui**)
| | ElevenLabs Scribe v2 Realtime | Deepgram | Whisper / faster-whisper |
|---|---|---|---|
| pt-BR | suportado | suportado (verificar modelo) | bom |
| Streaming | sim (WebSocket) | sim | não nativo (blocos) |
| Integração | mesma chave já usada | conta e cobrança novas | self-hosted, precisa CPU/GPU |
| Custo | consumo da conta ElevenLabs | pago por minuto | infraestrutura própria |
Escolha atual: **Scribe Realtime**, com fallback em lote pelo STT já integrado. Deepgram e faster-whisper ficam como alternativas, sem integração. Nenhum serviço pago novo foi ativado.

## 5. Custos e dependências
- ElevenLabs: TTS por caracteres e STT por tempo, na sua conta. O painel estima o custo **somente se** você informar as tarifas (`VOICE_COST_*`); sem isso mostra "n/d".
- LLM: uma chamada por turno (Ollama Cloud, como hoje).
- Evolution Go Calls: serviço extra (Go, Postgres próprio) e um segundo número de WhatsApp.
- Nada disso foi contratado ou ativado.

## 6. Como fazer um teste real autorizado
1. Suba o ambiente isolado conforme `deploy/voice-test/README.md` (sem expor portas).
2. Em **Configurações → Ligações de voz**, preencha URL, API key e token do webhook. Deixe `Ligações ativas = false` até o fim.
3. Configure o webhook da instância do Evolution Go para `/api/webhooks/voice?token=...`.
4. Em **Ligações Roberta**, autorize o **seu** número de teste (marque o consentimento).
5. Defina `Ligações ativas = true`. Confira que o aviso de pronto desapareceu.
6. Selecione o número e clique em **Ligar com Roberta**. Atenda, converse, interrompa, pergunte sobre o Axis.
7. Ao terminar, abra a ligação: estado, eventos, latências medidas, resumo comercial; o lead no CRM recebe a interação "Ligação".
8. Anote o que **realmente** aconteceu (tocou? houve áudio nos dois sentidos? a Roberta ouviu? a latência?). Em caso de problema, ative o kill switch.

## 7. Validado × simulado
| Item | Situação |
|---|---|
| VAD, estados, barge-in, silêncio, limite de duração, falhas de STT/TTS/LLM/WebSocket, idempotência, limites, kill switch, lista de autorizados, memória compartilhada WhatsApp↔ligação | **Simulado** (49 testes automatizados com relógio e rede falsos). Os 20 cenários da especificação estão cobertos de forma simulada. |
| Formato das mensagens do Evolution Go Calls | Baseado na leitura do código do PR #1; **não executado**. |
| Formato dos webhooks de chamada | **Não confirmado.** |
| Ligação real no WhatsApp, áudio bidirecional, latência real, qualidade do VAD, eco | **Não provado.** Depende do teste autorizado. |
| Latência | Medida em cada ligação (fim da fala → início do áudio, com etapas). Nenhum número é afirmado aqui. |

## 8. Riscos restantes
- Protocolo não oficial: bloqueio do número, quebras sem aviso e licença do Evolution Go.
- PR não mesclado e sem compilação verificada localmente.
- Webhooks e fluxo de entrada de áudio antes do atendimento não confirmados.
- Sem limpar fila e sem reconexão que preserve a chamada.
- Qualidade do VAD e eco em áudio real.
- Base legal para transcrição/gravação e telemarketing a validar.
