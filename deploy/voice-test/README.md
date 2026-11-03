# Evolution Go Calls — ambiente de TESTE isolado (ligações da Roberta)

Serviço **separado** da Evolution API de produção. Não reutiliza o número, o banco, os volumes nem a rede da Evolution API atual.

> Experimental. O projeto `evolution-go-calls` usa uma implementação não oficial do VoIP do WhatsApp (`purpshell/meowcaller`).
> Há risco de bloqueio do número remetente. Use um **número exclusivo de teste**, nunca o número comercial principal.

## Pré-requisitos
- Um **segundo número de WhatsApp** só para este remetente. Um aparelho vinculado não consegue ligar para a própria conta, então o seu número de teste (destino) precisa ser diferente do remetente.
- E-mail do operador para a ativação da licença do Evolution Go (`EVOLUTION_OPERATOR_EMAIL`).

## Subir (EasyPanel, projeto separado, ex.: `voice-test`)
1. Crie um projeto novo no EasyPanel (não use o projeto `systems`).
2. Crie um serviço de app com **fonte Git**: `https://github.com/luis-kovi/evolution-go-calls`, commit fixo `e21ce0dd7634f3047410c893b5e4d1c082034b84` (PR #1, onde estão `/call/dial`, `/call/answer`, `/call/hangup` e `/call/stream`). Build pelo `Dockerfile` do repositório (compila do código-fonte; **nunca** use o binário `evolution-go` versionado na raiz).
3. Crie um Postgres exclusivo (`evogo-calls-db`) e preencha as variáveis de `.env.example`.
4. **Não publique domínio nem porta.** O serviço fica acessível apenas pelo nome interno. Para o Samos alcançá-lo, o Samos Control precisa estar na mesma rede interna do projeto (ou use um domínio com autenticação, se necessário).
5. Alternativa local para quem tem Docker: `docker compose --env-file .env up -d --build`.

## Ligar ao Samos Control (Configurações → Ligações de voz)
| Campo | Valor |
|---|---|
| URL do Evolution Go Calls | `http://evogo-calls:8080` (nome interno; confirme a porta do seu serviço) |
| API key | token da instância criada no Evolution Go |
| ID da instância | usado só no stream assinado (opcional) |
| Chave de assinatura | o mesmo valor de `CALL_STREAM_SIGNING_KEY` (opcional) |
| Token do webhook | string aleatória longa |

Webhook (configurado na instância do Evolution Go): `https://control.websamos.com.br/api/webhooks/voice?token=<token>`, eventos `CallOffer`, `CallAccept`, `CallTerminate`.
Na instância, mantenha `RejectCall=false`.

Procedimento completo do teste autorizado: `docs/sales-ai/ROBERTA_VOICE.md`.
