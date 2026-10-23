# ADR-0002: autorização delimitada por equipe, permissão, delegação e capability

## Status

Aceito como decisão arquitetural e direção de implementação. A implementação completa permanece pendente e deve ser realizada em etapas, com compatibilidade e testes de regressão.

## Contexto

Tutor, monitor, gerente e administrador possuem responsabilidades diferentes. Nenhum papel global, associação a equipe, OAuth scope ou API key isoladamente representa autorização suficiente para acessar dados ou executar operações no Moodle.

A arquitetura deve distinguir claramente:

| Conceito | Responsabilidade |
|---|---|
| **Identity** | Identifica quem está realizando a operação. |
| **Team** | Define agrupamento gerencial e contexto organizacional. |
| **Role** | Define responsabilidade organizacional e políticas padrão. |
| **Platform Permission** | Autoriza server-side o uso de uma função ou tool do Connector. |
| **OAuth Scope** | Limita o que um token delegado pode executar. |
| **Connection Policy** | Define quais conexões Moodle podem ser utilizadas naquele contexto. |
| **Moodle Capability** | Confirma o que a conta/token remoto realmente pode fazer no Moodle. |

Nenhuma dessas camadas, isoladamente, concede acesso.

## Decisão

### 1. Equipes

`Team` representa um agrupamento gerencial de tutores e monitores para:

- acompanhamento;
- indicadores;
- relatórios;
- distribuição de responsabilidades;
- governança.

Pertencer a uma equipe não concede automaticamente acesso às tools.

### 2. Papéis

Papéis como Tutor, Monitor, Gerente e Administrador (ou qualquer rótulo criado pela instituição) representam responsabilidades organizacionais.

Um papel pode ser armazenado como rótulo ou determinar uma política inicial, mas nunca funciona como autorização final de uma operação. No Connector, o papel não é usado como bypass de endpoints ou tools; a autoridade é o grupo de permissões e a concessão/revogação direta.

Conceitualmente:

```text
Role
  ↓
Default Permission Policy
  ↓
Effective Platform Permissions
```

Concessões diretas, grupos de permissões e revogações explícitas podem alterar essa política.

### 3. Permissões da plataforma

A autorização das tools é controlada server-side por permissões estáveis, por exemplo:

```text
tool.courses.view
tool.classroom.view
tool.students.view
tool.assignments.view
tool.assignments.grade
tool.messages.view
tool.messages.send
tool.reports.view
tool.forums.view
tool.forums.write
tool.connections.manage
```

Cada tool deve declarar explicitamente sua permissão necessária.

Uma revogação explícita do usuário prevalece sobre concessões herdadas.

A autorização efetiva não deve depender de inferência pelo nome da tool.

### 4. OAuth scopes

OAuth scopes representam exclusivamente o limite da autorização delegada ao cliente, como ChatGPT.

Exemplos:

```text
moodle.read.courses
moodle.read.students
moodle.read.contents
moodle.read.assignments
moodle.read.submissions
moodle.write.messages
moodle.write.assignments.feedback
moodle.write.assignments.grade
```

Um OAuth scope:

- não transforma um usuário em administrador;
- não concede uma Platform Permission;
- não substitui Role;
- não substitui Moodle Capability;
- nunca amplia a autorização que o usuário já possui.

O token pode somente delegar um subconjunto da autorização disponível ao usuário.

### 5. Emissão OAuth

No `/authorize`, os scopes emitidos devem ser calculados por interseção:

```text
requestedScopes
        ∩
clientAllowedScopes
        ∩
userDelegableScopes
        ∩
connectionCoarseCapabilities
        =
issuedScopes
```

O token OAuth deve carregar identidade, cliente/resource e scopes delegados.

As Platform Permissions continuam sendo autoridade server-side e devem ser consultadas durante a execução. Não devem depender exclusivamente de claims persistidas no JWT, pois uma revogação precisa produzir efeito mesmo antes da expiração do token.

### 6. `moodle.admin`

`moodle.admin` não deve ser um scope disponível no consentimento OAuth comum.

Privilégios administrativos devem resultar de autorização server-side baseada em Role, Platform Permission e política administrativa.

Uma operação administrativa deve verificar explicitamente uma política local apropriada.

OAuth não deve ser capaz de elevar um usuário a administrador.

