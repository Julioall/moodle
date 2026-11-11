# Checklist de segurança — alterações de 11/11/2026

**Status: ainda não liberado para produção.** Branch de correções, sem alterações em master.

## Alterações implementadas
- Removido o `TemporaryRollout` com autorização universal. Usuários novos possuem somente permissões pessoais básicas de leitura e conexão Moodle.
- Grupos de permissões são modificáveis apenas pelo criador. Nenhum usuário pode conceder permissões além das próprias.
- Adicionar contas a grupos e modificar privilégios de terceiros exige vínculo institucional verificável; equipes pessoais não autorizam a operação.
- A lista administrativa de usuários foi limitada a membros das equipes institucionais administradas pelo solicitante.
- Métricas globais exigem permissões administrativas explícitas e atuais.
- Redefinição de senha administrativa compartilhada e exclusão em massa desativadas (HTTP 410). Exclusão voluntária da própria conta permanece.
- Chave pública SSH de host da VPS obrigatória e fixada em `known_hosts`, sem descoberta TOFU.
- Retirada configuração versionada de senha compartilhada de recuperação.

## Impacto da migração 085
Antes do deploy, realizar backup, listar e verificar concessões históricas, planejar rollback e janela de manutenção. A migração **revoga grants antigos** de `admin.view`, `tool.permission_groups.manage` e `tool.teams.manage`, além de permissões fora da linha-base de grupos "Acesso inicial". Administradores legítimos devem ser reprovisionados fora da inscrição pública, por procedimento supervisionado e auditado. Verificar manualmente overrides históricos de escrita e de notas.

## Pendente / NÃO VALIDADO
1. Fluxo seguro de recuperação por token único, validade curta, hash, verificação de identidade e invalidação de sessão.
2. Regras de exclusão administrativa segura para contas que pertencem a mais de uma instituição.
3. Revisão completa de isolamento em ferramentas MCP, conexões, relatórios, snapshots, caches e jobs.
4. Revalidação de permissões existentes em cookies/OAuth após revogação.
5. Segredos, issuer/audience OAuth HTTPS, `McpServerSecurity__RequireJwt`, `OAuth__RequireHttpsMetadata` e transferências internacionais.
6. Direitos de distribuição de arquivos institucionais rastreados em `public/pedagogic`.
7. Testes `dotnet test`, testes web, integração PostgreSQL, segurança adversarial e backup/restore.

**Critério de GO:** todos os testes e inspeções acima concluídos, revisão de segurança e LGPD assinada pelos responsáveis. Até lá, NO-GO.
