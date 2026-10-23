# Skill, Registry e Exposure Policy

## Objetivo

As skills orientam intenção e roteamento; o código determinístico decide conexão, capability, policy, normalização e confirmação. Uma skill não concede permissão e o modelo não pode inventar uma operação.

## Fluxo de leitura

```text
prompt
  -> SKILL / roteamento
  -> IConnectionRegistry
  -> IOperationRegistry
  -> IPolicyEngine
  -> ICapabilityRegistry
  -> ISafeReadExecutor
  -> IMoodleRestClient
  -> IResponseNormalizer
```

`moodle_execute_read` usa `ISafeReadExecutor`. A operação só é executada quando é leitura conhecida e também aparece nas capabilities da conexão. Escritas usam fluxos controlados e confirmação humana.

## Skills versus tools

- Tools MCP fornecem dados em tempo real, autenticação, autorização e ações.
- Skills fornecem instruções reutilizáveis para combinar essas tools.
- Instalar/conectar o app MCP não instala automaticamente as skills do plugin.
- Memória privada não é mecanismo de distribuição de skill.
- A fonte canônica das skills é `plugins/moodle-connector/skills/`.

O servidor não implementa nesta versão a extensão experimental `io.modelcontextprotocol/skills`. O catálogo completo tem 13 skills e é distribuído pelo pacote do plugin. Se houver futura importação via MCP, ela deve ser tratada como snapshot de submissão, não como carregamento dinâmico em runtime, e não pode criar uma segunda fonte editável.

## Exposure de tools

`TechnicalClassification` descreve a implementação. `ExposureStatus` descreve a decisão do perfil. `BenchmarkEvidence` descreve evidência empírica; nenhum substitui os demais.

O perfil `Production` expõe apenas metadata registrada e oculta itens diagnósticos/internos conforme política. Capabilities do token continuam sendo a autorização final. O filtro é aplicado antes da serialização de `tools/list`.

### Classificação técnica R1–R6

- `R1`: wrapper direto de operação Moodle conhecida.
- `R2`: leitura simples com transformação limitada.
- `R3`: leitura com validação, fallback ou compatibilidade.
- `R4`: agregação, paginação, joins ou relatório.
- `R5`: regra pedagógica, workflow controlado ou escrita com revisão humana.
- `R6`: infraestrutura, memória, discovery, arquivos ou diagnóstico.

## Escritas

Prefira os pares especializados de preview/confirm de grading, mensagens e fórum. `moodle_prepare_write`/`moodle_confirm_write` são fallback controlado quando expostos e habilitados. `moodle_reconcile_write` resolve `execution_unknown` sem reenviar a operação remota.

Uma skill nunca deve instruir retry cego de escrita, sobrescrita implícita ou confirmação sem mostrar a prévia exigida pelo domínio.

## Catálogo e ownership

| Skill | Ownership principal |
|---|---|
| `moodle-core` | conexão, capabilities, fallback, build e reconciliação técnica |
| `moodle-memory` | memória privada e documentos duráveis do usuário |
| `moodle-courses` | descoberta, identidade e estrutura de cursos |
| `moodle-content` | arquivos, páginas, URLs, quizzes e SCORM como conteúdo |
| `moodle-assignments` | tarefas, prazos e submissões |
| `moodle-students` | participantes, grupos, identidade e acesso |
| `moodle-grading` | preparo, evidência, revisão, exportação e publicação de correções |
| `moodle-follow-up` | sinais observáveis para acompanhamento |
| `moodle-forums` | leitura, rascunho, preview e publicação em fórum |
| `moodle-messaging` | rascunho, preview, destinatários e confirmação de mensagens |
| `moodle-pedagogy` | orientações pedagógicas e handoff de modelos |
| `moodle-reports` | relatórios agregados e exportações |
| `moodle-classroom-audit` | auditoria estrutural/checklist da sala |

## Handoffs

- Courses -> Content para ler o conteúdo real de recursos.
- Assignments -> Grading quando a intenção inclui nota/feedback/publicação.
- Assignments/Students -> Follow-up para formar sinais de acompanhamento.
- Follow-up -> Messaging para preparar contato controlado.
- Pedagogy -> Grading/Follow-up/Forums/Messaging/Reports quando houver interpretação institucional.
- Courses -> Classroom Audit para diagnóstico estrutural.

## Como alterar a superfície

1. Altere a skill apenas para intenção, roteamento, ownership, fallback e interpretação; ela não concede permissão.
2. Registre operações e metadata de tool de forma determinística no servidor.
3. Preserve capability, authorization, paginação e normalização.
4. Atualize testes de registry/policy/exposure e os casos de acionamento das skills.
5. Não esconda nem adicione tool somente para reduzir tokens sem evidência de benchmark/telemetria.
6. Para distribuição, modifique somente a árvore canônica e regenere o pacote; não copie skills para memória ou `public/`.
