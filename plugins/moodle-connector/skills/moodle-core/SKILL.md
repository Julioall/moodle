---
name: moodle-core
description: "Diagnosticar e rotear operações técnicas do Moodle, incluindo alias/conexão, capabilities, fallbacks, versão do runtime, cobertura e escrita em estado incerto. Use para troubleshooting ou roteamento técnico. Não use como etapa obrigatória de consultas simples já cobertas por uma skill de domínio."
---

# moodle-core

Use esta skill para resolver problemas técnicos e escolher uma rota segura quando a operação não estiver claramente coberta por outra skill Moodle.

## Entradas

Preserve a intenção do usuário, `moodleAlias` quando informado, IDs já conhecidos, filtros, erro retornado e qualquer estado de execução existente.

## Fluxo

1. Se o usuário mencionar uma instituição ou conexão, chame `moodle_list_connections`, associe o nome ao alias retornado e preserve esse alias em todas as ferramentas seguintes. Não trate o nome da instituição como termo de busca quando ele indicar a conexão. Sem conexão explícita, deixe o conector usar a padrão; não invente alias.
2. Para versão, commit ou deploy, use `get_connector_build_info`. Não use identidade de build como prova de capability Moodle.
3. Para descobrir uma rota suportada, use `moodle_list_available_flows` quando houver dúvida real de capability ou fallback.
4. Prefira tools especializadas. Use `moodle_execute_read` somente para uma função Moodle conhecida como leitura, autorizada pela conexão e sem equivalente especializado melhor.
5. Para escrita, prefira o par prepare/confirm específico do domínio. Use `moodle_prepare_write` e `moodle_confirm_write` somente quando estiverem expostos/habilitados e o fluxo registrado indicar que são a rota compatível; nunca faça escrita genérica por tentativa.
6. Se uma escrita retornar estado ambíguo como `execution_unknown`, use `moodle_reconcile_write` para reconciliar sem reenviar a operação.
7. Consulte `moodle-memory` apenas quando contexto durável puder alterar materialmente a tarefa; não faça leitura de memória por rotina.

## Leituras com vários IDs

- Prefira uma chamada à tool especializada que aceite uma lista de IDs; respeite os limites do contrato e deixe o conector dividir lotes e recuperar somente itens omitidos quando esse comportamento existir.
- Não transforme uma operação coletiva já disponível em várias chamadas MCP ou em chamadas individuais via `moodle_execute_read`.
- Confirme o schema da tool/função antes de presumir batching. Quando o Web Service recebe um único ID, cada entidade distinta exige sua própria chamada; não envie um campo-array improvisado nem descreva isso como consulta em lote.
- Para fórum, siga a otimização de `includePosts=false` e os custos por discussão documentados em [moodle-forums](../moodle-forums/SKILL.md). `postsPerDiscussion` limita a saída, não o volume lido do Web Service.

Tools de diagnóstico como `moodle_diagnose_connection`, `moodle_list_functions` e `moodle_check_function` podem existir em ambientes técnicos e permanecer ocultas em Production. A ausência delas não é evidência de falha do plugin.

## Estados que não podem ser confundidos

Para combinar várias leituras independentes, siga [references/independent-reads.md](references/independent-reads.md): preserve sucessos por operação, informe falhas/cobertura e não converta timeout em ausência. Essa regra não exige diagnóstico prévio para consultas simples.

- `funcao_indisponivel`: a conexão não oferece a função;
- `sem_permissao`: a chamada foi recusada no contexto;
- `dado_indisponivel`: a resposta não contém o campo necessário;
- `zero_observado`: o escopo consultado terminou sem registros;
- `falha_parcial`: parte das fontes ou páginas falhou;
- `truncated` ou `hasMore`: a resposta não representa o escopo completo.

## Saída

Informe a rota escolhida, conexão/IDs relevantes, cobertura e estado final. Nunca exponha tokens, senhas, cookies, códigos OAuth ou segredos.

Leia [references/connector-surface.md](references/connector-surface.md) somente quando a tarefa envolver mais de uma família de tools, fallback técnico ou reconciliação de escrita.