### 7. API keys e clientes internos

API keys representam outro método de autenticação/delegação e não devem escapar da política geral de autorização.

Chamadas via API key também devem produzir um `AuthorizationContext` e passar pelas mesmas verificações server-side de:

- permissões;
- conexão;
- contexto;
- capabilities;
- restrições de escrita.

`CanWrite` é condição necessária para determinados clientes, mas nunca suficiente para autorizar uma escrita. Clientes de serviço legados sem uma identidade local permanecem em modo de compatibilidade explícita até a migração para um grupo de permissões próprio; novas integrações devem falhar fechado sem essa política.

## Regra central de autorização

A autorização efetiva será:

```text
ALLOW =
    authenticated
    ∩ team/context
    ∩ effectivePlatformPermission
    ∩ delegatedAuthorizationBoundary
    ∩ connectionPolicy
    ∩ contextualMoodleCapability
    ∩ NOT explicitDeny
```

A camada `delegatedAuthorizationBoundary` corresponde:

```text
OAuth → scopes emitidos ao token
API Key → política/permissões atribuídas ao cliente
```

Nenhuma dessas camadas pode ampliar as permissões server-side do usuário.

## Escritas

Operações de escrita exigem adicionalmente:

```text
WRITE =
    ALLOW
    ∩ connection.CanWrite
    ∩ specificWritePermission
    ∩ specificDelegatedWriteScope
    ∩ preview/prepare
    ∩ explicitConfirmation
    ∩ idempotency
    ∩ audit
```

Quando uma operação não suporta prepare/confirm por sua natureza, a exceção deve estar documentada explicitamente.

## Capability Moodle

Cada `MoodleConnection` deve possuir um perfil de capabilities descobertas.

Esse perfil representa o limite máximo tecnicamente possível naquela conexão, não autorização automática ao usuário.

O fluxo é:

1. a conexão fornece capabilities observáveis;
2. o Connector normaliza essas capabilities;
3. a política local pode restringir esse conjunto;
4. a tool recebe sua autorização local;
5. antes da operação, a capability contextual do Moodle é verificada novamente;
6. ausência, desconhecimento ou falha na capability necessária resulta em bloqueio.

A política local pode restringir o Moodle, nunca ampliar suas permissões.

## Contrato de autorização das tools

Cada tool deve possuir um contrato determinístico contendo, no mínimo:

```text
ToolAuthorizationContract
├── RequiredPlatformPermission
├── OAuthScopePolicy
│   ├── StaticScopes
│   └── DynamicResolver quando necessário
├── RiskLevel
├── ReadOnly / Write
├── RequiresWriteConnection
└── RequiresConfirmation
```

Tools comuns devem utilizar scopes estáticos.

Tools universais ou cujo comportamento dependa da operação solicitada podem utilizar um resolver explícito de scopes.

Não deve existir inferência permanente baseada apenas no nome da tool.

## Convites

Convites de equipe concedem:

```text
Team
+
Role
```

Eles não concedem diretamente OAuth scopes.

Permissões adicionais são atribuídas posteriormente por grupos de permissões ou concessões explícitas.

OAuth scopes são calculados somente quando um cliente solicita delegação.

Campos atuais que armazenem scopes junto à associação de equipe devem ser tratados como legado/compatibilidade até sua migração e não devem constituir a autoridade final para execução de tools.

## Fonte de verdade

Portal, MCP, API e processos internos devem consumir a mesma política server-side de autorização.

A decisão não deve ser duplicada entre:

- JWT;
- middleware;
- metadata MCP;
- Portal;
- serviços de aplicação.

Uma única camada deve calcular a autorização efetiva.

## Consequências

- Roles deixam de funcionar como ACL implícita.
- Equipes passam a representar contexto organizacional, não autorização.
- Platform Permissions tornam-se a autoridade local das tools.
- OAuth scopes tornam-se apenas limites de delegação.
- Revogações podem produzir efeito sem esperar expiração de JWT.
- Capabilities Moodle continuam sendo a fronteira externa final.
- API keys deixam de constituir um caminho paralelo de autorização.
- `moodle.admin` deixa de ser privilégio solicitável pelo cliente.
- O manifesto MCP passa a anunciar apenas os scopes realmente necessários a cada tool.
- Portal, MCP e API passam a compartilhar a mesma decisão server-side.
