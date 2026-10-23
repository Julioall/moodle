# Contrato de evidencia para relatorios

Todo relatorio composto deve carregar contexto suficiente para ser auditado e reproduzido.

## Campos minimos

- `generatedAt`, curso, periodo/janela, populacao e denominador usado.
- Ferramentas/fontes consultadas, paginas, `truncated`, capabilities ausentes e falhas parciais.
- Limiares efetivos: nota minima, dias de inatividade, limite de estudantes e filtros.
- `coveredCount/total` por bloco quando o conector fornecer esses campos.
- Separacao entre observado, desconhecido e categoria calculada.
- Para entregas, `DeliveryCoverage`, população/inventário elegíveis e número de atividades desconhecidas. Não derive entregas da diferença entre total e pendências.

## Interpretacao segura

`at risk`, `likely complete`, `recovery needed`, `regular` e `inactive` sao categorias indicativas produzidas pelo fluxo. Nao as transforme em decisoes oficiais nem preencha lacunas com presenca, SGE, satisfacao, tendencia historica ou presenca fisica nao observadas.

Quando houver cobertura parcial, reduza o escopo da conclusao e encaminhe os casos para `moodle-follow-up`. Quando a proxima acao for mensagem ou nota, use respectivamente `moodle-messaging` ou `moodle-grading`; o relatorio em si nao deve escrever no Moodle.

Nota por atividade e nota total do curso são escopos distintos. `latenessState` é autoritativo; `late=false` pode representar estado desconhecido. Estados de entrega e avaliação/correção também são independentes.
