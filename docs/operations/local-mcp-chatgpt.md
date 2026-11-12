# MCP local do Moodle Conector no ChatGPT

**Escopo:** uso privado em computador Windows com Docker Desktop e PowerShell 7. O plugin pessoal aponta a um túnel HTTPS transitório; não é um deploy público nem autorização para tratar dados institucionais. Este perfil é baseado na branch de hardening e não altera a `master`.

## Arquitetura

```text
ChatGPT (Plugin privado)
       |
       | OAuth 2.1, PKCE, HTTPS
       v
https://<tunel-ngrok>/mcp
       |
       | ngrok no Windows, encaminhando apenas 127.0.0.1:8788
       v
Docker: Moodle.Presentation (Production / JWT; leitura somente)
       |        |  +-- /authorize /token /.well-known/... /register (OAuth)
       |  +-- / (portal; cookie HttpOnly + CSRF)
       v
Docker: PostgreSQL (sem porta exposta ao host)
```

O servidor MCP **já existe** no projeto. Não é preciso criar outro microserviço nem entregar a senha do Moodle ao ChatGPT. O fluxo é de autorização do usuário no portal, com scopes OAuth, permissões locais, capacidade do Moodle e política de conexão.

### Pré-requisitos

- Docker Desktop (com Linux containers) em execução; Docker Compose v2.
- PowerShell 7 (`pwsh`).
- Um túnel HTTPS que suporte Streamable HTTP/SSE e encaminhe para a porta **8788**. O exemplo usa [ngrok](https://ngrok.com/docs/using-ngrok-with/http-tunnels/); o túnel oferece uma URL pública **temporária**, sem autenticação própria. Toda autenticação continua sendo obrigação do servidor Moodle Conector.
- Uma licença MediatR válida, armazenada **somente no seu PC**. Em modo Production o servidor intencionalmente recusa inicialização com esse valor vazio ou de exemplo.
- Permissão do seu plano/workspace para adicionar servidor MCP personalizado no ChatGPT. A disponibilidade de leitura/escrita varia conforme o plano.
- Para dados reais, permissão explícita do controlador/instituição e do Moodle remoto. Comece com conta e dados sintéticos.

## 1. Clonar o perfil isolado

```powershell
git clone -b fix/local-chatgpt-mcp-plugin https://github.com/Julioall/moodle.git
cd moodle
pwsh ./scripts/start-chatgpt-local.ps1 -Action Init
```

O comando Init gera `.env.chatgpt-local`, ignorado pelo Git, com uma senha de banco exclusiva, chave AES aleatória de 32 bytes e chave administrativa aleatória. **Não repita Init** após salvar dados no banco: trocar a chave impede descriptografar credenciais antigas. Não publique nem envie esse arquivo.

Abra `.env.chatgpt-local` localmente e configure `MEDIATR_LICENSE_KEY` com licença real. O fluxo de bootstrap abaixo pode ser realizado antes disso, mas o modo Production requer licença.

## 2. Criar a primeira conta sem expor a internet

```powershell
pwsh ./scripts/start-chatgpt-local.ps1 -Action Bootstrap
```

Abra `http://127.0.0.1:8789`, registre sua conta local e, depois, cadastre uma conexão Moodle autorizada no portal. O bootstrap fica em **Development**, aceita cadastro e usa uma **porta diferente** da publicação para evitar que um túnel na porta 8788 exponha o cadastro. **Não abra qualquer túnel na porta 8789**.

Depois do cadastro:

```powershell
pwsh ./scripts/start-chatgpt-local.ps1 -Action Stop
```

Isso interrompe os contêineres, sem excluir os volumes. A conta e as conexões persistem.

## 3. Criar o endpoint HTTPS

Em um segundo terminal, com o ngrok instalado e autenticado na sua conta, execute:

```powershell
ngrok http 8788
```

Copie a URL `https://...` exibida pelo ngrok, **sem** `/mcp` no final. Mantenha essa janela aberta. A porta 8788 continua acessível localmente apenas em `127.0.0.1`; o ngrok cria a conexão de saída e publica a URL. O PostgreSQL não tem portas publicadas.

O nome de host pode mudar quando reiniciar o ngrok: nesse caso repita Start com a nova URL e, no ChatGPT, atualize/recrie a conexão para apontar ao novo endpoint. Para continuidade, prefira um domínio HTTPS fixo, com controle de acesso e logs.

## 4. Iniciar o perfil protegido (Production)

Substitua o exemplo pelo host HTTPS real do seu túnel:

```powershell
pwsh ./scripts/start-chatgpt-local.ps1 -Action Start -PublicOrigin 'https://seu-subdominio.ngrok.app'
pwsh ./scripts/start-chatgpt-local.ps1 -Action Check -PublicOrigin 'https://seu-subdominio.ngrok.app'
```

O `Start` reconstrói e reinicia a aplicação em **Production**, com:

- JWT obrigatório e API key desabilitada no MCP;
- issuer/audience OAuth fixados à URL pública HTTPS, descoberta OAuth e DCR;
- cadastro público e recuperação por e-mail fechados;
- `Features__...WriteEnabled=false`, além do executor universal de escrita desabilitado;
- cookies `Secure`, armazenamento privado de chaves e certificados OAuth, PostgreSQL sem exposição direta;
- volume isolado do ambiente local anterior, e Docker executando a aplicação sem `root`.

O `Check` consulta `/health` e metadados OAuth públicos via HTTPS e compara `issuer`, `authorization_endpoint`, `token_endpoint` e `authorization_servers`. Não envia senhas. Se falhar, verifique URL pública, proxy `X-Forwarded-Proto`, DNS, logs e reinicie Start; **não** desligue JWT/HTTPS para contornar o erro.

## 5. Criar o plugin pessoal no ChatGPT

1. No ChatGPT web, abra **Plugins** → **Adicionar** → **Adicionar servidor MCP personalizado**.
2. Nome sugerido: **Moodle Conector Local**.
3. URL do servidor: `https://seu-subdominio.ngrok.app/mcp`.
4. Selecione OAuth/conta de usuário quando solicitado. Autorize no portal usando a conta cadastrada no bootstrap.
5. Revise as tools detectadas e o aviso de segurança. Crie como **plugin privado**.
6. Em um chat com acesso ao plugin, use `@Moodle Conector Local` e comece com uma consulta não sensível, por exemplo, informações do conector ou lista de cursos de uma conta de teste.

O manifesto `plugins/moodle-connector/mcp.json` existente é destinado ao endpoint remoto do projeto e **não** é alterado pelo modo local. A conexão personalizada registra o seu endpoint temporário sem versionar URLs privadas no repositório.

### Conferência local opcional

```powershell
pwsh ./scripts/start-chatgpt-local.ps1 -Action Status
npx @modelcontextprotocol/inspector@latest
```

No MCP Inspector, use Streamable HTTP e `http://127.0.0.1:8788/mcp` para examinar inicialização/metadados. Chamadas privadas de tools exigem OAuth válido; **não** cole chaves do banco ou de administrador no Inspector.

## Secure MCP Tunnel: quando utilizar

O [Secure MCP Tunnel da OpenAI](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels) permite que o ChatGPT alcance MCPs privados sem expor uma porta pública. Porém, no modelo atual, o `authorization_endpoint` do OAuth permanece acessado **diretamente pelo navegador**: o túnel não publica rotas arbitrárias de login. Como o Moodle Conector hospeda **o próprio broker OAuth** junto ao MCP, usar apenas o túnel privado **não fecha o fluxo de login existente**. Uma opção futura é colocar a autorização em um IdP HTTPS externo e adaptar/validar a integração, mantendo a API MCP privada.

Nunca altere `McpServerSecurity__RequireJwt=false` ou ative uma API key global para conseguir conectar o ChatGPT. O produto não aceita chaves de API personalizadas como substituto do OAuth do usuário em plugins privados.

## Encerrar, proteger e diagnosticar

```powershell
pwsh ./scripts/start-chatgpt-local.ps1 -Action Stop
```

- Pare também o ngrok. `Stop` **não** apaga os dados persistidos; não use `docker compose down -v` sem backup.
- Rotacione credenciais se forem acidentalmente expostas. Não registre tokens em issues, chats ou logs.
- A porta de bootstrap 8789 nunca deve ter túnel. A porta de serviço 8788 é restrita a loopback; um túnel em execução significa exposição do portal/MCP na internet, protegida pelo login.
- Não exponha o Compose `local.yml` de desenvolvimento nem libere registros abertos.
- Em ambiente com pessoas reais, valide acesso por instituição, consentimento/autorização, retenção, logs e LGPD antes de usar; o CI verde não substitui esses procedimentos.
- Se o ChatGPT não mostrar **Adicionar servidor MCP personalizado**, confirme as permissões do plano/workspace. Criar um pacote ZIP não contorna restrições de acesso.
- Para publicação pública e disponibilidade contínua, use HTTPS em domínio estável, requisitos de revisão e homologação institucional; ngrok de teste **não é um deploy de produção**.

### Referências atuais

- [Conectar servidores MCP personalizados ao ChatGPT](https://help.openai.com/pt-br/articles/12584461-connecting-custom-mcp-servers-in-chatgpt)
- [Conectar e testar um plugin](https://developers.openai.com/plugins/deploy/connect-chatgpt)
- [Autenticação de plugins](https://developers.openai.com/plugins/build/auth)
- [Secure MCP Tunnel](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels)
