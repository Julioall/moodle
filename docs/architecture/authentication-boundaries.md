# Fronteiras de autenticação por superfície

Este documento descreve contratos de rota implementados no host. Ele separa a autenticação de
máquina do MCP da sessão humana do portal; uma credencial válida em uma superfície não é uma
credencial implícita na outra.

| Superfície | Rota | Credencial aceita | Controle complementar | Resultado sem credencial |
| --- | --- | --- | --- | --- |
| MCP | `/mcp` | OAuth/JWT e/ou API key, conforme configuração | conexão Moodle, scopes, permissões, rate limit e auditoria | `401` ou desafio JSON-RPC OAuth para `tools/call` |
| Descoberta MCP | `/.well-known/oauth-*`, `/.well-known/openid-configuration`, `/.well-known/jwks` | pública | metadados não expõem segredos | resposta de metadados |
| OAuth | `/authorize`, `/token` | cookie no consentimento e PKCE no token | OpenIddict, issuer/audience e redirect URI registrado | redirect para login ou erro OAuth |
| Portal público | `/api/status`, `/api/info`, `/api/csrf`, registro e login | nenhuma | rate limit e CSRF nas mutações | contrato público controlado |
| Portal privado | demais rotas `/api` | somente cookie `moodle-app` | política `portal.session`, permissão de plataforma, CSRF nas mutações | `401 portal_session_required` |
| Administração | `/admin/**` | chave administrativa no header configurado | rate limit e validação de segredo em produção | `401`/`403` sem revelar a chave |

## Implementação

- `AuthenticatedPrincipalEnrichmentMiddleware` restaura permissões e vínculo Moodle somente para
  `/api` e `/mcp` já autenticados.
- `PortalApiAuthorizationMiddleware` aplica `portal.session` a todas as rotas privadas de `/api`.
  Tokens Bearer OAuth não são esquema aceito nessa política.
- `McpRequestSecurityMiddleware` é exclusivo de `/mcp` e concentra API key, Bearer JWT, desafios
  OAuth JSON-RPC, rate limit por sujeito/conector e auditoria de falhas.

## Evidências automatizadas

- `Portal_privado_exige_sessao_cookie_e_preserva_bootstrap_publico` prova a separação entre
  bootstrap público e API privada.
- `Token_oauth_do_mcp_nao_substitui_sessao_cookie_do_portal` prova a rejeição cross-route.
- `McpJwtClaimsIntegrationTests` valida discovery e desafios OAuth do MCP.

Nenhum segredo, JWT completo, API key ou senha Moodle deve ser incluído em logs, relatórios de
falha ou evidências de benchmark.
