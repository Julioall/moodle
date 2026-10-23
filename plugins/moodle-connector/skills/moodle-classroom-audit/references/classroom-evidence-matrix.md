# Matriz de evidencia para auditoria da sala

Use esta matriz para evitar que um endpoint vazio seja interpretado como ausencia real.

| Dimensao | Fontes | Resultado valido | Nao concluir quando |
| --- | --- | --- | --- |
| Estrutura e modulos | `list_course_contents`, `audit_course_structure`, `list_course_resources` | Modulo confirmado, ausente ou incompleto | A pagina esta truncada, a capability falta ou o curso nao foi resolvido. |
| Atividades e prazos | `list_course_activities`, `list_activity_deadlines`, `get_assignment` | Cobertura por `courseId`, `cmid` e `instanceId` | Ha apenas uma pagina, datas incompletas ou atividade fora do periodo. |
| Participacao | `list_course_participants`, `list_course_students`, `list_students_without_recent_access` | Denominador e janela explicitos | A lista de participantes esta parcial ou o acesso nao tem timestamp confiavel. |
| Conclusao | Relatorios que fornecam evidencia de completion | Estado de conclusao quando explicitamente retornado | A consulta detalhada de completion nao esta exposta; completion nao esta habilitado ou o status e desconhecido. |
| Forum | `read_forum`, `list_students_without_forum_participation` | Participacao observada dentro da janela | Forum nao foi localizado, a janela nao foi informada ou nao ha cobertura suficiente. |
| Checklist institucional | `audit_virtual_classroom_checklist` | `ok`, `ausente`, `incompleto` ou `nao_verificavel` | O retorno nao informa regra, cobertura ou timestamp. |

## Regras de cobertura

- Informe `generatedAt`, fontes, paginas, `truncated`, capabilities ausentes e `coveredCount/total` quando disponiveis.
- Diferencie curso vazio, modulo nao verificavel, modulo confirmado ausente e consulta sem permissao.
- Reconcile IDs antes de agregar: `courseId`, `cmid`, `instanceId`, `userid` e grupo.
- `inactive`, `at risk` e `ausente` sao estados tecnicos/indicativos; nao sao decisoes oficiais de evasao, aprovacao ou reprovacao.
