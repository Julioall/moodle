# Inventário preliminar LGPD — projeto Moodle Connector

Data: 11/11/2026. Documento interno em revisão; não é uma política pública definitiva.

| Atividade | Dados | Finalidade | Revisões necessárias |
| --- | --- | --- | --- |
| Contas | Nome, e-mail, hash de senha, IDs | Autenticação e controle de acesso | Escopo institucional, autenticação, auditoria |
| Integrações Moodle | URL, alias, credenciais cifradas, tokens | Acesso autorizado ao Moodle | Propriedade, isolamento, rotação de chaves |
| Cursos e alunos | Identificadores, entregas, avaliações, acessos | Acompanhamento pedagógico | Bases legais, minimização, instituições |
| Correções e IA | Arquivos submetidos, feedback, contexto e propostas | Auxílio na avaliação | Revisão humana, destinatários, retenção |
| Relatórios | Nomes, notas, planilhas CSV/XLSX | Gestão e análise | Arquivos privados, expiração, trilha |
| Segurança e auditoria | IPs, eventos e mensagens de erro | Segurança operacional | Retenção, anonimização de logs, acesso |
| MCP/terceiros | Solicitações e dados recuperados | Integração autorizada | Transparência, contrato, transferências |

## Pendências para o controlador
- Definir controlador/operador por instituição e contratos com prestadores.
- Determinar bases legais e finalidades específicas: consentimento não é presumido.
- Definir prazos de retenção de dados, backups, caches, relatórios, rascunhos e logs; testar eliminação.
- Documentar atendimento de direitos dos titulares, autenticação, portabilidade e correção de dados.
- Avaliar menores de idade, compartilhamento com IA, transferência internacional e RIPD conforme aplicável.
- Aprovar encarregado, contato institucional, política de privacidade pública e resposta a incidentes.
- Verificar licença de redistribuição dos conteúdos `public/pedagogic`.

**Limite:** a exclusão voluntária da conta e a expiração de alguns arquivos não comprovam eliminação de backups ou dados na origem Moodle. Validar no ambiente real antes de produção.
