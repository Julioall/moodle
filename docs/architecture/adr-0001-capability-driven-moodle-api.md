# ADR-0001: API Moodle orientada por capacidades

## Decisão

O conector descobre as funções Web Service em `core_webservice_get_site_info` para cada conexão Moodle e usa esse perfil, não a versão do Moodle, para selecionar comportamentos.

O transporte comum usa `POST /webservice/rest/server.php` com parâmetros `application/x-www-form-urlencoded`. Tokens, usuário e senha não são colocados em URLs. A lista retornada pelo token é a fonte de disponibilidade; a classificação por verbo é derivada do nome, sem um inventário fixo de funções.

Funções de consulta podem ser chamadas por `moodle_execute_read`. Escritas e remoções **só podem ser preparadas** quando a função tem política de escrita cadastrada em `MoodleWriteScopePolicy`. Para essas operações aprovadas, são obrigatórios `moodle_prepare_write` e `moodle_confirm_write`, feature flag, `CanWrite`, escopo específico da família, mesma conexão e confirmação literal. Funções desconhecidas ou destrutivas não classificadas são bloqueadas, mesmo que o Moodle as exponha e o token possua `moodle.write`.

## Consequências

- Cada alias pode ter funções e estratégias diferentes.
- Tools acadêmicas continuam sendo a superfície preferencial para fluxos recorrentes.
- A classificação é conservadora: se o nome não comprova ser consulta, a função não é tratada como leitura; e só chega ao fluxo de escrita se estiver explicitamente autorizada pela política de funções.
- Clientes MCP sem atualização dinâmica consultam `moodle_list_available_flows` para receber indisponibilidades explicadas.
