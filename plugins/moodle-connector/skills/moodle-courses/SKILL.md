---
name: moodle-courses
description: "Localizar, resolver e inspecionar cursos Moodle, incluindo identificação, seções, módulos, atividades e prazos. Use para descobrir ou entender a estrutura de um curso. Não use para ler conteúdo interno de arquivos/SCORM, analisar submissões, estudantes ou notas."
---

# moodle-courses

Use esta skill para resolver o curso correto e inspecionar sua estrutura sem alterar o Moodle.

## Entradas

Preserve `moodleAlias`, `courseId`, `shortName`, `idnumber`, nome/categoria informados, filtros e paginação.

## Fluxo

1. Se o usuário citar uma instituição ou conexão, chame `moodle_list_connections` e passe o alias correspondente em `moodleAlias`. Depois liste cursos do usuário com `list_my_courses`.
2. Localize por identificador ou nome com `search_courses` e confirme com `get_course`. Use as interfaces `search`/`fetch` somente quando estiverem expostas e forem adequadas ao host.
3. Leia seções/módulos com `list_course_contents` e `get_course_module`.
4. Use `list_course_activities`, `get_course_activity` e `list_activity_deadlines` para inventário e prazos.
5. Use `audit_course_structure` apenas quando a intenção for auditar a estrutura, não como substituto de uma simples listagem.
6. Encaminhe arquivos/páginas/URLs/quizzes/SCORM a `moodle-content`; tarefas a `moodle-assignments`; estudantes a `moodle-students`; notas a `moodle-grading`.

Nomes `core_*` e `mod_*` são funções Moodle, não tools diretas. Use `moodle-core` se for necessário recorrer ao executor genérico.

## Identificadores e parada

Não confunda `courseId`, `cmid` e `instanceId`. Se houver mais de um curso plausível e nenhum identificador permitir desambiguar com segurança, mantenha a ambiguidade e peça apenas o dado indispensável.

## Saída

Retorne o curso resolvido, identificadores relevantes e somente a estrutura necessária à pergunta. Preserve paginação, `truncated`, `hasMore` e warnings quando afetarem a conclusão.
