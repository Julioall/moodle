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

1. Criar contas sintéticas **A**, **B** e **AB** (vinculada às duas instituições), dois aliases e cursos distintos em homologação.
2. A não consegue ler, alterar, gerar, baixar ou excluir relatórios, recursos ou notas de B, inclusive por tentativa direta de ID/URL.
3. A não consegue gerenciar permissões da conta AB porque AB também pertence a B. Alterações em B não podem aumentar direitos em A.
4. Desativar associação de A, repetir as mesmas chamadas com cookies/JWT antigos, conferir negativa e trilha sanitizada.
5. Tentar exclusão administrativa em massa e autoexclusão de AB; nenhuma deve eliminar dados de B.
6. Repetir após restore de backup e limpeza de caches, jobs e snapshots.

## SSH e configuração de aplicação

- A chave real do host **não pode ser inferida do Git nem obtida por `ssh-keyscan` no primeiro acesso**. O responsável pela VPS deve conferir a fingerprint por canal previamente confiável e provisionar `VPS_SSH_HOST_KEY`.
- Confirmar `ASPNETCORE_ENVIRONMENT=Production`, `McpServerSecurity__RequireJwt=true`, `McpServerSecurity__RequireApiKey=false`, `OAuth__RequireHttpsMetadata=true`, issuer e audience públicos HTTPS consistentes. Não copiar variáveis de ambiente com valores sensíveis para tickets.
- Recuperação de senha só deve ser ativada depois de configurar SMTP, remetente e URL HTTPS, confirmar entrega e testar código inválido, expirado e reutilizado.
- Confirmar volume privado para relatórios/pacotes, isolamento PostgreSQL, backups cifrados, atualizações de dependências e logs com retenção aprovada.

## Critério de encerramento

Os relatórios de CI e a presença de documentação são **evidências parciais**. Cada item exige aprovação registrada, resultado de homologação, identificação de responsável e decisão de risco residual. Não afirmar conformidade LGPD, nem liberar a `master` ou produção, antes do aceite formal de quem detém autoridade sobre os dados.
