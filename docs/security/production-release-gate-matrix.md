# Matriz de aceite SEC-01–07, PUB-01 e LGPD

**Branch:** fix/security-lgpd-production-hardening • **Estado:** NO-GO para uso multi-institucional real até validação e aceites externos.

| Achado | Correção técnica na branch | Pendência de aceite real | Evidência para encerrar |
|---|---|---|---|
| SEC-01 | Remoção de catálogo de privilégios implícitos; migração 085 registrada | Auditoria de grants históricos, revoke de cookies/tokens, backup/restore | Relatório de permissões pré/pós-migração e testes negativos |
| SEC-02 | Reset administrativo de senha compartilhada bloqueado; fluxo por código único + SMTP | Credenciais SMTP e teste de envio, UX em homologação, rate limit | Testes de código inválido, expirado, repetido, e-mail inexistente |
| SEC-03 | Segredo estático excluído e carimbo de conta revogável | Análise de possível exposição prévia e rotação de credenciais se aplicável | Checklist do responsável pela VPS e contas |
| SEC-04 | Grupos com validação de proprietário, hierarquia, instituição e usuário multi-institucional | Testar endpoints, course scope, workers, Moodle capabilities, dois tenants | Testes de adversário A/B por recurso e conexão, revisão independente |
| SEC-05 | Listagem restrita; deleção administrativa em massa HTTP 410 | Desenhar processo de baixa por instituição sem apagar conta global e produzir auditoria durável | Revisão de operador/controlador + prova de não destruição interinstitucional |
| SEC-06 | `VPS_SSH_HOST_KEY` obrigatório e `ssh-keyscan` não utilizado | Chave real do host validada fora de banda | Impressão digital confirmada pelo provedor, CI deploy de homologação |
| SEC-07 | Produção exige JWT e OAuth com HTTPS; credenciais MCP sem conta local não ganham grants | Verificar configuração efetiva, issuer/audience, chaves, revogação e endpoints | Output redigido de runtime, OAuth smoke e validação externa |
| PUB-01 | Guias excluídos da árvore futura e da imagem; corpus externo configurável | Verificar autorização/licença e histórico Git / forks / caches | Inventário e decisão documentada do titular |
| LGPD | ROPA, retenção, DSAR, incidentes e aviso propostos como minutas | Definir controlador, DPO, bases legais, contratos, backups, fornecedores, canais, RIPD quando aplicável | Aprovação formal e evidência de operações em homologação |

## Ativação de operações de escrita

As variáveis `FEATURES_MESSAGES_WRITE_ENABLED`, `FEATURES_ASSIGNMENT_FEEDBACK_WRITE_ENABLED`, `FEATURES_ASSIGNMENT_GRADE_WRITE_ENABLED` e `FEATURES_MCP_GRADING_WRITE_ENABLED` têm valor padrão **false** no deploy. Qualquer ativação exige decisão explícita da instituição responsável, teste de permissões por usuário/curso e confirmação humana para alterações acadêmicas. A flag não substitui controle de escopo nem autorização do Moodle.

## Varredura automática de dependências

O CI de segurança agora deve bloquear o build diante de avisos **altos ou críticos** no `npm audit` (incluindo ferramentas de build) e de `NU1903`/`NU1904` no NuGet Audit de dependências diretas e transitivas do .NET 10. A verificação depende da disponibilidade das bases de avisos dos respectivos registries e **não substitui** análise SAST, segredos, histórico Git ou inventário de componentes da imagem Docker. Não suprimir avisos sem justificativa e aceite registrado.

## Deploy com bloqueio de segurança

1. Não executar migrações 085–087 diretamente em produção sem backup, plano de rollback, janela de manutenção e reaprovisionamento administrativo confiável. A migração 085 revoga privilégios antigos intencionalmente.
2. Confirmar que `PasswordReset__Enabled` só seja ativado após preenchimento seguro de `SmtpHost`, `SmtpPort`, `SmtpUsername`, `SmtpPassword`, `SenderAddress` e `PublicBaseUrl` HTTPS. Senha SMTP no secret store, nunca no Git.
3. Manter `Registration__PublicEnabled=false` sem processo de verificação de e-mail/convite institucional.
4. Em produção, fixar JWT e HTTPS, verificar TLS do origin/edge, segurança do Docker, rede do PostgreSQL, armazenamento privado e `VPS_SSH_HOST_KEY` por canal externo.
5. Executar CI verde por commit; testar com dados sintéticos duas instituições, cursos distintos e conta simultaneamente vinculada a ambas; validar restauração e retenção.
6. Registrar parecer de Segurança, Controlador e Encarregado/Jurídico. Abrir PR revisado somente após os gates técnicos; merge em `master` e deploy exigem autorização operacional.

**Nunca declarar conformidade LGPD por existir um markdown ou por um teste unitário aprovado.**
