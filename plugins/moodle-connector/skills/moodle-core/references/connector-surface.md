# Connector surface reference

Leia esta referência quando a tarefa envolver mais de uma família, fallback de capability, escrita genérica ou reconciliação.

## Roteamento técnico

1. Quando o usuário mencionar uma instituição/conexão, use `moodle_list_connections` para obter o alias e envie-o em todas as tools da tarefa. Fixe IDs conhecidos sem inventar conexão.
2. Prefira a tool especializada do domínio.
3. Use `moodle_list_available_flows` quando houver dúvida real sobre a estratégia/capability da conexão.
4. Use `moodle_execute_read` somente para função Moodle conhecida como leitura e sem alternativa especializada melhor.
5. Prefira pares prepare/confirm de domínio para escrita.
6. `moodle_prepare_write`/`moodle_confirm_write` existem na superfície publicada, mas são fallback controlado e podem estar desabilitados por configuração; use apenas quando o fluxo registrado os indicar.
7. Use `moodle_reconcile_write` para `ExecutionUnknown`; não reenvie a escrita.

As tools `moodle_diagnose_connection`, `moodle_list_functions` e `moodle_check_function` são diagnósticos técnicos que podem ficar ocultos em Production. Não faça a execução normal depender delas.

## Matriz de domínio

| Intenção | Skill principal | Tools especializadas típicas | Handoff |
| --- | --- | --- | --- |
| Conexões | `moodle-core` | `moodle_list_connections` | courses, forums |
| Cursos/estrutura | `moodle-courses` | `list_my_courses`, `search_courses`, `get_course`, `list_course_contents`, `list_course_activities` | content, assignments |
| Conteúdo | `moodle-content` | `list_course_resources`, `list_course_files`, `list_course_pages`, `list_course_urls`, `list_course_quizzes`, `list_course_scorms`, `ler_scorm`, `moodle_download_file` | courses |
| Tarefas/submissões | `moodle-assignments` | `list_course_assignments`, `get_assignment`, `list_assignment_submissions`, `get_student_submission`, `list_pending_submissions`, `list_late_submissions` | grading, follow-up |
| Estudantes/grupos | `moodle-students` | `list_course_participants`, `list_course_students`, `list_course_groups`, `get_group_members`, `list_students_without_recent_access` | follow-up, messaging |
| Fórum | `moodle-forums` | `read_forum`, `list_students_without_forum_participation`, `create_forum_post_preview`, `confirm_forum_post` | follow-up |
| Notas/correção | `moodle-grading` | `get_student_gradebook`, `get_student_activity_grades`, `start_pending_grading_run`, `prepare_ai_grading_batch`, `save_ai_grading_batch` | assignments, pedagogy |
| Relatórios | `moodle-reports` | `generate_course_summary`, `generate_course_grades_report`, `generate_weekly_performance_report`, `generate_class_council_report`, `generate_full_post_execution_report`, `generate_monitor_class_report` | follow-up |
| Pedagogia | `moodle-pedagogy` | `get_pedagogical_guidelines` | grading, forums, messaging, reports |
| Memória privada | `moodle-memory` | `manage_user_memory`, `list_user_memory_documents`, `read_user_memory_document`, `save_user_memory_document`, `remove_user_memory_document` | pedagogy |

## Estados distintos

- `funcao_indisponivel`: função/capability não oferecida;
- `sem_permissao`: acesso recusado no contexto;
- `dado_indisponivel`: campo necessário ausente;
- `zero_observado`: consulta completa sem registros;
- `falha_parcial`: uma parte da coleta falhou;
- `truncated`/`hasMore`: cobertura incompleta;
- `ExecutionUnknown`: escrita possivelmente enviada, exigindo reconciliação sem retry cego.
