---
name: moodle-classroom-audit
description: "Auditar uma sala virtual Moodle quanto a estrutura, checklist, recursos, atividades e cobertura de evidências. Use para diagnóstico estrutural ou checklist institucional. Não use para simples listagem de curso, relatório de desempenho da turma ou alteração da sala."
---

# moodle-classroom-audit

Use esta skill para auditoria de sala virtual em modo somente leitura.

## Entradas

Resolva conexão, curso, janela quando aplicável e escopo/checklist solicitado.

## Fluxo

1. Leia estrutura com `list_course_contents` e/ou `audit_course_structure`.
2. Use `list_course_resources` quando a auditoria depender da presença de arquivos, páginas, URLs, livros, pastas ou rótulos.
3. Use `audit_virtual_classroom_checklist` quando o usuário pedir checklist institucional e preserve estados como `ok`, `ausente`, `incompleto` e `nao_verificavel`.
4. Recolha somente prazos, atividades, participantes, acesso e fórum necessários ao diagnóstico.
5. Reconcile `courseId`, `cmid`, `instanceId`, `userid` e grupo antes de calcular cobertura.
6. Aplique [references/classroom-evidence-matrix.md](references/classroom-evidence-matrix.md) antes de concluir ausência ou incompletude.
7. Se a intenção principal for um relatório agregado de desempenho/monitoria, encaminhe a `moodle-reports`.

## Limites

Curso vazio, capability ausente, módulo não verificável e módulo confirmado ausente são estados diferentes. `generate_monitor_class_report` cobre principalmente matrícula/acesso e não substitui auditoria completa. Checklist não constitui decisão oficial de aprovação, reprovação ou evasão.

## Saída

Ao combinar estrutura, atividades, participantes e fóruns, aplique [a regra de composição](../moodle-core/references/independent-reads.md): falha de uma leitura independente preserva as demais evidências e torna somente a dimensão afetada não verificável.

Apresente achados por dimensão, estado observado, fonte, cobertura e itens não verificáveis. Não altere conteúdo, visibilidade ou configuração do curso.
