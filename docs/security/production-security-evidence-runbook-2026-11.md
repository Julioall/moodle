# Homologação operacional — SEC-01, SEC-03, SEC-04, SEC-06 e SEC-07

**Status:** checklist de produção não executado. Preparado em 11/11/2026 na branch `fix/security-lgpd-production-hardening`.

Este runbook documenta a coleta de evidências **sem exportar dados acadêmicos nem segredos**. Não constitui aprovação para deploy, nem substitui investigação de incidente, parecer jurídico, teste de restauração ou autorização do controlador.

## Preparação segura

- Use conexão de administração aprovada para o PostgreSQL, preferencialmente pelo console da VPS ou túnel SSH autenticado. Nunca use `psql` com senha em linha de comando, histórico do shell ou issue.
- Antes de executar migrações 085–087: snapshot consistente + teste de restauração em homologação, revisão do plano de rollback, janela de manutenção, responsável de infraestrutura e plano de reaprovamento explícito de administradores.
- Compare os totais **antes** e **depois**; não publique identificadores de usuários, credenciais, tokens, URLs contendo segredos ou documentos de estudantes em logs/tickets.
- O bloco SQL é apenas de leitura. A conta de auditoria pode ter privilégio SELECT restrito às tabelas citadas; não precisa de permissões de escrita.

## Consulta agregada de produção (somente leitura)

```sql
BEGIN TRANSACTION READ ONLY;

-- O deploy seguro exige todas estas versões efetivamente aplicadas.
SELECT "Version", "Description", "AppliedAt"
FROM "MOODLE_schema_versions"
WHERE "Version" IN (85, 86, 87)
ORDER BY "Version";

-- Concessões elevadas remanescentes: após 085 devem ser zero,
-- exceto concessões novas expressamente aprovadas e auditadas pelo DBA.
SELECT "Permission", count(*) AS group_grants
FROM permission_group_permissions
WHERE "Permission" IN
  ('admin.view', 'tool.permission_groups.manage', 'tool.teams.manage',
   'tool.assignments.grade', 'tool.messages.send')
GROUP BY "Permission"
ORDER BY "Permission";

SELECT "Permission", count(*) AS explicit_user_grants
FROM user_permission_overrides
WHERE "IsAllowed" = TRUE
  AND "Permission" IN
  ('admin.view', 'tool.permission_groups.manage', 'tool.teams.manage',
   'tool.assignments.grade', 'tool.messages.send')
GROUP BY "Permission"
ORDER BY "Permission";

-- Grupos de acesso inicial não podem herdar direitos fora da baseline.
SELECT p."Permission", count(*) AS unexpected_baseline_grants
FROM permission_group_permissions p
JOIN permission_groups g ON g."Id" = p."GroupId"
WHERE g."Name" = 'Acesso inicial'
  AND p."Permission" NOT IN (
      'dashboard.view', 'courses.view', 'schools.view', 'students.view', 'reports.view',
      'connections.manage', 'settings.view', 'grading.view',
      'tool.assignments.view', 'tool.messages.view', 'tool.reports.view',
      'tool.courses.view', 'tool.students.view', 'tool.classroom.view',
      'tool.followup.view', 'tool.forums.view', 'tool.connections.manage',
      'tool.pedagogy.view'
  )
GROUP BY p."Permission"
ORDER BY p."Permission";

-- Contas com vínculos ativos em mais de uma equipe institucional.
-- Não exportar nomes, e-mails nem UUIDs de titulares.
SELECT count(*) AS multi_institution_accounts
FROM (
    SELECT m."UserId"
    FROM team_memberships m
    JOIN teams t ON t."Id" = m."TeamId"
    WHERE m."IsActive" = TRUE AND t."IsPersonal" = FALSE
    GROUP BY m."UserId"
    HAVING count(DISTINCT m."TeamId") > 1
) AS accounts;

-- Contas sem carimbo de revogação válido.
SELECT count(*) AS accounts_with_empty_security_stamp
FROM user_accounts WHERE "SecurityStamp" = '00000000-0000-0000-0000-000000000000';

-- Estado dos tokens; não retornar "Payload", "ReferenceId" nem "Subject".
SELECT coalesce("Status", 'undefined') AS token_status,
       count(*) AS token_count,
       count(*) FILTER (WHERE "ExpirationDate" < now()) AS expired
FROM "OpenIddictTokens"
GROUP BY coalesce("Status", 'undefined')
ORDER BY token_status;

-- Tokens de recuperação inválidos ou ainda válidos, sem jamais retornar hash.
SELECT count(*) FILTER (WHERE "ConsumedAtUtc" IS NULL AND "ExpiresAtUtc" > now())
         AS active_recovery_tokens,
       count(*) FILTER (WHERE "ExpiresAtUtc" < now() - interval '24 hours'
          OR "ConsumedAtUtc" < now() - interval '24 hours')
         AS recovery_tokens_due_for_purge
FROM password_reset_tokens;

ROLLBACK;
```

