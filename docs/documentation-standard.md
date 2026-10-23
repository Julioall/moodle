# Padrão de documentação

## Objetivo

Manter a documentação encontrável, previsível e sem múltiplas fontes concorrentes. Todo novo documento deve seguir esta regra antes de ser versionado.

## Nome de arquivo

- Usar somente letras minúsculas, números e hífens: `lowercase-kebab-case.md`.
- Usar extensão `.md` para documentação textual.
- Para ADRs, usar `adr-NNNN-descricao-curta.md`, com quatro dígitos sequenciais.
- Usar nomes por assunto, não por data, salvo documentos explicitamente históricos ou planos datados.
- Evitar espaços, acentos, maiúsculas, abreviações obscuras e nomes genéricos como `novo.md`, `final.md` ou `documento-2.md`.
- `README.md` é a única exceção: deve ser usado apenas como índice de uma pasta ou entrada do repositório.

## Localização

| Conteúdo | Pasta |
|---|---|
| Estado atual da aplicação e dos dados | `docs/application-reference.md` |
| Decisões e limites | `docs/architecture/` |
| Segurança, privacidade e controles | `docs/security/` |
| Deploy, setup operacional e troubleshooting | `docs/operations/` |
| Contratos, integrações e modelos técnicos | `docs/technical/` |

## Conteúdo mínimo

Todo documento deve declarar, quando aplicável: objetivo, escopo, status, autoridade, limitações e referências. Não declarar funcionalidades como implementadas sem evidência no código/testes ou sem marcar explicitamente como planejadas.

ADRs devem conter no mínimo `Status`, `Contexto`, `Decisão` e `Consequências`.

Specs ativas devem conter no mínimo `Status`, `Objetivo`, `Escopo`, `Fora de escopo`,
`Dependências`, `Critérios de aceite`, `Validação`, `Rollout` e `Rollback`. O índice em
`docs/specs/README.md` é a autoridade sobre seu estado; tarefas, PRs e evidências devem
referenciar o identificador da spec, sem copiar decisões para planos paralelos.

## Regras de manutenção

- Antes de criar um arquivo, procurar se já existe documento canônico sobre o assunto.
- Preferir atualizar/consolidar o documento canônico a criar uma cópia.
- Ao mover um arquivo versionado, usar `git mv` e atualizar todos os links.
- Ao consolidar um documento, atualizar a fonte canônica e remover a cópia substituída quando seu conteúdo estiver incorporado, for regenerável ou estiver obsoleto. Manter material histórico somente quando ele tiver valor durável próprio; não criar um arquivo por padrão.
- Não usar `CHANGELOG.md` para notas soltas ou estado de release; registrar estado atual na documentação canônica e histórico técnico somente quando houver valor de rastreabilidade.
- Toda alteração estrutural deve atualizar [docs/README.md](README.md) e os índices afetados. A revisão do inventário acontece durante a mudança; não manter uma auditoria datada em paralelo.
