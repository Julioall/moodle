# PUB-01: isolamento de material pedagógico institucional

Status: **mitigado no pacote de publicação, autorização jurídica pendente**.

A branch de hardening remove do estado futuro do repositório sete guias pedagógicos institucionais de `public/pedagogic/`, retira a exceção do `.gitignore` e deixa de empacotá-los por MSBuild ou Docker. O sistema continua iniciando sem essa base; a busca de orientações retorna vazio se o diretório não estiver presente.

Quem possuir **autorização documental de utilização** deve colocar os guias licenciados fora do repositório público e fora de `wwwroot`, em diretório com permissão de leitura exclusiva ao processo do app. Configure `PedagogicGuidance__RootPath` apontando para esse volume privado (ex.: `/app/data/private-pedagogic`). O conteúdo fornecido deve ser conferido quanto a licença, sigilo, restrições de circulação e minimização.

**Atenção:** retirar arquivos da árvore de uma branch NÃO elimina os blobs da `master` nem do histórico Git já publicado. Antes da divulgação oficial, o responsável pela titularidade deve decidir se é necessário remover cópias antigas do repositório e solicitar expurgo de caches externos quando possível. Não reescrever histórico público unilateralmente: isso exige análise de impacto e coordenação com colaboradores/forks.

Critério para dar PUB-01 como encerrado: inventário dos sete documentos, comprovação de permissão/licença ou retirada efetiva de todas as cópias públicas, validação do build sem o material, avaliação do histórico e aceite formal do titular/responsável.
