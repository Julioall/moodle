# Matriz de evidência para correção

Leia antes de propor nota, salvar rascunho, exportar CSV ou preparar publicação.

| Pergunta | Fonte preferencial | Bloqueio/limite |
| --- | --- | --- |
| A entrega pertence ao item/estudante correto? | `prepare_ai_grading_batch` ou `manifest.json`, com `gradingItemId`, submission e atividade reconciliados | Identidade ambígua, hash de item incompatível ou item fora do escopo: não corrigir automaticamente. |
| O conteúdo necessário foi lido? | Resources individuais `resourceType='submission'` ou arquivos da parte ZIP realmente lida | Status/manifesto não provam leitura dos bytes. Falha de leitura, autorização, integridade ou limite: revisão humana; não inventar conteúdo. |
| O cliente não consegue abrir o ZIP? | `prepare_ai_grading_batch` paginado para obter URIs reais de submissão/contexto; leia-as com `resources/read` ou `read_moodle_resource` e salve propostas via `save_ai_grading_batch` | Não derive URIs de `artifactId` ou caminhos do manifesto. Sem schema legível, não importe resultados do pacote; itens sem evidência/contexto verificável ficam para revisão humana. |
| O contexto da atividade está correto? | `assignments[].contextResources` + `contextResourceRefs`; em resultado de pacote, `items[].contextHash` | Preserve o hash específico do item; não substitua por `activity.contextHash`. Se nenhum contexto corresponder com segurança, não inventar rubrica/critério. |
| Há escala/nota máxima? | Contexto do lote | Só atribuir nota numérica quando `maxGrade`/escala estiver disponível. |
| O lote está coberto? | Fluxo paginado: `expectedItems`, `preparedItems`, `missingItems`, `missingBatchCount`; pacote: status/manifesto por parte, `activityGroups[].parts[].gradingItemIds` e resultado de importação | Não declarar cobertura total com itens pendentes/ausentes. Para partes reaproveitadas ou retentadas, conte a união dos IDs, não a soma de seleções; `totalItems` não substitui os contadores de cobertura. |
| A proposta aponta para evidência real? | `proposal.evidence` | Cite ao menos um `artifactId` real da submissão e, quando disponível, a URI correspondente. Nunca invente UUID. |
| As URIs da proposta estão corretas? | `proposal.resourceUris` | Copie todas e somente as URIs dos resources da submissão. `contextResources` podem aparecer em evidências, não em `resourceUris`. |
| O usuário quer arquivo externo? | `save_ai_grading_batch` + `export_grading_corrections_csv` | CSV não publica no Moodle. |
| O resultado veio de um pacote? | `import_grading_package_results` com `packageId`, `manifestHash` e JSON de `formato-resposta.schema.json` | A importação valida e salva rascunhos locais; revise `complete`, `pending`, `rejected`, `saved`, `unchanged` e `errors`. Não publica nem substitui rascunhos conflitantes. |
| O usuário quer publicar? | `create_batch_grade_launch_preview` | Mostre a prévia completa; `allowPartial` e overwrite não são defaults. |
| A publicação terminou? | `confirm_batch_grade_launch` + `get_assisted_grading_batch_status` | `authorized` não é sucesso final; somente `commitStatus=Succeeded` confirma o item. |

## Situações especiais

- MIME desconhecido não é falha por si só; falha real é não conseguir obter/autorizar/ler/validar o resource.
- Entrega claramente de outra atividade pode receber nota 0 somente quando houver escala e o contrato do lote orientar esse tratamento; o feedback deve explicar a incompatibilidade e permanecer rascunho para revisão humana.
- Critério não verificável, contexto insuficiente ou cobertura parcial deve ficar explícito como gap/revisão humana.
- Nota/feedback existentes só podem ser sobrescritos com autorização explícita e `allowOverwriteExisting=true` no fluxo de prévia.
- `includeAlreadyGraded=true` é exclusivo de reavaliação solicitada.
- `ExecutionUnknown` nunca autoriza retry cego.
