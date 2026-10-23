---
name: moodle-memory
description: "Consultar, salvar ou remover memória privada do Moodle para contexto durável não sensível, como alias, decisões, caminhos e modelos reutilizáveis. Use quando o usuário pedir memória persistente ou quando um modelo salvo for necessário. Não use para memória geral do ChatGPT nem para dados pessoais/acadêmicos de estudantes."
---

# moodle-memory

Use esta skill para contexto durável interno do conector. Não confunda essa memória com a memória geral do ChatGPT.

## Entradas

Determine se a intenção é listar, ler, salvar ou remover; preserve `moodleAlias` e `courseId` quando o contexto for específico.

## Fluxo

- Liste memórias curtas com `manage_user_memory` usando `action='listar'`.
- Salve memórias curtas com `action='salvar'` somente nas categorias suportadas e com `origin` correto.
- Remova memória curta com `action='remover'` somente após identificar o `memoryId` e quando houver intenção explícita de remoção.
- Use `list_user_memory_documents` e `read_user_memory_document` para modelos/referências extensas.
- Use `save_user_memory_document` para documentos duráveis em markdown, HTML ou texto.
- Use `remove_user_memory_document` somente quando a remoção do documento for solicitada.

Não persista informação apenas porque apareceu na conversa. Para `origin='inferred'`, aceite somente fatos estáveis, não sensíveis e claramente reutilizáveis; se a utilidade durável for incerta, não salve.

## Segurança

Nunca salve senhas, tokens, cookies, códigos OAuth, dados pessoais de estudantes, notas individuais identificáveis ou conteúdo sensível desnecessário. Não transfira memória entre aliases ou cursos sem evidência explícita.

## Saída

Diga o que foi lido, salvo ou removido e o escopo (`moodleAlias`/`courseId`) quando relevante. Não reproduza conteúdo sensível além do necessário.
