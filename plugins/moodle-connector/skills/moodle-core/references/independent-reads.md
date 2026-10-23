# Composição de leituras independentes

Quando uma tarefa precisar de várias leituras sem dependência entre elas, preserve o resultado de cada operação. Resolva primeiro conexão e curso; só execute em paralelo chamadas que já tenham todos os seus identificadores autorizados. Não dispare detalhes dependentes de uma listagem ainda não concluída.

Use a semântica all-settled: aguarde todas as chamadas iniciadas, registre resultado, estado, cobertura e erro por operação. Uma exceção em uma chamada não descarta os sucessos das outras. O servidor não precisa executar fan-out para isso.

Exemplo sintético: após resolver o curso, estrutura e participantes respondem; a consulta de fórum falha por timeout.

| Operação | Resultado | Cobertura | Uso na resposta |
|---|---|---|---|
| list_course_contents | sucesso com módulos | completa para o escopo consultado | Apresentar a estrutura observada. |
| list_course_participants | sucesso com participantes | completa para a página; continuar paginação quando necessário | Apresentar população somente com o denominador coberto. |
| list_course_forums | falha por timeout | indisponível | Informar que fóruns não puderam ser verificados; não afirmar zero fóruns. |

Mesmo com status=ok, examine warnings, freshness.complete, paginação e estados dos campos antes de afirmar completude. Lista vazia em uma leitura completa é zero observado; lista vazia após erro ou com cobertura incompleta não comprova ausência. Resultado parcial mantém os dados válidos e identifica as fontes que faltam.

Não repetir todas as consultas para recuperar uma única falha. Repetição exige leitura idempotente, erro transitório reconhecido, orçamento finito e respeito ao cancelamento. Permissão/capability ausente não se resolve com repetição. Escritas e prepare/confirm não participam desse padrão.

Ao produzir relatório, mantenha as duas leituras válidas do exemplo e marque a dimensão fórum como não verificável. Não transforme falha em zero, lista vazia ou indisponibilidade global.
