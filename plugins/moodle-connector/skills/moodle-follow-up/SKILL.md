---
name: moodle-follow-up
description: "Identificar estudantes que precisam de acompanhamento a partir de acesso, pendências, atrasos, participação, notas e sinais de risco observáveis no Moodle. Use para triagem e próximos passos. Não use para enviar mensagens, lançar notas ou produzir um relatório institucional completo."
---

# moodle-follow-up

Use esta skill para triagem de acompanhamento. Ela identifica evidências; não contata estudantes automaticamente.

## Entradas

Defina conexão, curso, população, janela temporal e limiares relevantes. Preserve critérios explícitos do usuário; se uma tool aplicar default, reporte o valor efetivo quando disponível.

## Fluxo

1. Consulte somente os sinais necessários: `list_students_without_recent_access`, `list_students_with_pending_submissions`, `list_pending_submissions`, `list_late_submissions`, `list_students_without_forum_participation`, `list_students_below_min_grade`, `get_student_activity_grades`, `report_students_at_risk` ou `generate_weekly_performance_report`.
2. Use `moodle-pedagogy` quando a interpretação exigir orientação institucional de recuperação, feedback ou intervenção.
3. Reconcile `userid`, curso e atividade antes de combinar sinais.
4. Diferencie nota por atividade (`list_students_below_min_grade`) de nota total do curso (`report_students_at_risk`). Uma lista vazia por atividade não prova que a nota total esteja acima do mínimo.
5. Use `latenessState` como autoridade; `late=false` pode significar atraso desconhecido. Entrega (`DeliveryState`) e avaliação/correção (`SubmissionEvaluationState`) são sinais separados.
6. Não invente limiar de inatividade, atraso ou risco. Ausência de capability é desconhecido, não evidência negativa.
7. Se houver truncamento ou falha de fonte, marque cobertura parcial e não apresente a triagem como censo.
8. Se o usuário decidir contatar os casos identificados, encaminhe a `moodle-messaging`.

`DeliveryCoverage`, `EligibleAssignmentCount` e `UnknownAssignmentsCount` são necessários para interpretar contagens de entrega. Quando o inventário não for conhecido, total e quantidade desconhecida permanecem nulos; não calcule entregues como total menos pendentes nem sugira destinatários a partir de lacunas.

## Saída

Para cada caso relevante, apresente estudante, evidência observada, fonte, janela/limiar aplicado e cobertura. Sugira próximo passo somente quando sustentado pela evidência e pela orientação pedagógica recuperada; não fabrique um “score de confiança”.
