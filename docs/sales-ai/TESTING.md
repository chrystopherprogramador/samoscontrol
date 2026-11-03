# Testes

`dotnet test -c Release` — 79 testes originais + 157 novos em `tests/SamosControl.Tests/RobertaTests.cs`:
- `RobertaClassifierTests`: ~90 frases (opt-out, recusa, humano, preço, desconto, demo, sim/não/talvez, satisfeito, irritado, fora do ICP, perguntas, ERP, nº de lojas, tópicos).
- `RobertaEngineTests`: conversas completas (anti-loop, satisfeito, 10+ lojas, preço, demo, contradição, dados já conhecidos, limite de turnos, KB vazia).
- `RobertaValidatorTests`: respostas boas e ruins (alucinação, repetição, tamanho, proibidas).
- `RobertaFlowTests`: Shadow, regeneração/escalonamento, sem LLM, opt-out, Revisão, ativação de prompt, retry/circuit breaker (com `FakeLlm`).
