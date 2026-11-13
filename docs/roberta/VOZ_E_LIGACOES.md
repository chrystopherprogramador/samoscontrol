# Roberta 2.0 — Áudios e ligações (unificação do cérebro)

## Diagnóstico (auditoria do código)
- **Cérebro único**: `SamosAgent` + `IAiProvider` (`RoutingAiProvider`: OpenClaw ou Ollama, por `ROBERTA_PROVIDER`). Ferramentas (`IAgentTool`), permissões, memória (`PersonalNotes`) e histórico (`AiMessages`) já existiam e atendiam o WhatsApp.
- **Telefonia real**: Evolution Go Calls (`ICallGateway`/`VoiceCallManager`/`VoiceSession`), STT e TTS ElevenLabs (`pcm_16000` em streaming). WhatsApp: Evolution API `message/sendWhatsAppAudio` (base64) com TTS ElevenLabs `mp3_44100_64`.
- **Ligações**: ainda usavam o motor SDR (`RobertaService.ProposeAsync`, criação de Lead/`SdrContact`/campanha oculta, textos "assistente virtual de vendas", resultados de demo).

## Causas reais dos áudios confusos
1. O texto do agente ia **cru** ao TTS (emojis, markdown, `•`, `#id`, URLs, `R$ 4.785,50`, `dd/MM/yyyy HH:mm`, `CONFIRMAR XXXXXX`, resultados de várias ferramentas concatenados). Só a ligação tinha uma limpeza básica.
2. O prompt era só "WhatsApp" e mandava manter **todos** os ids/itens de listas — incompatível com fala.
3. Respostas com código de confirmação eram enviadas como áudio (inutilizável).
4. Respostas > 1500 caracteres caíam em texto sem aviso (agora é decidido sobre o texto falado).

## Correções
- `Application/Voice/SpeechText.cs`: normalização **determinística** (sem LLM) — remove markdown/URLs/ids/emojis/blocos de código, remove frases repetidas em sequência, escreve por extenso moeda, datas, horas, porcentagem, inteiros e decimais (com concordância de gênero), siglas; não altera telefones/CPF/CNPJ nem números. `VoiceText.ForSpeech` delega a ela.
- `WhatsAppService.SendReplyAsync`: o TTS recebe o texto normalizado; histórico/mensagem guardam o original; resposta com `CONFIRMAR XXXXXX` vai como texto; log de chars/TTS ms/bytes/envio ms.
- `SamosAgent`: `AgentContext.Spoken` → prompt com **modo voz/áudio** (frases curtas, sem listas/links/ids; resumo + oferta de detalhe) e instrução pós-ferramenta adequada; histórico compartilhado entre canais (mesmo usuário, 24 h, 16 msgs).
- Canal `VOZ`: ações que exigem confirmação são **bloqueadas** (identificação por número não basta) e orientam a pedir pelo WhatsApp; nada fica pendente.

## Ligações
- `RobertaVoiceBrain` reescrito: chama o **mesmo `SamosAgent`** (`Channel=VOZ`, `Spoken=true`), normaliza com `SpeechText`, limita a 4 frases e, se truncou, envia o texto completo ao WhatsApp do próprio usuário. Sem SDR, Lead, CRM ou "demo".
- `VoiceCallManager`: só liga/atende número **autorizado + consentido + pertencente a usuário ativo com `ai.use`** (mesma regra do WhatsApp). `VoiceContactResolver` removido; `VoiceCall.LeadId` permanece nulo (sem migração).
- UI (`Voice/Index`, `Details`): painel Roberta 2.0 (telefonia, motor de IA, modelo, voz/TTS/STT, latência), textos sem SDR/comercial.

## Testes (389 passando; baseline 372)
`SpeechTextTests` (valores/datas/horas/%/markdown/duplicatas/telefones), `VoiceTests` (TTS recebe fala normalizada; código de confirmação vai como texto), `AgentTests` (VOZ bloqueia ação sensível, prompt de voz, histórico entre canais, cérebro de voz usa o mesmo agente e sem SDR), `VoiceManagerTests` (exige usuário com `ai.use`, sem Lead/SDR, inbound de número sem usuário recusado).

## Não verificado / pendências (exigem ambiente e autorização)
- Codec aceito pela Evolution API instalada para `sendWhatsAppAudio` e qualidade real da voz ElevenLabs (nenhuma chamada real foi feita).
- Telefonia ao vivo (Evolution Go Calls) e latência real.
- `previous_text`/`next_text` do ElevenLabs para continuidade entre frases de ligação: suportado pela API, **não implementado** (exigiria alterar `IVoiceSpeech`).
- Log detalhado temporário com expiração: não implementado; os logs atuais não gravam tokens nem texto.
- Código SDR legado (`Sdr.cs`, `Roberta*`) segue no repositório como código morto, fora do fluxo ativo.
- Nada foi implantado em produção; credenciais, banco e telefonia ativa não foram tocados.
