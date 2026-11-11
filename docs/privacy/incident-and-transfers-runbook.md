# Plano de resposta a incidentes e revisão de transferências internacionais

**Status: MINUTA sujeita à aprovação de Segurança, Jurídico e do(s) controlador(es).**

## Responsabilidades a preencher
- Controlador da instituição: **[identificação, contato de plantão, autoridade para notificar]**.
- Operador do conector: **[responsável técnico, contato de emergência]**.
- Encarregado/DPO: **[contato público e suplente; analisar eventual dispensa legal]**.
- Operador de hospedagem e provedores de IA/SMTP: **[inventário de fornecedores e contatos]**.

## Processo de incidente

1. Classificar e conter: isolar chaves, contas, endpoints, API keys, uploads e workers envolvidos, preservando disponibilidade mínima e evidências.
2. Documentar início de conhecimento pelo controlador, tipo de dado, quantidade estimada, possíveis menores, sistemas, acesso não autorizado comprovado ou suspeito e ações adotadas.
3. Preservar trilha sanitizada, logs de acesso e hashes de artefatos sem repetir senhas, chaves, corpos de tarefas de alunos ou payloads integrais em bilhetes.
4. Avaliar se o evento pode causar risco ou dano relevante. A decisão de notificar cabe ao controlador, com auxílio do operador.
5. Quando incidente comunicável, cumprir prazo aplicável: Resolução ANPD nº 15/2024 estabelece, em regra, **3 dias úteis** para comunicações à ANPD e aos titulares, ressalvadas hipóteses específicas; registrar marco temporal e eventual comunicação complementar.
6. Executar mitigação, correção, varredura de acessos, rotação de chaves e verificação de que não há novas exposições.
7. Conservar o registro e as ações de lições aprendidas conforme prazos legais e política aprovada.

Fonte oficial: https://www.gov.br/anpd/pt-br/canais_atendimento/agente-de-tratamento/comunicado-de-incidente-de-seguranca-cis

## Inventário de compartilhamentos/transferências

| Sistema | Região e fornecedor | Dados transmitidos | Contrato/suboperador | Transferência internacional | Ação |
|---|---|---|---|---|---|
| Moodle institucional | **[instituição/país]** | Notas, atividades, identificadores | **[contrato]** | Confirmar | Definir controlador e escopo |
| VPS/backup | **[provedor/país]** | Banco, segredos cifrados, logs | **[DPA]** | Confirmar | Verificar criptografia e acesso |
| Serviço de IA/ChatGPT | **[fornecedor, modelo, local]** | Conteúdo enviado via MCP mediante uso | **[termos/instrução]** | Confirmar | Minimização; regra de compartilhamento |
| SMTP recuperação | **[provedor/região]** | E-mail, link de uso único | **[DPA]** | Confirmar | Retenção e TLS |
| Observabilidade | **[provedor/região]** | Metadados técnicos, IP, erros | **[DPA]** | Confirmar | Redação e retenção |

Não presumir transferência internacional apenas porque há integração de IA: investigar efetivamente país de processamento e mecanismo jurídico. Se houver transferência sujeita à Resolução CD/ANPD nº 19/2024, avaliar hipótese de art. 33 LGPD, adoção de cláusulas-padrão ou outro instrumento válido, e transparência obrigatória aplicável.

Fonte: https://www.gov.br/anpd/pt-br/acesso-a-informacao/institucional/atos-normativos/regulamentacoes_anpd/resolucao-cd-anpd-no-19-de-23-de-agosto-de-2024
