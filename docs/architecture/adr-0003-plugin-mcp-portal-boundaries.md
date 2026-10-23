# ADR-0003: Fronteiras entre plugin, servidor MCP e portal

## Status

Aceito em execução.

## Contexto

O repositório contém um servidor MCP remoto em ASP.NET Core, um portal React/Vite, uma UI MCP
embutida, skills de orientação e metadados de submissão. Esses elementos foram descritos em parte
da documentação usando termos intercambiáveis como app, portal, plugin e MCP, o que dificulta
publicação, testes, autorização e evolução independente.

## Decisão

O produto terá cinco artefatos com responsabilidades explícitas:

| Artefato | Responsabilidade | Localização ou contrato |
|---|---|---|
| Plugin universal | Distribuição de skills e configuração portátil do MCP remoto | `plugins/moodle-connector/` |
| Servidor MCP remoto | Tools, recursos UI, OAuth/OIDC, autorização e execução Moodle | ASP.NET Core em `/mcp` |
| Portal web | Operação humana por meio de `/api`, sessão de cookie e CSRF | `src/Moodle.Web/` |
| UI MCP | Interface embutida retornada por recursos `ui://` | recurso registrado pelo servidor MCP |
| Submissão | Metadados de revisão e casos de teste de tools | `chatgpt-app-submission.json` |

O plugin não hospeda nem duplica o servidor remoto. O manifesto portátil descobre `mcp.json` na
raiz do pacote e configura o endpoint HTTPS por Streamable HTTP. O servidor autoriza conexões
ChatGPT independentes por OAuth e Dynamic Client Registration; o pacote não guarda IDs de conexão
específicos de uma conta. O portal não chama Moodle, OpenAI ou MCP diretamente pelo navegador.

No modelo de Apps SDK, o produto é **interactive-decoupled** somente no fluxo de correção: a tool
MCP entrega o recurso `ui://grading-review/v2/app.html` e o widget usa a bridge MCP. O portal
React em `/` é uma SPA humana externa ao widget; ele não é recurso `ui://`, não usa a bridge e não
deve ser classificado como app MCP interativa.

No primeiro ciclo, portal e servidor podem continuar co-hospedados no mesmo deploy. A separação
é de contratos e módulos de código; uma divisão de processos exigirá decisão posterior e evidência
operacional.

## Consequências

- O diretório `plugins/moodle-connector/` passa a ser o pacote instalável, não o projeto ASP.NET.
- Skills serão distribuídas pelo pacote; sua fonte canônica será decidida e migrada na SPEC-0002.
- O servidor continua sendo a autoridade para autenticação, schemas, tools, confirmação de escrita
  e UI MCP.
- `chatgpt-app-submission.json` contém metadados de revisão; não substitui `plugin.json` nem `mcp.json`.
- Toda alteração que atravesse essas fronteiras deve referenciar a spec ativa correspondente.

## Referências

- [Arquitetura de plugins da OpenAI](https://developers.openai.com/plugins/concepts/plugins)
- [Empacotamento de plugins da OpenAI](https://developers.openai.com/plugins/build/plugins)
- `docs/specs/spec-0000-component-boundaries.md`
