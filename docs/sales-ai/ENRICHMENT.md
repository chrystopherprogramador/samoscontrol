# Enrichment (Entrega 2 — planejado, não implementado)

Camada nativa em C#, em waterfall por custo: dados do CRM → site da empresa → metadados públicos → redes públicas → fontes abertas → busca → providers gratuitos → providers pagos opcionais (só após calcular o potencial do lead).
Cada dado enriquecido guarda valor, fonte, data, confidence e método (`LeadEnrichment`). Não encontrado = `null`; nunca inventar. Execução em `BackgroundService` já existente, nunca na requisição da página.
