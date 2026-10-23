---
name: moodle-grading
description: "Use quando a tarefa envolver notas, correção assistida, reavaliação, pacotes ZIP de evidências ou publicação controlada no Moodle. Não use para relatórios gerais de turma, simples status de entrega ou mensagens."
---

# moodle-grading

Use esta skill para leitura de notas e para o fluxo de correção assistida. Como envolve avaliação e possível escrita oficial, preserve evidência e confirmação humana.

## Entradas

Resolva conexão, curso, atividade(s), critérios/rubrica, nota máxima quando disponível, intenção de reavaliação e destino final: somente análise, CSV ou publicação no Moodle.

## Leitura de notas

Use `get_student_gradebook`, `get_student_activity_grades` e `list_students_below_min_grade` conforme o escopo. `list_students_below_min_grade` lista notas por atividade/SA; a nota total do curso é um item separado no gradebook. Uma lista vazia por atividade não comprova que a nota total esteja acima do mínimo. Confira cobertura antes de interpretar ausência de itens. Não some atividades localmente para substituir nota total do Moodle.

## Correção assistida

1. Inicie com `start_pending_grading_run`, restringindo por `courseId` e `assignmentIds` quando aplicável. A tool atual não filtra o lote por estudante; não afirme que `studentId` limitou o run.
2. Leia todas as páginas necessárias de `prepare_ai_grading_batch` usando `page`/`nextPage` enquanto `hasMore=true`; confira cobertura global, não apenas a página atual.
3. Com mais de um item elegível e cliente capaz de abrir ZIP, use `prepare_grading_packages` uma vez para o `gradingRunId` em `per_activity`. Leia cada parte pronta sem esperar as demais. Aplique [references/grading-evidence-matrix.md](references/grading-evidence-matrix.md), leia arquivos/contexto e importe resultados com `import_grading_package_results(packageId, manifestHash, resultsJson)` conforme o schema. A importação salva rascunhos, nunca publica.
4. Para um item, cliente sem suporte a ZIP ou itens sem cobertura no pacote, leia resources de submissão e `contextResources` individualmente. Use `save_ai_grading_batch` somente nesse caminho individual.
5. Conte a cobertura pela união de itens importados e itens corrigidos pelo fallback; não duplique nem substitua rascunhos existentes. Evidência insuficiente fica para revisão humana. Não invente conteúdo, rubrica, nota máxima ou evidência.
6. Escolha um destino: `export_grading_corrections_csv` para arquivo externo, ou publicação no Moodle.
7. Para publicação, chame `plan_grading_publication(batchJobId)` e leia todas as páginas (`pageSize` até 25 grupos). Planeje cobre o ledger local, sem consultar Moodle nem autorizar escrita. Se houver aviso de cobertura incompleta, não publique o subconjunto como se fosse completo; corrija a cobertura. Itens em ações ativas são omitidos e as referências em `activeActions` devem ser acompanhadas antes de novo envio.
8. Para cada grupo retornado, crie uma ação independente com `create_batch_grade_launch_preview(batchJobId, gradingItemIds=...)`. Apresente todas as páginas da prévia e obtenha aprovação vinculada exatamente às prévias exibidas. O tamanho retornado em `recommendedMaxItems` é uma hipótese operacional; o padrão 20 ainda precisa de homologação.
9. Depois da aprovação afirmativa inequívoca, confirme cada ação aprovada com `confirm_batch_grade_launch(pendingActionId)`, em sequência. Não exija frase literal; negação ou resposta ambígua nunca confirma. `authorized` significa autorização/enfileiramento, não conclusão.
10. Acompanhe cada ação por `get_grading_publication_status(pendingActionId)` até todos os itens selecionados terem `publicationState=succeeded` e `commitStatus=Succeeded`. `retry_scheduled` mantém a autorização original; não peça nova aprovação. `unknown` exige reconciliação antes de qualquer nova escrita.
11. Consulte `get_assisted_grading_batch_status` até cada item ficar `commitStatus=Succeeded`, `Failed`, bloqueado ou exigir reconciliação. Em timeout, consulte o mesmo run/ação antes de repetir.

### Comparação exata opcional de arquivos

Use `compare_submission_to_selected_model` somente quando o usuário selecionar explicitamente um resource de submissão e um anexo de contexto oficial da mesma atividade para esta comparação. Passe as URIs opacas retornadas pelo conector; a escolha do anexo como modelo vale apenas para essa chamada e não é inferida pelo nome ou pelo conteúdo. Igualdade exata de bytes é apenas um sinal técnico: não prova autoria, cópia ou fraude, não altera nota e exige revisão humana antes de qualquer interpretação acadêmica ou disciplinar. A resposta não expõe bytes nem hashes; a leitura ainda pode registrar o hash de integridade do resource no fluxo padrão.

## Pacotes ZIP de evidência

Use esta rota quando o usuário pedir um pacote ZIP ou quando a correção for conduzida pelo fluxo de pacotes. Ela complementa a leitura individual de resources; importar resultados do pacote cria rascunhos locais e não publica notas.

