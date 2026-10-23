---
name: moodle-forums
description: "Ler discussões e posts de fóruns Moodle, analisar participação, redigir conteúdo de fórum e publicar com prévia/confirmacão. Use para atividades de fórum. Não use para mensagens privadas ou para criar ação pendente quando o usuário quer apenas um rascunho de texto."
---

# moodle-forums

Use esta skill para leitura, análise e publicação controlada em fóruns.

## Leitura

- Se o usuário citar uma instituição ou conexão, chame `moodle_list_connections` e use o alias correspondente em `moodleAlias` em `read_forum` e nas demais chamadas do fluxo.
- Use `read_forum` para discussões e posts.
- Se o usuário quer somente listar discussões/resumos, sem ler o conteúdo dos posts, passe `includePosts=false`. Nesse modo, `read_forum` não chama `mod_forum_get_discussion_posts` para cada discussão.
- Com `includePosts=true`, o conector chama `mod_forum_get_discussion_posts` uma vez para cada discussão retornada na página. Essa função recebe um `discussionid` por chamada; não há leitura coletiva de posts por lista de IDs no contrato usado pelo conector.
- `postsPerDiscussion` limita quantos posts entram no resultado, mas o Moodle ainda retorna os posts da discussão antes desse corte local. Limite `pageSize` ao número necessário para reduzir discussões e chamadas; use `postsPerDiscussion` para limitar o conteúdo apresentado, sem tratá-lo como redução da leitura no Moodle. Não repita `read_forum` individualmente por discussão quando uma página puder cobrir o escopo.
- Use `list_students_without_forum_participation` somente depois de definir população e escopo do fórum.
- A análise de participação precisa ler posts das discussões e também faz chamadas por discussão; `includePosts=false` em `read_forum` não altera esse fluxo.
- Preserve página, grupo, `discussionId` e falhas por discussão.
- Não conclua desengajamento com página incompleta, fórum errado ou cobertura insuficiente.

## Rascunho sem publicação

Se o usuário pedir apenas para redigir/revisar uma postagem, produza o texto sem criar `PendingAction`. Consulte `moodle-pedagogy` quando a postagem tiver finalidade pedagógica institucional.

## Publicação

1. Resolva curso, fórum, discussão, grupo e conexão.
2. Crie a prévia com `create_forum_post_preview` somente quando houver intenção de publicar.
3. Mostre destino, assunto/texto literal, contexto, `pendingActionId` e `confirmationText`.
4. Execute `confirm_forum_post` somente após a confirmação exigida, usando exatamente o texto retornado.
5. Se a escrita falhar ou ficar incerta, não marque como concluída nem repita automaticamente; use `moodle-core` para tratamento técnico se necessário.

Funções `mod_forum_*` são Web Services Moodle, não tools diretas. Mensagem privada pertence a `moodle-messaging`.

## Saída

Em leitura, mostre apenas o escopo necessário e a cobertura. Em publicação, diferencie rascunho, prévia criada, ação confirmada e falha/incerteza.
