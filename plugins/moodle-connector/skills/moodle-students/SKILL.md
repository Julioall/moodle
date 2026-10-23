---
name: moodle-students
description: "Resolver participantes, estudantes, matrículas, grupos, identidade e sinais de acesso recente no Moodle. Use para roster, grupos ou desambiguação de usuário. Não use para classificar risco, analisar desempenho, corrigir atividades ou enviar mensagens."
---

# moodle-students

Use esta skill para identidade, participantes, grupos e acesso, em modo leitura.

## Entradas

Preserve `moodleAlias`, `courseId`, `userid`/`studentId`, nome/e-mail apenas como pistas e filtros de grupo/acesso.

## Fluxo

- Participantes: `list_course_participants` ou `list_course_students`.
- Grupos: `list_course_groups` e `get_group_members`.
- Acesso recente: `list_students_without_recent_access` quando houver uma janela/limiar definido.
- Se uma busca específica de usuário exigir função Moodle `core_enrol_*` ou `core_user_*`, use o fallback genérico somente quando `moodle-core` validar a capability.
- Encaminhe risco/pendências a `moodle-follow-up`, nota a `moodle-grading` e contato a `moodle-messaging`.

## Identidade

O `userid` Moodle é autoritativo dentro da conexão atual. Se nome, e-mail ou identificador curto produzir múltiplos candidatos, não escolha por aproximação. Nunca reutilize `userid` de outra conexão.

## Completude

Roster, grupos e `lastaccess` podem ser parciais. Diferencie ausência observada, campo indisponível, capability ausente, permissão negada e paginação incompleta.

## Saída

Retorne apenas os participantes/identificadores necessários e a evidência de acesso/grupo solicitada. Não derive risco ou desempenho apenas de identidade ou acesso.
