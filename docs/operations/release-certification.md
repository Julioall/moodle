# Certificação de releases

Use esta lista antes de abrir ou atualizar um PR. O CI remoto confirma os mesmos gates, mas
não deve ser a primeira etapa para diagnosticar uma falha conhecida.

## Gates de código

Execute na raiz do repositório:

```powershell
Push-Location src/Moodle.Web
npm ci
npm run lint
npm run typecheck
npm test
Pop-Location

dotnet restore Moodle.slnx
dotnet build Moodle.slnx --configuration Release --no-restore --no-incremental
dotnet test Moodle.slnx --configuration Release --no-build --no-restore --filter "Category!=PostgresIntegration&Category!=ManualPerformance"
```

O filtro acima cobre testes unitários e integrações com doubles, mas não substitui o gate
PostgreSQL. Para executar o gate local completo com Docker:

```powershell
./scripts/test-postgres.ps1 -Configuration Release
```

Não remova nem silencie as categorias PostgreSQL para fazer a suíte parecer verde. O CI executa
separadamente os testes marcados `PostgresIntegration` contra PostgreSQL 16.

## Gates de pacote e documentação

Com PowerShell 7 e Python instalados:

```powershell
./scripts/validate-plugin.ps1
./scripts/package-moodle-plugin.ps1
./scripts/generate-chatgpt-app-submission.ps1 -Check
python plugins/moodle-connector/skills/moodle-core/scripts/check_skill_catalog.py --repo-root .
python -c "import json; d=json.load(open('plugins/moodle-connector/evals/skill-trigger-cases.json', encoding='utf-8')); assert len(d['cases']) >= 52"
./scripts/check-documentation.ps1
python scripts/check-dockerfile-encoding.py
bash -n scripts/check-public-readiness.sh
```

## Gates de entrega

Valide localmente as configurações Compose e Caddy usadas pela mudança. Para a imagem de produção:

```powershell
docker build -f deploy/Dockerfile --tag moodle-release-validation .
./scripts/verify-production-container.ps1 -Image moodle-release-validation -RequireGradingPackageTransport
```

O workflow de CI também valida os arquivos GitHub Actions, as topologias Compose e o contrato do
container de produção. Uma alteração que afete uma dessas superfícies deve manter o gate
correspondente habilitado.

## Publicação

Confira a branch e os workflows que serão acionados antes do push. CI e validação documental
executam em `dev` e `master`; nenhum deploy é feito a partir de `dev`. O deploy de produção só
ocorre após o CI concluir com sucesso para um push em `master`, ou por acionamento manual do
workflow a partir de `master`. Operações de VPS e diagnósticos de produção também são restritos a
`master`.
