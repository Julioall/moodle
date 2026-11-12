# Moodle Conector

Servidor MCP para conectar o Moodle ao ChatGPT, com portal web, autenticação OAuth e fluxos acadêmicos. Consultas usam a conexão Moodle autorizada do usuário. Ações de escrita exigem autorização e confirmação quando habilitadas.

## Início rápido local

O repositório tem dois modos independentes:

- **Desenvolvimento local simples:** `deploy/docker-compose.local.yml`, em `http://127.0.0.1:8787`. **Não exponha este modo a túnel público:** ele usa defaults de desenvolvimento, que não oferecem a proteção de autenticação necessária.
- **MCP local + plugin privado do ChatGPT:** `deploy/docker-compose.chatgpt-local.yml`, em `http://127.0.0.1:8788`. Inicia em modo Production com OAuth/JWT, senhas locais próprias e operações de escrita desligadas. Para conectar ao ChatGPT por HTTPS, consulte **[Guia de MCP local para ChatGPT](docs/operations/local-mcp-chatgpt.md)**.

Para rodar o modo simples com Docker Desktop:

```powershell
docker compose -f deploy/docker-compose.local.yml -p moodle-conector up -d --build
```

> Materiais pedagógicos institucionais não são versionados nem distribuídos sem autorização. O sistema funciona sem eles. O modo local para ChatGPT não dispensa autorização institucional para acessar dados reais.

## Documentação

- [MCP local para ChatGPT](docs/operations/local-mcp-chatgpt.md)
- [Referência da aplicação e dos dados](docs/application-reference.md)
- [Índice da documentação](docs/README.md)
- [Certificação de releases](docs/operations/release-certification.md)
- [Compose de produção](deploy/docker-compose.yml) e [workflow de deploy](.github/workflows/deploy-vps.yml)

Este projeto não é oficial do Moodle HQ.
