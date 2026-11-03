# Lead Score (Entrega 2 — planejado)

Engine determinística com pesos configuráveis pelo admin (segmento de moda, multiloja, e-commerce, físico+online, tamanho/cor, expansão, ERP defasado, contratação, dor, intenção). Faixas: 0-39 baixa · 40-59 nutrir · 60-74 bom · 75-89 quente · 90-100 extremamente quente. Histórico em `LeadScoreHistory`. A LLM nunca define o score.
Na Entrega 1 o `ConversationState.InterestLevel` (0-3) é só sinal conversacional.
