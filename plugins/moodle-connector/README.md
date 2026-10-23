# Moodle

Plugin portátil para ChatGPT e Codex. Ele distribui as skills do Moodle e configura, em
`mcp.json`, o servidor MCP remoto em `https://novascript.com.br/mcp`.

## Conteúdo

- `plugin.json`: manifesto portátil e informações de apresentação.
- `mcp.json`: conexão MCP remota por Streamable HTTP.
- `skills/`: 13 skills reutilizáveis para trabalho acadêmico no Moodle.
- `evals/skill-trigger-cases.json`: casos positivos, indiretos, negativos e de borda para revisar
  o acionamento das skills.

As skills atuais são:

`moodle-core`, `moodle-memory`, `moodle-courses`, `moodle-content`, `moodle-assignments`,
`moodle-students`, `moodle-grading`, `moodle-follow-up`, `moodle-forums`, `moodle-messaging`,
`moodle-pedagogy`, `moodle-reports` e `moodle-classroom-audit`.

## Conexão e autenticação

O pacote não referencia MCP Apps registrados ou identificadores de conexão associados a uma conta.
Cada instalação lê o endpoint público de `mcp.json` e cria sua própria conexão no ChatGPT/Codex. Quando
solicitado, o usuário autentica pelo OAuth do Moodle; o servidor associa as tools à conta
e à conexão Moodle autorizadas.

O servidor OAuth usa authorization code com PKCE e Dynamic Client Registration (DCR). O cadastro
aceita somente callbacks HTTPS do ChatGPT. Não inclua tokens, códigos OAuth, cookies ou credenciais
Moodle no pacote.

Para conexões novas, o servidor precisa publicar `registration_endpoint` em
`/.well-known/oauth-authorization-server`. O pacote não consegue ativar cadastro OAuth se a versão
do servidor em produção ainda não oferecer esse endpoint.

## Skills e tools MCP

Conectar o servidor MCP disponibiliza tools, mas não instala automaticamente as skills. O pacote
inclui ambas as partes: o host carrega as skills de `skills/` e a configuração MCP de `mcp.json`.

O servidor não implementa a extensão experimental MCP `io.modelcontextprotocol/skills`; as skills
são distribuídas pelo pacote.

## Validar e gerar o pacote

Na raiz do repositório:

```powershell
./scripts/validate-plugin.ps1
./scripts/package-moodle-plugin.ps1
```

O gerador valida os manifestos e cria `artifacts/moodle-plugin.zip`. A disponibilidade de
plugins e skills pode variar conforme o plano, workspace, permissões e superfície do ChatGPT/Codex.
