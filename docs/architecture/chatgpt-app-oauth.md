# OAuth para conexões MCP do ChatGPT

## Decisão

O conector usa um authorization server OAuth embutido no `Moodle.Presentation`, implementado com OpenIddict Server. Cada conexão MCP registra seu próprio cliente OAuth público pelo endpoint DCR `POST /register`.

O fluxo usa authorization code com PKCE `S256`, metadata OAuth pública, tokens Bearer para `/mcp` e validação de issuer, audience, assinatura, expiração e scopes em toda chamada. Aplicações, autorizações e tokens são armazenados no banco do conector; certificados RSA persistem no volume para assinar os tokens entre reinicializações.

## Topologia

O mesmo host público expõe o MCP, o portal e o broker OAuth:

```text
ChatGPT
  -> GET  https://<APP_DOMAIN>/.well-known/oauth-protected-resource/mcp
  -> GET  https://<APP_DOMAIN>/.well-known/oauth-authorization-server
  -> GET  https://<APP_DOMAIN>/.well-known/openid-configuration
  -> GET  https://<APP_DOMAIN>/.well-known/jwks
  -> POST https://<APP_DOMAIN>/register
  -> GET  https://<APP_DOMAIN>/authorize
  -> POST https://<APP_DOMAIN>/token
  -> POST https://<APP_DOMAIN>/mcp
```

Configuração recomendada:

```env
APP_DOMAIN=moodle-conector.<dominio>
COMPOSE_PROFILES=https
CADDYFILE=./deploy/Caddyfile

McpServerSecurity__RequireJwt=true
McpServerSecurity__RequireApiKey=false

OAuth__Issuer=https://moodle-conector.<dominio>
OAuth__Audience=https://moodle-conector.<dominio>/mcp
OAuth__ScopeName=moodle-mcp-audience
OAuth__RequireHttpsMetadata=true
OAuth__KeyStoragePath=/app/data/oauth
```

`OAuth__ChatGptClientId` e `OAuth__ChatGptRedirectUri` são opcionais e servem apenas para manter clientes estáticos antigos. Novas conexões usam DCR e não dependem dessas variáveis. Quando `OAuth__Issuer` e `OAuth__Audience` ficam vazios, a aplicação deriva ambos de `APP_DOMAIN`.

## Contrato MCP/OAuth

`/.well-known/oauth-protected-resource/mcp` publica o recurso canônico `https://<APP_DOMAIN>/mcp`, o authorization server e os scopes aceitos.

`/.well-known/oauth-authorization-server` publica:

- `issuer`, `authorization_endpoint`, `token_endpoint` e `jwks_uri`;
- `registration_endpoint = https://<APP_DOMAIN>/register`;
- `grant_types_supported` com authorization code e refresh token;
- `code_challenge_methods_supported` com `S256`;
- `token_endpoint_auth_methods_supported` com `none`;
- `client_id_metadata_document_supported = false`.

O endpoint DCR aceita clientes públicos sem segredo, authorization code com PKCE e callbacks HTTPS do ChatGPT nos caminhos `/connector/oauth/{callback_id}` ou `/connector_platform_oauth_redirect`. Outros hosts, protocolos, portas, query strings e fragments são rejeitados. O cadastro usa o rate limit de autenticação do portal.

`/.well-known/openid-configuration` e `/.well-known/jwks` são publicados pelo OpenIddict para descoberta OIDC/JWT padrão. `tools/list` anuncia `_meta.securitySchemes` OAuth 2.0 quando JWT está ativo. Chamadas sem token válido retornam desafio MCP em `_meta["mcp/www_authenticate"]`.

## Fluxo

1. ChatGPT lê o servidor remoto de `mcp.json` e descobre os metadados do recurso e do OAuth.
2. ChatGPT registra um cliente público por conexão em `/register` e recebe um `client_id` independente da conta que publicou ou distribuiu o pacote.
3. ChatGPT abre `/authorize` com `client_id`, callback, scopes, state, code challenge e resource.
4. O portal autentica o usuário e o OpenIddict emite o authorization code.
5. ChatGPT troca o código por access token em `/token` usando PKCE.
6. ChatGPT chama `/mcp` com `Authorization: Bearer <token>`.
7. O MCP valida o token e resolve a conta e a conexão Moodle autorizadas.

## Validação

Na VPS, confira os endpoints públicos:

```bash
curl https://<APP_DOMAIN>/health
curl https://<APP_DOMAIN>/.well-known/oauth-protected-resource/mcp
curl https://<APP_DOMAIN>/.well-known/oauth-authorization-server
curl https://<APP_DOMAIN>/.well-known/openid-configuration
curl https://<APP_DOMAIN>/.well-known/jwks
```

O metadata OAuth precisa conter `registration_endpoint`. Em uma conexão ChatGPT de teste, confirme que o cadastro de cliente com callback do ChatGPT retorna HTTP 201, que callbacks fora da allowlist são rejeitados e que o fluxo completo de autorização funciona.

## Referências oficiais

- [Autenticação de usuários em plugins](https://developers.openai.com/plugins/build/auth)
- [Conceitos de servidores MCP](https://developers.openai.com/plugins/build/mcp-server)
- [OpenIddict Server](https://documentation.openiddict.com/guides/getting-started/creating-your-own-server-instance.html)
