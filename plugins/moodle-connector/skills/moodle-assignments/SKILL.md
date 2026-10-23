---
name: moodle-assignments
description: "Consultar tarefas Moodle, prazos, submissões, pendências, atrasos e status de entrega. Use para saber o que foi solicitado ou entregue e por quem. Não use para atribuir nota/feedback, publicar correções ou analisar o gradebook; encaminhe essas intenções a moodle-grading."
---

# moodle-assignments

Use esta skill para tarefas e submissões sem alterar entregas nem publicar avaliação.

## Entradas

Preserve `moodleAlias`, `courseId`, `assignmentId`/instance id, `cmid`, `studentId`/`userid`, filtros temporais e paginação.

## Fluxo

1. Liste/consulte tarefas com `list_course_assignments`, `get_assignment` e `list_activity_deadlines`.
2. Consulte entregas com `list_assignment_submissions` e `get_student_submission`; `get_submission_status` é alias de compatibilidade.
3. Para subconjuntos, use `list_pending_submissions`, `list_late_submissions`, `list_submissions_awaiting_grading` ou `list_students_with_pending_submissions` conforme a intenção. Esta última lista somente pendências confirmadas; quando modo de submissão ou cobertura estiver desconhecido, preserve o estado desconhecido e os avisos.
4. Se o usuário pedir nota, feedback, leitura de anexos para avaliação ou publicação, encaminhe a `moodle-grading`.
5. Use [references/submission-inspection.md](references/submission-inspection.md) somente quando a tarefa depender de anexos, integridade/formato ou de um bundle local fornecido pelo usuário.

Funções `mod_assign_*` são Web Services Moodle; não as trate como tools diretas. Recorra ao executor genérico somente por `moodle-core` e quando a capability for conhecida.

## Completude

`hasMore`, cursor ou página parcial significa evidência incompleta. Só afirme “não entregou” depois de cobrir a tarefa, a população e as páginas relevantes. Capability ausente não equivale a zero entregas.

Use `latenessState` (`late`, `on_time`, `unknown`, `no_deadline`, `not_submitted`) como estado autoritativo. O booleano legado `late=false` também pode acompanhar `latenessState=unknown`; não o interprete como pontualidade. O filtro de atrasadas inclui somente atraso confirmado.

`list_students_with_pending_submissions` lê ao vivo modo de submissão e entregas. Não interprete uma lista vazia como ausência global quando `DeliveryCoverage.IsComplete` for falso.

## Saída

Informe tarefa/submissão resolvida, status observado, datas relevantes e cobertura. Não exponha integralmente todas as entregas em consultas coletivas sem necessidade.
