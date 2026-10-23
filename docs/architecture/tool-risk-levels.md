# Tool Risk Levels

As tools MCP devem declarar risco operacional antes de serem publicadas:

- `ReadOnly`: consulta sem dados sensiveis ou escrita.
- `SensitiveRead`: consulta com dados academicos, pessoais ou avaliativos.
- `DraftOnly`: gera rascunho sem publicar no Moodle.
- `HumanConfirmedWrite`: escrita comum que exige confirmacao humana.
- `CriticalHumanConfirmedWrite`: escrita de alto impacto que exige confirmacao humana reforcada.
- `AdminWrite`: operacao administrativa restrita.

Tools de escrita para professores devem usar `HumanConfirmedWrite` ou superior e passar pelo fluxo de pending action.
