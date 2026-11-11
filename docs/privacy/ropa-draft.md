# Registro de operações de tratamento — minuta ROPA (LGPD, art. 37)

**Situação:** EM VALIDAÇÃO • Versão técnica: 11/11/2026 • Não constitui aprovação jurídica.

O registro abaixo deriva das funcionalidades identificadas no código do Moodle Connector. Para cada instituição, preencher responsável, fundamento legal, finalidade específica, local de hospedagem, contrato, mecanismos de controle, prazo aprovado e evidência. O aplicativo não determina sozinho se atua como controlador ou operador; essa qualificação deve refletir o tratamento real.

| ID | Processo e finalidade | Categorias de titulares e dados | Destinatários/sistemas | Base legal e responsável | Retenção e evidência |
|---|---|---|---|---|---|
| A001 | Cadastro e controle de acesso | Tutores, monitores e gestores; nome, e-mail, credenciais protegidas e permissões | Portal, PostgreSQL, OAuth | **PENDENTE: controlador, base legal, encarregado** | Prazo de conta, sessão, log e retirada de vínculo **a aprovar** |
| A002 | Conexões Moodle de cada usuário | Credencial Moodle, endpoint HTTPS, alias, tokens e chaves | Moodle de origem, PostgreSQL, cofre de segredos | **PENDENTE: instituição e DPA** | Revogação em desligamento, backup e rotação **a aprovar** |
| A003 | Consulta acadêmica | Estudantes e equipe docente; matrículas, notas, acessos, entregas | Moodle, snapshots e relatórios | **PENDENTE: finalidade por instituição e base legal** | Retenção por dataset e vínculos **a aprovar** |
| A004 | Correção assistida | Trabalhos, documentos, avaliação, feedback, possibilidade de texto sensível | Moodle, pipelines de extração e IA, armazenamento privado | **PENDENTE: operador/processador, base, fluxo de revisão humana** | Raw de correção: opção padrão 7 dias; rascunhos: 180 dias; comprovar configuração e eliminação |
| A005 | Relatórios | Identificadores de alunos, notas, curso, resultados CSV/XLSX | Jobs, armazenamento privado, download por proprietário | **PENDENTE: controlador e destino dos exports** | TTL, arquivos baixados, cópias e backup **a aprovar** |
| A006 | Agenda, tarefas e mensagens | Participantes, turma, comentários, tarefas, contatos | Portal, Moodle messaging, equipe autorizada | **PENDENTE: papéis e compartilhamento** | Política específica de comentário e tarefa **a aprovar** |
| A007 | Auditoria e segurança | IP, ID de usuário, identificador de ferramenta, eventos, erros sanitizados | Logs, banco e alertas | **PENDENTE: base, acesso mínimo, finalidade de segurança** | Intervalos por classe de log e obrigações de guarda **a aprovar** |
| A008 | Memórias e documentos pessoais | Usuário do portal, instruções e conteúdo livre | Banco, armazenamento e busca interna | **PENDENTE: dados permitidos, função e base** | Não armazenar dados de alunos em memória; testes de retenção/exclusão **a executar** |
| A009 | MCP e IA | Identificador do usuário, dados fornecidos às tools, escopos e tokens | Cliente MCP, serviço de IA, fornecedores identificados | **PENDENTE: terceiros, cláusulas, base e transferência** | Dados exportados ao fornecedor e eliminação **a confirmar contratualmente** |
| A010 | Recuperação individual de acesso | E-mail do titular e hash de token aleatório | PostgreSQL, SMTP configurado | **PENDENTE: aviso e fornecedor SMTP** | Token 15 min; limpeza programada 24 h após uso/expiração; comprovação por testes |

## Registro complementar obrigatório por controlador

Para cada operação, preencher: **identificação do controlador e operador, canal do encarregado, categorias de titulares, base legal aprovada, finalidade granular, dados estritamente necessários, origem, compartilhamento, suboperadores, local/país, instrumento de transferência, controles técnicos e administrativos, retenção em primário/cache/exportação/backup, eliminação, exercício de direitos, risco/RIPD, versão e aceite**.

Registrar anualmente (e após qualquer alteração relevante) revisão de fluxos reais com responsáveis de Segurança, Jurídico/DPO, Produto e instituição contratante. Sem esses dados este é um ROPA técnico incompleto, não definitivo.
