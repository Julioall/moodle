# Moodle

Conector independente que expõe o Moodle ao ChatGPT via MCP e oferece um portal web para contas, conexões Moodle e fluxos acadêmicos. Leituras usam a conexão autorizada do usuário. Escritas disponíveis exigem permissão e confirmação pelo fluxo previsto.

## Início rápido

Para usar o serviço, crie uma conta no portal, cadastre uma conexão Moodle e conecte o ChatGPT ao endpoint MCP fornecido pelo ambiente.

Para desenvolvimento local, instale o Docker Desktop e suba a stack:

```

Materiais pedagógicos institucionais locais em `public/pedagogic/` não são versionados nem
incluídos na publicação. O mecanismo de busca de orientações funciona sem essa base; só distribua
materiais nesse diretório quando houver autorização explícita de redistribuição.powershell
docker compose --project-directory deploy --project-name moodle-conector -f deploy/docker-compose.local.yml up -d --build
```

## Entenda e opere o projeto

- [Referência da aplicação e dos dados](docs/application-reference.md) — estado atual, dados, escopos, retenção e fontes de autoridade.
- [Índice da documentação](docs/README.md) — segurança, integrações, operação e contratos.
- [Certificação de releases](docs/operations/release-certification.md) — gates locais e CI.
- [Compose de produção](deploy/docker-compose.yml) e [workflow de deploy](.github/workflows/deploy-vps.yml).

Este projeto não é oficial do Moodle HQ. A referência da aplicação e do modelo de dados deve ser consultada antes de inferir comportamento a partir de uma spec ou de um relatório antigo.