## Provas exigidas por controle

| Controle | Prova | Responsável | Estado |
|---|---|---|---|
| SEC-01 — privilégios | Antes/depois de 085, grants elevados explicitamente aprovados, comparação de ACLs e revogação efetiva de cookie/JWT/API key | DBA + Segurança | **Pendente no ambiente real** |
| SEC-03 — credencial compartilhada | Inventário de uso anterior, rotação de API keys, senha institucional, sessões e tokens eventualmente afetados | Operação/VPS + instituições | **Pendente** |
| SEC-04 — isolamento | Em homologação, matriz adversarial com usuários de A, B e A+B para curso, conexão, relatório, nota, arquivo e operações de escrita | QA + representantes institucionais | **Pendente** |
| SEC-06 — host SSH | Obter fingerprint ED25519/RSA pelo console/provedor, comparar fora de banda com secret `VPS_SSH_HOST_KEY`, realizar deploy de homologação | Responsável pela VPS | **Pendente** |
| SEC-07 — OAuth/MCP | Evidência sanitizada de HTTPS, issuer, audience, JWT obrigatório, API key MCP desativada, expiração, replay e vinculação de conta | Segurança + Infra | **Pendente** |

## Teste adversarial de isolamento (mínimo)

Use somente dados sintéticos, contas de teste identificáveis como tal e instalações Moodle de homologação. Registre a versão/commit testado e os resultados sem nomes, e-mails, UUIDs, notas ou tokens.

1. Criar instituições **A** e **B**, usuários **A-only**, **B-only** e **AB** (vinculado às duas), conexões Moodle distintas e cursos/estudantes sintéticos exclusivos de cada conexão. Registrar previamente o mapa esperado de titularidade.
2. Com a sessão de **A-only**, conferir lista e acesso direto por ID para cursos, estudantes, notas, submissões/arquivos, relatórios e jobs/artifacts de A e de B. Acessos e downloads de B devem ser negados sem revelar conteúdo. Repetir com **B-only** invertendo as instituições.
3. Para cada superfície, conferir tanto a listagem quanto operações por identificador direto. Se uma escrita estiver habilitada em homologação, testar preparação e confirmação com usuário, instituição, conexão e curso corretos; chamadas cruzadas devem falhar antes de alterar Moodle ou criar/entregar artifacts.
4. Com **AB**, verificar acesso aos recursos de A e B segundo os vínculos ativos. Um administrador de A não pode alterar permissões ou associação institucional de **AB**, pois isso afetaria também B. Uma alteração autorizada em B não pode conceder direitos em A.
5. Desativar o vínculo de **AB** com A. Depois de renovar a autenticação, confirmar acesso negado a A e preservado em B. Repetir com cookie/JWT anterior e conferir revogação ou rejeição conforme a política implementada. Inspecionar caches, snapshots e jobs para garantir que a seleção da conexão não reaproveite dados da instituição anterior.
6. Tentar exclusão administrativa em massa e autoexclusão de **AB**. A operação deve ser recusada enquanto não houver procedimento institucional; os dados e o vínculo de B devem continuar íntegros.
7. Repetir a matriz após restaurar o backup em homologação, aplicar as migrações pendentes pelo inicializador normal e limpar caches descartáveis. Registrar também quaisquer falhas, sem anexar respostas que contenham dados de teste desnecessários.

