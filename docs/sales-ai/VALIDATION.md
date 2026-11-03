# Validação de respostas

`ResponseValidator` (determinístico, autoritativo) + juiz semântico opcional via LLM (só pode acrescentar problemas).

Regras: vazio; > 400 caracteres; > 3 frases; mais de uma pergunta; pergunta em ação que não pergunta; ação de pergunta sem exatamente uma pergunta; pergunta de tópico já feito; pergunta fora do tópico da ação; similaridade (Jaccard ≥ 0,6) com as últimas 5 mensagens enviadas; palavras proibidas ("garanto", "100%", "grátis", "desconto", "sou humano"...) salvo se constarem na KB; números/preços/percentuais, URLs e nomes de ERP sem fonte.

Fluxo: gera → valida → se reprovar, regenera uma vez informando os motivos → se reprovar de novo (ou IA indisponível) escala para humano (`HandoffToSeller`). Nunca se envia resposta reprovada.

Scores por resposta (`SdrSuggestion.ScoresJson`): HallucinationScore, RepetitionScore, ConversationProgressScore, ContextUsageScore, NaturalnessScore, GoalCompletionScore (heurísticos). HandoffQuality e CommercialQuality ficam para a Entrega 4 (precisam de desfecho real).
