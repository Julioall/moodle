---
name: moodle-messaging
description: "Redigir, preparar e confirmar mensagens privadas Moodle para estudantes, com destinatários e evidência explícitos. Use quando o usuário quer uma mensagem ou envio pelo Moodle. Não use para identificar quem precisa de contato nem para criar ação pendente quando ele quer apenas um rascunho de texto."
---

# moodle-messaging

Use esta skill para conteúdo de mensagem e envio controlado. A skill não presume broadcast atômico, agendamento ou confirmação de leitura.

## Rascunho sem envio

Se o usuário pedir apenas para redigir/revisar a mensagem, produza o texto sem chamar uma tool `prepare_*`. Use `moodle-pedagogy` quando a comunicação depender de orientação institucional.

## Preparar e enviar

1. Resolva conexão e destinatários pelo `userid` da conexão atual. A seleção de candidatos pertence a `moodle-follow-up`.
2. Escolha o par apropriado: `prepare_welcome_message`, `prepare_access_reminder`, `prepare_activity_reminder`, `prepare_recovery_message`, `prepare_closing_message` ou `prepare_followup_message`, seguido do `confirm_*` correspondente.
3. Mostre destinatários, motivo, texto literal, escopo, `pendingActionId`, expiração e `confirmationText` retornados.
4. Confirme somente após o usuário fornecer a confirmação exigida e envie exatamente o texto de confirmação esperado pelo servidor.
5. Informe o resultado por destinatário. Falha parcial permanece parcial e não deve ser repetida automaticamente.

## Leitura de conversas

Funções `core_message_get_*` são Web Services Moodle. Use-as via `moodle_execute_read` somente quando a função for conhecida, permitida e necessária. Nunca use `core_message_send_instant_messages` diretamente pelo executor genérico.

## Saída

Diferencie rascunho, prévia/pending action e envio efetivamente retornado pelo servidor. Não afirme leitura pelo destinatário sem evidência.
