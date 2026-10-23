# Instruções para agentes

- Para perguntas sobre o estado atual da aplicação ou seus dados, comece em [docs/application-reference.md](docs/application-reference.md) e abra apenas as fontes relevantes ao trabalho.
- Código, configuração e scripts SQL definem o comportamento implementado. Specs em Draft, Review, Approved ou Implementing descrevem mudanças propostas ou em andamento; não as trate como prova do estado atual.
- Ao alterar entidades, escopos, retenção, exclusão de conta ou processamento de dados Moodle, confira as fontes indicadas na referência e atualize-a na mesma mudança.
- Não inclua credenciais, tokens, payloads Moodle, submissões ou dados pessoais reais em documentação, logs, exemplos ou evidências versionadas.
- Antes de enviar ou atualizar um PR, execute localmente as validações relevantes para a mudança. Use [docs/operations/release-certification.md](docs/operations/release-certification.md) como catálogo dos gates; deixe o CI remoto como confirmação final, não como primeira etapa de diagnóstico.
- Para uma mudança somente em `docs/`, o PR continua usando o gate completo; depois do merge, o push para `main` usa a validação documental leve. Evite enviar atualizações intermediárias de PR antes de corrigir localmente falhas conhecidas, pois cada atualização pode iniciar outra execução completa de CI.