### Registro mínimo do ensaio

| Campo | Valor a registrar |
|---|---|
| Ambiente e data | Identificador de homologação e horário com fuso |
| Código | SHA/commit e versão da imagem testada |
| Dados | Confirmação de que contas, cursos, notas e arquivos eram sintéticos |
| Cobertura | Superfície, papel (A-only/B-only/AB), conexão e cenário testado |
| Resultado | Permitido/negado esperado, resultado observado e aprovado/reprovado |
| Evidência | Link restrito ao relatório sanitizado; não anexar tokens, dados pessoais ou payloads Moodle |
| Aceite | Responsável executor e revisor institucional |

Um resultado agregado como “CI verde” não substitui as linhas por superfície e por papel. Qualquer falha mantém SEC-04 pendente até correção e repetição do cenário afetado.

## Restauração e migrações 085–087 em homologação

1. Identificar o backup selecionado, horário de consistência, origem e SHA do código que será avaliado. Guardar o backup cifrado em local restrito; não registrar credenciais nem conteúdo de estudantes no relatório.
2. Restaurar em banco isolado de homologação, sem apontar a aplicação para produção. Confirmar que a restauração terminou sem erro e registrar contagens agregadas das entidades necessárias antes das migrações.
3. Inspecionar o ledger `MOODLE_schema_versions` e o schema restaurado. Executar 085–087 somente pelo inicializador/fluxo normal da aplicação e registrar versão, duração e erros; não editar ledger nem aplicar SQL manual para contornar falhas.
4. Conferir que as três versões aparecem uma única vez e que as tabelas/colunas de tokens de recuperação e `SecurityStamp` existem. Verificar, por contagem agregada, os grants que a 085 deve revogar e a baseline do grupo `Acesso inicial`; investigar qualquer grant residual antes de aprovar.
5. Repetir o levantamento de contagens e permissões após a migração. Não deve haver concessão elevada reintroduzida, regressão da baseline ou perda não explicada de dados. Confirmar que usuários legítimos de administração foram reprovisionados somente pelo procedimento supervisionado aprovado.
6. Executar a matriz SEC-04 sobre a cópia restaurada. Guardar como evidência o identificador/hash do backup (sem disponibilizar o arquivo), versão de origem, versões aplicadas, resultado agregado das verificações e aceite do DBA/revisor.
7. Se a restauração, migração ou comparação falhar, interromper a preparação de produção, preservar a cópia de homologação para diagnóstico e registrar o plano de correção. Não promover a cópia restaurada nem usar o teste como autorização de deploy.

O ensaio só confirma essa combinação de backup, código e ambiente. Uma alteração posterior no schema, nas permissões ou no fluxo de restauração exige nova validação.

## SSH e configuração de aplicação

- A chave real do host **não pode ser inferida do Git nem obtida por `ssh-keyscan` no primeiro acesso**. O responsável pela VPS deve conferir a fingerprint por canal previamente confiável e provisionar `VPS_SSH_HOST_KEY`.
- Confirmar `ASPNETCORE_ENVIRONMENT=Production`, `McpServerSecurity__RequireJwt=true`, `McpServerSecurity__RequireApiKey=false`, `OAuth__RequireHttpsMetadata=true`, issuer e audience públicos HTTPS consistentes. Não copiar variáveis de ambiente com valores sensíveis para tickets.
- Recuperação de senha só deve ser ativada depois de configurar SMTP, remetente e URL HTTPS, confirmar entrega e testar código inválido, expirado e reutilizado.
- Confirmar volume privado para relatórios/pacotes, isolamento PostgreSQL, backups cifrados, atualizações de dependências e logs com retenção aprovada.

## Critério de encerramento

Os relatórios de CI e a presença de documentação são **evidências parciais**. Cada item exige aprovação registrada, resultado de homologação, identificação de responsável e decisão de risco residual. Não afirmar conformidade LGPD, nem liberar a `master` ou produção, antes do aceite formal de quem detém autoridade sobre os dados.