1. Use o `gradingRunId` de `start_pending_grading_run` para reunir os sublotes da execução; use um `batchJobId` quando o escopo deve ficar restrito a um único sublote. Passe esse identificador em `prepare_grading_packages`.
2. `packageMode="per_activity"` agrupa por atividade e contexto compartilhado e é o padrão. Use `per_submission` quando for necessário um ZIP por entrega. Uma atividade pode gerar várias partes por causa dos limites de transporte; guarde `activityGroups` e cada `packageId`/`gradingItemId`. Para cobertura entre partes, conte a união de `gradingItemIds`, não a soma cega de seleções em pacotes reaproveitados ou retentativas.
3. A preparação normalmente continua em background. Consulte `get_grading_package_status` para cada pacote e verifique status, manifesto, instruções, `selectedItems`, `includedItems`, `pendingItems`, `issues`, hashes e expiração. Processe somente itens incluídos e com evidência lida; cobertura parcial permanece explícita.
4. Leia os bytes do ZIP pela URI `downloadResourceUri` com `resources/read`. Se o cliente não oferecer leitura direta de resources, use `read_grading_package` com essa URI. Receber bytes não significa que o cliente consiga extrair ou interpretar o ZIP. Ao consultar `get_grading_package_status`, respeite `retryAfterSeconds` (padrão de 5 s, máximo de 30 s) por até 120 s no total; depois faça fallback apenas dos itens ainda sem evidência.
5. Se o ZIP estiver ausente, expirado, indisponível, ilegível ou num formato que o cliente não consiga abrir, use o fluxo individual como fallback. Chame `prepare_ai_grading_batch` para o mesmo `gradingRunId`/escopo (ou `batchJobId`) e percorra todas as páginas. Reconcilie os itens por `gradingItemId`, submissão e contexto; use as URIs reais de submissão e `contextResources` retornadas pelo lote, lendo-as com `resources/read` ou `read_moodle_resource`. Não construa URIs a partir de `artifactId` nem trate caminhos do manifesto como URIs. Se o schema do pacote não puder ser lido, não chame `import_grading_package_results` para esses itens: faça propostas com base nos resources individuais realmente lidos e salve os rascunhos com `save_ai_grading_batch`. Itens sem conteúdo/contexto verificável permanecem pendentes para revisão humana.
6. Quando o ZIP puder ser aberto, leia `manifest.json`, `instrucoes.md` e `formato-resposta.schema.json`. Preserve `gradingItemId`, `submissionContentHash` e o `contextHash` específico de cada `items[]`; não substitua esse hash pelo `activity.contextHash`. Use apenas `artifactId` e evidências realmente presentes na entrega; sem evidência suficiente, marque para revisão humana em vez de inventar nota ou feedback. O pacote preserva a identidade do estudante e não é anônimo.
7. Importe cada parte legível com `import_grading_package_results` quando o status trouxer um `manifestHash` selado, usando o `packageId`, esse hash e o JSON conforme o schema do pacote. Revise `complete`, `pending`, `rejected`, `saved`, `unchanged` e `errors`. Importação idêntica pode retornar `unchanged`; itens ausentes, rejeitados ou `needs_review` não são correções concluídas. `pending` pode indicar resultado ausente ou `needs_review`: retente apenas itens elegíveis que ainda não foram preparados e encaminhe `needs_review` à revisão humana. Conte a cobertura pela união dos `gradingItemIds` processados no pacote e no fallback, sem duplicar itens.
8. Se um ZIP existente expirar ou seu transporte desaparecer, `regenerateTransport=true` recompõe o transporte de itens que já têm rascunho; não reabre nem sobrescreve a correção. Depois de revisar os rascunhos importados ou salvos pelo fallback, siga a prévia e confirmação do fluxo acima para publicar.

## Exceções controladas

- Use `includeAlreadyGraded=true` somente para reavaliação explicitamente solicitada.
- Use `allowOverwriteExisting=true` somente com autorização explícita para substituir nota/feedback existentes.
- Não use `allowPartial=true` por padrão; só aceite cobertura parcial após revisar os contadores definidos na matriz de evidência e deixar claro o que ficará de fora.

## Recuperação

Para runs com várias atividades, use `plan_grading_publication` para agrupar itens do ledger por atividade e publique cada grupo em uma ação independente, em sequência. Consulte todas as páginas do plano. O valor `recommendedMaxItems` informa a hipótese configurada, inicialmente 20; não é um limite do Moodle. Apresente todas as páginas da prévia de cada grupo antes de solicitar aprovação. Consulte o status pelo `pendingActionId` para acompanhar cada ação; a consulta por `gradingRunId` seleciona a mais recente ainda ativa e lista os demais IDs, mas não substitui o histórico individual.

`Succeeded` não deve ser reenviado. Acompanhe ações `Authorized`/`Executing` pela mesma referência. Para `ExecutionUnknown`, compare no Moodle atividade, tentativa e valores aprovados; feedback sem nota continua `grade=null`. Para correções, use `moodle_reconcile_write(pendingActionId, gradingItemId, resolution="auto")` para cada item incerto. A tool relê a conexão capturada e não faz POST: destino observado retorna `applied` para nota/feedback, sem comprovar autoria da escrita ou liberação/workflow; baseline retorna `inconclusive`; divergência retorna `conflict`. Baseline, leitura incompleta e conflito permanecem incertos, pois não provam que a requisição terminou ou não foi aplicada. Não crie nova prévia para esses itens. Para outros tipos de escrita, `executed`/`not_applied` continuam exigindo evidência explícita.

Use `requeue_blocked_grading_items` para análise bloqueada, `requeue_failed_grading_publication_items` para publicação comprovadamente falha, `find_grading_correction_by_submission` para localizar lote por `submissionId`, e `cancel_assisted_grading_batch`/`cancel_grading_batch` para cancelamento local conforme o contrato. Não divida/reenvie um item com estado incerto antes de reconciliá-lo. Se uma atividade ainda falhar depois da reconciliação, reduza apenas aquele grupo até itens individuais.

## Saída

Diferencie rascunho salvo, CSV exportado, publicação autorizada, publicação confirmada por item e falhas/bloqueios. Não apresente autorização assíncrona como nota já gravada no Moodle.
