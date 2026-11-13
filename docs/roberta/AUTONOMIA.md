# Roberta 2.0 — Autonomia operacional (fase 1: ligações agendadas)

## Diagnóstico (por que ela dizia "não consigo ligar no futuro")
- **Ferramenta ausente.** O agente só tinha `ligar_para_mim` (imediata) e `criar_tarefa`/`criar_job` (lembrete por WhatsApp). Não existia nenhuma ferramenta nem tipo de job que discasse no futuro.
- O prompt não era a causa: o motor (`SchedulerWorker` + `AutomationJob`, persistido no banco, reserva `NextRunAt` antes de executar) já existia e é adequado; faltava um tipo de job que usasse o `VoiceCallManager`.
- Permissões e telefonia estavam corretas (Evolution Go Calls, número autorizado + consentimento + usuário ativo com `ai.use`).

## O que foi implementado
- `JobType.PlaceCall` (valor novo no fim do enum; armazenado como inteiro, **sem migration**).
- Ferramentas do agente (mesmo orquestrador `SamosAgent`): `agendar_ligacao`, `listar_ligacoes_agendadas`, `cancelar_ligacao_agendada` (`ScheduledCallTools.cs`). Só liga para o **próprio usuário**, no número cadastrado, que precisa estar na lista de autorizados com consentimento. Se algo impede (horário passado, telefonia desativada/kill switch, número não autorizado) a ferramenta devolve o motivo real e **nada é persistido**; a confirmação só vem após o job ser gravado.
- Execução (`AutomationService.PlaceCallAsync`), máquina de estados persistida em `ConfigJson`:
  `DIAL` → chama `VoiceCallManager.StartTestCallAsync` com chave idempotente `job{id}-a{tentativa}` → `CHECK` (após o tempo de toque) lê o resultado da `VoiceCall`:
  atendida = concluída; não atendida/falha = nova tentativa (até 3, a cada 5 min); esgotadas = aviso por WhatsApp + notificação, job desativado e execução marcada como Falhou.
- Se o worker ficou fora e o horário passou há mais de 15 min, **não liga atrasado**: avisa por WhatsApp.
- A abertura da ligação cita o lembrete (normalizado para fala) e a conversa continua com o mesmo cérebro.
- Prompt: regra nova (agendar_ligacao vs. lembrete por WhatsApp; nunca declarar impossibilidade sem checar as ferramentas).
- Tela Ligações: bloco "Ligações agendadas".
- Reinício/VPS: tudo está no banco; `RunDueAsync` reserva a execução antes de rodar (sem duplicidade) e a chamada usa chave idempotente.

## Testes (397 passando; 8 novos em `ScheduledCallTests`)
Persistência, recusa sem falsa confirmação, execução no horário e escopo novo (reinício), sem discagem duplicada, atendida, retry + fallback + parada, janela perdida, cancelamento e isolamento entre usuários, abertura falada.

## NÃO implementado nesta fase (honesto)
- Executor de infraestrutura (Docker/logs/métricas/comandos cadastrados): exige um serviço executor dedicado (ex.: agente com API restrita no host), sem `docker.sock` no container. Não existe hoje; precisa de desenho e autorização para instalar algo no servidor.
- Níveis de autonomia 1–4 configuráveis, tela de Autonomia/Infraestrutura/Auditoria dedicada e tarefas multi-etapas com pausa/retomada por aprovação.
- Integração com cron/skills do OpenClaw e resumos no Letta.
- Teste real com telefonia (só com mocks); o desfecho "não atendida" depende de a Evolution Go Calls informar o fim da chamada sem `ConnectedAt`.
- Deploy: não feito (exige autorização explícita).
