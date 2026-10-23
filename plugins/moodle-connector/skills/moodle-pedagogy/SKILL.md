---
name: moodle-pedagogy
description: "Recuperar orientações pedagógicas institucionais e modelos duráveis do Moodle para avaliação, feedback, recuperação, fórum, comunicação ou relatório. Use quando a tarefa exige esse referencial. Não use para consultas comuns de dados Moodle nem trate a orientação como autorização para escrever no Moodle."
---

# moodle-pedagogy

Use esta skill para recuperar referência pedagógica antes de gerar uma decisão, texto ou interpretação que dependa de norma/modelo institucional.

## Entradas

Converta a intenção em conceitos objetivos de busca e preserve contexto de curso somente quando ele for relevante à orientação.

## Fluxo

1. Se houver indicação de modelo/documento já salvo, use `moodle-memory` para localizá-lo e lê-lo.
2. Consulte `get_pedagogical_guidelines` com os conceitos centrais da tarefa, por exemplo avaliação formativa, recuperação, acompanhamento ou fórum.
3. Aplique somente a orientação realmente recuperada. Não invente norma institucional nem trate ausência de resultado como regra implícita.
4. Entregue o contexto recuperado à skill de domínio (`moodle-grading`, `moodle-follow-up`, `moodle-forums`, `moodle-messaging` ou `moodle-reports`).

## Limites

Orientação pedagógica não autoriza acesso, envio de mensagem, publicação em fórum, lançamento de nota, aprovação, reprovação ou qualquer outra escrita. Essas ações continuam sujeitas ao fluxo e à confirmação do domínio.

## Saída

Resuma a orientação aplicável e identifique suas limitações. Quando não houver evidência suficiente, declare isso em vez de fabricar regra.
