# Checklist De Segurança Por Release

Use este checklist antes de publicar uma release em VPS ou habilitar novas tools.

## Contrato ChatGPT Apps

- [ ] `/mcp` responde via HTTPS público.
- [ ] `scripts/verify-production-endpoint.ps1 -AppDomain <APP_DOMAIN> -RequireStrictTransportSecurity` passa após o deploy.
- [ ] `/.well-known/oauth-protected-resource/mcp` aponta para o authorization server correto.
- [ ] `/.well-known/oauth-authorization-server`, `/.well-known/openid-configuration` e `/.well-known/jwks` respondem sem erro.
- [ ] `tools/list` expõe `securitySchemes` OAuth quando `McpServerSecurity:RequireJwt=true`.
- [ ] Tools read-only declaram `ReadOnly=true`, `Destructive=false`, `OpenWorld=false` e `Idempotent=true`.
- [ ] Tools connector-like mantêm `search` e `fetch` no formato padrão.
- [ ] Resources de UI usam `text/html;profile=mcp-app`, `_meta.ui.csp`, `_meta.ui.domain` e `openai/widgetDescription`.
- [ ] Tools que renderizam UI expõem `_meta.ui.resourceUri`; `openai/outputTemplate` fica apenas como alias de compatibilidade.

## Autenticação E OAuth

- [ ] `McpServerSecurity__RequireJwt=true` em produção.
- [ ] `McpServerSecurity__RequireApiKey=false`, salvo compatibilidade operacional aprovada.
- [ ] `OAuth__RequireHttpsMetadata=true` em produção.
- [ ] `/.well-known/oauth-authorization-server` publica `registration_endpoint` em `/register`.
- [ ] `POST /register` aceita callbacks HTTPS permitidos do ChatGPT e recusa outros hosts e esquemas.
- [ ] `OAuth__Issuer` e `OAuth__Audience` usam `https://` ou derivam de `APP_DOMAIN`.
- [ ] `OAuth__KeyStoragePath` aponta para volume persistente.
- [ ] Chave `ConnectorSecrets__EncryptionKeyBase64` decodifica para 32 bytes.
- [ ] Secret `MEDIATR_LICENSE_KEY` contém uma licença MediatR válida e não é gravado em arquivos versionados.
- [ ] Startup em produção rejeita connection string, chave de criptografia, chave administrativa e licença MediatR ausentes ou placeholders.

## Moodle E Credenciais

- [ ] `MoodleApi__BaseUrl` e `MoodleProxy__BaseUrl`, quando usados, apontam para ambientes esperados.
- [ ] Tokens globais de Moodle não são usados como fallback silencioso, salvo decisão explícita.
- [ ] `MoodleApi__AllowServiceTokenForReadOnlyQueries=false`, salvo exceção aprovada.
- [ ] Credenciais Moodle de teste não estão em arquivos versionados.

## Escritas E Feature Flags

- [ ] As flags `FEATURES_*_WRITE_ENABLED` refletem a política aprovada para o ambiente, estão registradas no release e são gravadas explicitamente pelo workflow de deploy; não dependem silenciosamente do padrão de `appsettings.json`.
- [ ] Para cada escrita habilitada, `CanWrite`, escopo exigido, prévia, confirmação literal, expiração, reivindicação atômica e auditoria foram verificados.
- [ ] Qualquer escrita nova usa fluxo `prepare_*` e `confirm_*`.
- [ ] Confirmação exige usuário correto, escopo requerido, expiração e texto exato.
- [ ] Idempotência de confirmação está coberta por teste.

## Resiliência E Abuso

- [ ] Rate limit do `/mcp` está configurado para o perfil da release.
- [ ] Rate limits de portal/admin permanecem ativos.
- [ ] Timeouts, retries e circuit breaker Moodle estão configurados com limites conservadores.
- [ ] Aumentos de retry foram revisados contra risco de amplificar instabilidade do Moodle.

## Auditoria E Privacidade

- [ ] Payloads de auditoria passam por `AuditPayloadSanitizer`.
- [ ] Logs não incluem senha, token Moodle, JWT completo, API key, refresh token ou link privado com token.
- [ ] Fluxos sensíveis possuem `CorrelationId`.
- [ ] Troubleshooting usa erro, status e correlação, não credenciais.

## Banco E Deploy

- [ ] Script `Database/Scripts/001_initial_schema.sql` está presente no artefato publicado.
- [ ] Mudanças de schema novas foram adicionadas como script versionado ou migration aprovada.
- [ ] Workflow `deploy-vps.yml` passa em `Validate VPS secrets`.
- [ ] `dotnet test Moodle.slnx --configuration Release` passa.
- [ ] `git diff --check` não reporta whitespace inválido.

## Rollback

- [ ] Commit anterior estável identificado.
- [ ] Dados persistentes afetados pela release foram avaliados.
- [ ] Procedimento em `docs/operations/deploy-runbook.md#rollback` foi revisado.
- [ ] Healthchecks pós-rollback definidos: `/health`, `/api/status`, metadata OAuth e `/mcp`.
