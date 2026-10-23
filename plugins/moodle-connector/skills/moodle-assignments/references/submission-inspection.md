# Submission inspection reference

Use esta referencia quando uma tarefa depender de anexos, formato, integridade, conteudo extraido ou cobertura de arquivos.

## Pipeline atual do conector

1. A submissao e resolvida pelo Moodle; o agente nao baixa `pluginfile.php` diretamente.
2. O fluxo unificado de lotes persiste metadados e referencias dos anexos; no caminho MCP, o download pesado e a extracao nao sao pre-requisitos.
3. `prepare_ai_grading_batch` registra os anexos do lote como resources opacos com `name`, `mimeType`, `size` e URI `moodle://resource/...`. Essas referencias ficam no `structuredContent` e nao sao promovidas automaticamente a anexos no `content` da tool.
4. O cliente le o resource/file original explicitamente pelo gateway seguro, usando `resources/read` ou `read_moodle_resource`; o gateway revalida identidade, conexao, escopo, vinculo, tamanho e integridade antes de devolver os bytes.
5. A correção assistida não executa extração local: o chat recebe primeiro as referencias e materializa apenas os arquivos que realmente precisa ler.

O MIME é apenas metadado de transporte. RTF, DOC/XLS/PPT, OpenDocument, imagens, ZIP e formatos desconhecidos seguem como bytes originais; não há whitelist de parser para limitar a entrega.

Limites versionados relevantes: 25 MB por arquivo, 10 arquivos por submissao, 120.000 caracteres extraidos, 25 entradas consideradas por ZIP e 20 MB por entrada interna. Confirme `appsettings.json`/ambiente antes de prometer esses valores em outro deployment.

## Politica de evidencia

- `pending`: referência original aguardando registro no MCP Resource.
- `succeeded`, `ocr_extracted`, `scanned_pdf`, `unsupported_format`, `file_too_large`, `empty` e `failed`: estados históricos de instalações anteriores; não controlam a entrega atual.

O anexo só deve ser tratado como inacessível quando o registro/leitura do resource falhar, o resource expirar/for proibido ou os limites de transporte forem excedidos.

## Pre-processamento de bundles locais

Para um ZIP exportado localmente, execute:

```powershell
python plugins/moodle-connector/skills/moodle-assignments/scripts/inspect_submission_bundle.py "C:\caminho\entregas.zip" --pretty
```

O script apenas inspeciona o arquivo local: nao extrai para o disco, detecta assinaturas, lista entradas, identifica containers DOCX/XLSX/PPTX, percorre ZIPs internos ate a profundidade configurada e sinaliza path traversal, arquivos criptografados, excesso de membros, tamanho descomprimido e razao de compressao suspeita. Ele nao participa do fluxo MCP de correção.

## Lacunas deliberadas

Formulas e resultados salvos de XLSX, dimensoes/transparencia de imagens, camadas XCF/PSD, renderizacao de DOCX/PPTX/XLSX, frames de video e transcricao de audio ainda nao fazem parte do contrato MCP atual. Para esses criterios, marque verificacao pendente ou proponha uma ferramenta dedicada; nao invente evidencia a partir do nome/extensao.
