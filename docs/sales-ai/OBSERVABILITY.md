# Observabilidade

Nativa (sem Langfuse/Opik por enquanto): `LlmExecutions` registra tarefa, prompt/versão, provedor, entrada, saída, latência, ação, erro. `LlmService` é o ponto único de chamada: timeout de 45 s, 1 retry, circuit breaker (5 falhas seguidas → aberto por 60 s). Últimas 30 execuções aparecem em `/Sdr/Prompts`.

Limitações conhecidas: `Model` e tokens/custo ainda não são preenchidos (o `IAiProvider` não os expõe); roteamento de modelo por tarefa depende de estender o `IAiProvider` (Entrega 2). Reavaliar Langfuse/Opik se o volume de conversas justificar uma stack própria (exigem ClickHouse/Redis).
