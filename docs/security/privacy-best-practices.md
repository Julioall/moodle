# Boas Práticas de Privacidade

## Dados sensíveis

O conector pode lidar com:

- nomes de cursos;
- dados acadêmicos;
- notas;
- prazos;
- entregas;
- identificadores de usuário;
- credenciais Moodle protegidas.

## Não registrar

Nunca registrar em logs, issues, prints ou documentação:

- senha Moodle;
- token Moodle;
- JWT completo;
- API key;
- client secret;
- connection string com senha;
- dados pessoais de alunos sem necessidade operacional.

## Memória do usuário

- Nunca salve senhas, tokens, API keys, client secrets, connection strings ou qualquer outro segredo em `gerenciar_memoria_usuario`.
- Não salve nomes, identificadores, notas, entregas, diagnósticos ou outros dados pessoais e acadêmicos de alunos.
- Use `listar` para revisar o conteúdo armazenado e obter o `memoryId`; use `remover` quando a memória estiver incorreta, obsoleta ou quando o usuário pedir sua exclusão.
- Trate `remover` como ação destrutiva sobre estado interno, embora ela não altere o Moodle.
- Automatizações só devem salvar preferências, caminhos, correções e decisões duráveis, com escopo mínimo e origem (`explicit` ou `inferred`) fiel.

- Para conteudos extensos, use `salvar_documento_memoria_usuario`; as mesmas proibicoes de segredos e dados pessoais de alunos se aplicam aos documentos.
- Modelos reutilizaveis devem ficar como documento de memoria, com a memoria curta `category=modelo` apenas apontando para o conteudo completo.

## Uso por professores e tutores

- Consulte apenas cursos sob sua responsabilidade.
- Não compartilhe respostas contendo dados acadêmicos em canais não autorizados.
- Revise mensagens e feedbacks antes de confirmar qualquer ação futura de escrita.

## Uso por suporte/DevOps

- Ao diagnosticar problemas, use correlation id e códigos de erro.
- Prefira logs sanitizados.
- Não solicite credenciais reais de professores.
- Rotacione API keys em caso de exposição.

## Uso por coordenação

- Defina claramente quais perfis podem acessar dados agregados.
- Restrinja acesso a dados de alunos conforme política institucional.
- Evite decisões automáticas baseadas apenas em indicadores gerados pelo conector.
