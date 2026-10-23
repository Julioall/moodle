---
name: moodle-content
description: "Localizar e ler conteúdo interno de cursos Moodle, incluindo arquivos, páginas, URLs, quizzes e pacotes SCORM. Use quando a resposta depender do conteúdo do recurso. Não use para simples estrutura do curso, submissões de estudantes, tentativas/nota de quiz ou progresso SCORM."
---

# moodle-content

Use esta skill quando for necessário acessar o conteúdo real de um recurso do curso.

## Entradas

Preserve conexão, curso, `cmid`/instance id, tipo de recurso e qualquer recurso específico indicado pelo usuário.

## Fluxo

- Recursos gerais: `list_course_resources`.
- Arquivos: `list_course_files`.
- Páginas: `list_course_pages`.
- URLs: `list_course_urls`.
- Quizzes: `list_course_quizzes` e `get_quiz` para metadados; não inferir tentativas ou notas.
- SCORM: `list_course_scorms` e `ler_scorm` para conteúdo do pacote.

Quando uma resposta fornecer URL autorizada `pluginfile.php`/`webservice/pluginfile.php` e os bytes forem necessários, use `moodle_download_file`, repetindo o mesmo `moodleAlias` usado na listagem quando ele estiver definido. Nunca reconstrua, exponha ou acrescente token à URL.

Depois do download, leia o arquivo com as capacidades de arquivo disponíveis na sessão. Não inferir conteúdo pelo nome, extensão ou MIME.

## Limites

Listar um recurso não significa ter lido seu conteúdo. Diferencie conteúdo vazio, recurso indisponível, permissão negada, capability ausente e leitura truncada. `ler_scorm` lê o ZIP, o manifesto, os SCOs e arquivos textuais limitados do pacote (incluindo assets comuns). Para pacotes acima do limite de extração, retorna `contentExtractionStatus=resource_only`, `contentCoverage=none` e um `packageResource`/link MCP; nesse caso, use a URI `moodle://resource/{id}` para ler o ZIP autorizado, sem presumir que o conteúdo textual foi extraído. Confira `contentExtractionStatus`/`contentCoverage`: `middleware_only` ou `structural_only` não comprovam extração do conteúdo pedagógico, e cobertura `partial` exige limitar a conclusão. A tool não prova tentativa, conclusão ou nota do estudante.

## Saída

Indique qual recurso foi lido, seus identificadores e o conteúdo/fato realmente observado. Se a leitura não foi possível, declare o motivo sem preencher lacunas.
