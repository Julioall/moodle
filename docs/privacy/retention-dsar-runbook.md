# Retenção, descarte e atendimento aos titulares — procedimento de trabalho

**Status: POLÍTICA TÉCNICA PROPOSTA — aprovação do controlador pendente.**

## Matriz preliminar de retenção

| Categoria | Mecanismo observado/proposto | Critério de eliminação | Evidência requerida |
|---|---|---|---|
| Senhas e tokens de sessão | Hash; carimbo de segurança; revogação após troca de senha | Revogar credenciais e sessões anteriores | Teste de replay de cookie, JWT e API key |
| Tokens de recuperação | Hash SHA-256, uso único e validade 15 min; limpeza automática | Remover após 24h da utilização/expiração | Log de limpeza sem destinatários/tokens |
| Pacotes brutos de avaliação | `RawFileRetentionDays` padrão 7 | Conferir execução real do worker e backups | Registro de expiração e consulta negativa |
| Rascunhos de avaliação | `DraftRetentionDays` padrão 180 | Validar finalidade e obrigação de guarda | Testes de retenção/negação |
| Relatórios, CSV/XLSX e blobs | Armazenamento privado e outbox de exclusão | Definir TTL por propósito, revogar URLs, proteger download; excluir copies/outbox | Log técnico + busca por artefato |
| Dados de estudantes na origem Moodle | Plataforma da instituição controladora | **Não eliminar unilateralmente** do Moodle de terceiros | Encaminhamento/autorização do controlador |
| Cache, snapshot e tarefas | Múltiplos subsistemas | Matriz por dataset/owner/conn; sem prazo arbitrário único | Teste de isolamento e limpeza por titular |
| Logs de auditoria, incidentes | Retenção necessária para segurança, com dados mínimos | Intervalos específicos aprovados, guarda por obrigação aplicável | Manifesto/versionamento de arquivos |
| Backups, réplica e exports locais | Fora do escopo da exclusão simples do portal | Janela de expiração, restauração de teste e supressão após restauração | Evidência de purge e teste de restore |

**Não aplicar uma eliminação automática genérica a notas acadêmicas**: avaliar retenção legal e papel da instituição. Dados pessoais em trabalhos de alunos podem conter informações sensíveis incidentalmente.

## Fluxo DSAR / direitos do titular

1. Receber solicitação por canal oficial **[PENDENTE: endereço, telefone ou portal definido pelo controlador]**; registrar número interno sem copiar documentos sensíveis em logs.
2. Confirmar identidade do solicitante de forma proporcional e segura; nunca usar apenas nome ou e-mail enviado na requisição.
3. Identificar controlador de cada dataset: conta própria do portal, Moodle da instituição, provedor de IA, armazenamento secundário ou backup.
4. Classificar o pedido: confirmação, acesso, correção, portabilidade quando aplicável, oposição, revisão, eliminação/bloqueio ou informação sobre compartilhamento.
5. Avaliar a base legal, eventuais obrigações de guarda e impedimentos; quando operador, encaminhar ao controlador e registrar instrução recebida.
6. Executar ação autenticada, com dupla checagem para exclusão: conta pessoal via autoatendimento; dados de estudante **somente** via decisão do controlador; jamais apagar globalmente uma conta multi-institucional para atender pedido de uma única instituição.
7. Registrar evidência minimizada: ID do chamado, decisão do controlador, datasets, etapas efetuadas, identificador do executor, validação da remoção e pendências de backup.
8. Responder pelo canal aprovado, registrar prazo e verificar se operações em terceiros/backup têm acompanhamento pendente.

## Testes obrigatórios

- Duas contas e duas instituições com massa sintética: A não exporta nem apaga dados de B.
- Excluir uma conta em homologação e verificar banco, snapshots, report jobs, armazenamento de blobs e credenciais; testar restauração a partir de backup e replay da ordem de exclusão.
- Exportação/download de relatório: sem acesso por ID arbitrário e arquivos temporários fora de `wwwroot`.
- Confirmar que a documentação operacional não promete apagamento imediato de backups ou sistemas da instituição sem meios para comprová-lo.

**Critério de conclusão:** matriz aprovada pelo controlador, canais e responsáveis nomeados, testes de eliminação/restauração registrados e sistema de acompanhamento implementado.
