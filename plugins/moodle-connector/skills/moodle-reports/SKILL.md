---
name: moodle-reports
description: "Gerar relatórios agregados Moodle de notas, desempenho, monitoria, resumo de curso, conselho de classe ou pós-execução, com cobertura e limites explícitos. Use quando o usuário pede um relatório composto ou exportação. Não use para uma consulta pontual nem para auditoria estrutural detalhada da sala."
---

# moodle-reports

Use esta skill para saídas agregadas e reproduzíveis. Relatório é leitura/análise; não envia mensagem nem altera nota.

## Escolha da tool

- `generate_course_summary`: resumo rápido de matrícula e acesso.
- `generate_course_grades_report`: notas totais por estudante em conteúdo estruturado.
- `export_course_grades_excel`: mesmo domínio de notas em arquivo Excel para distribuição/arquivo.
- `generate_weekly_performance_report`: acesso, notas, entregas e classificações indicativas.
- `generate_class_council_report`: situação pedagógica indicativa para discussão colegiada.
- `generate_full_post_execution_report`: situação provável ao fim do curso, preservando desconhecidos.
- `generate_monitor_class_report`: matrícula e acesso para monitoria.
- `audit_virtual_classroom_checklist`: checklist estrutural; para auditoria detalhada use `moodle-classroom-audit`.

## Fluxo

1. Resolva conexão, curso, período/janela, população e denominador.
2. Use `moodle-pedagogy` apenas quando o relatório exigir interpretação pedagógica, risco, recuperação ou conselho; não é necessário para exportação factual simples.
3. Preserve limiares informados pelo usuário e reporte defaults efetivos da tool quando relevantes.
4. Valide [references/report-evidence-contract.md](references/report-evidence-contract.md) antes de apresentar um relatório como completo.
5. Não some atividades localmente para substituir a nota total retornada pelo Moodle.
6. Para entregas, use `DeliveryCoverage` e `UnknownAssignmentsCount`; `TotalAssignments`/`EligibleAssignmentCount` ficam nulos quando o inventário elegível é desconhecido. `SubmittedCount` é soma de envios confirmados, nunca `total - pendentes`. Avaliação desconhecida não apaga um envio comprovado, e avaliação/correção não substitui o estado de entrega. A tool de pendências e os relatórios usam modo de submissão e entregas live; snapshots de entregas não substituem essa consulta.
7. `unknown` e cobertura incompleta não significam situação regular nem ausência de risco. Confira motivos e não trate `status=ok` como prova de cobertura.

## Saída

Ao compor leituras independentes, aplique [a regra de composição](../moodle-core/references/independent-reads.md). Preserve fontes bem-sucedidas e marque dimensões indisponíveis; status=ok de uma tool não comprova cobertura completa do relatório.

Informe `generatedAt`, população/denominador, fontes, cobertura, capabilities ausentes, filtros/limiares e falhas parciais quando disponíveis. Categorias como risco ou conclusão provável são indicativas, não decisões oficiais.
