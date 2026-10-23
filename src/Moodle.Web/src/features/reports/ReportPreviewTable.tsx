import type { ReportPreview } from './templates/report-templates-gateway';
import { ReportPreviewColumnHeaders, type ReportPreviewColumn, type ReportPreviewColumnActions } from './ReportPreviewColumnHeaders';

function warningMessage(code: string) {
  const messages: Record<string, string> = {
    participant_classification_incomplete: 'Há participantes sem classificação suficiente; a exportação completa foi bloqueada.',
    participant_classification_uncertain: 'A população inclui participantes identificados por fallback.',
    activity_preview_budget: 'Os campos de atividade não foram calculados na amostra por limite de leitura.',
    preview_read_budget: 'Parte dos campos de nota ficou fora do limite de leitura da amostra.',
    preview_deadline: 'A prévia atingiu seu tempo máximo.',
    sample_empty_partial: 'A amostra não retornou linhas; isso não confirma ausência de participantes.',
    population_unavailable: 'A população do curso não pôde ser carregada.',
    grade_unavailable: 'Alguns dados de nota não puderam ser carregados.',
    access_unavailable: 'Alguns dados de acesso não estão disponíveis.',
    membership_unavailable: 'Alguns dados de matrícula ou papéis não estão disponíveis.',
    category_path_unavailable: 'O caminho da categoria não está disponível.',
    email_unavailable: 'O e-mail não está disponível para todos os participantes.',
    tracking_unavailable: 'Os campos de situação não puderam ser calculados.',
    activity_coverage_incomplete: 'Os campos de atividade estão incompletos.',
    activity_submission_mode_unknown: 'O modo de submissão de algumas atividades não pôde ser confirmado; os campos de atividade estão indisponíveis.',
    tracking_coverage_incomplete: 'Os campos de situação estão incompletos.',
    cold_snapshot_stale: 'Alguns metadados vieram de uma cópia antiga.',
  };
  return messages[code] ?? 'Alguns dados não puderam ser carregados.';
}

export function ReportPreviewTable({ preview, columns = preview.columns, columnActions }: { preview: ReportPreview; columns?: ReportPreviewColumn[]; columnActions?: ReportPreviewColumnActions }) {
  const sampledCourses = preview.sampledCourses ?? preview.sampledCourseIds.map((courseId) => ({ courseId, name: undefined }));
  const omittedCourses = Math.max(0, preview.requestedCourseCount - preview.sampledCourseIds.length);
  return <section aria-label="Amostra de dados" className="min-w-0 space-y-3">
    <p className="text-sm" role="status">{preview.returnedRows} linhas de amostra · {preview.sampledCourseIds.length} de {preview.requestedCourseCount} cursos. A geração fará uma nova leitura dos dados atuais.</p>
    {sampledCourses.length > 0 && <p className="text-sm text-muted-foreground">Cursos consultados na amostra: {sampledCourses.map((course) => course.name || `Curso ${course.courseId}`).join(' · ')}</p>}
    {omittedCourses > 0 && <p className="text-sm text-amber-700" role="status">Por limite de leitura, {omittedCourses} {omittedCourses === 1 ? 'curso não aparece' : 'cursos não aparecem'} nesta amostra. Todos os {preview.requestedCourseCount} cursos selecionados serão processados no arquivo final.</p>}
    {preview.truncated && <p role="status" className="text-sm text-amber-700">A amostra é limitada e não representa todos os cursos ou linhas selecionados.</p>}
    {preview.partial && <p role="status" className="text-sm text-amber-700">A prévia é parcial; confira os avisos e a cobertura dos cursos consultados.</p>}
    {preview.warnings.length > 0 && <ul className="list-inside list-disc text-sm text-muted-foreground" aria-label="Avisos da prévia">{preview.warnings.map((warning) => <li key={warning}>{warningMessage(warning)}</li>)}</ul>}
    <div className="max-h-[65vh] overflow-auto rounded-md border"><table className="w-full text-left text-sm"><thead className="sticky top-0"><ReportPreviewColumnHeaders columns={columns} actions={columnActions} /></thead><tbody>{preview.rows.map((row) => <tr key={`${row.courseId}:${row.studentId}`} className="border-t">{columns.map(column => {
      const cellIndex = preview.columns.findIndex(previewColumn => previewColumn.fieldId === column.fieldId);
      const cell = cellIndex >= 0 ? row.cells[cellIndex] : undefined;
      const value = preview.columns[cellIndex]?.type === 'DateTime' ? formatUtcDateTime(cell?.value) : String(cell?.value ?? '');
      return <td key={column.fieldId} className="whitespace-nowrap px-3 py-2">{!cell || cell.state === 'Missing' ? '—' : cell.state === 'Unavailable' ? <span title={cell.reason ?? undefined} className="text-amber-700">Indisponível</span> : value}</td>;
    })}</tr>)}</tbody></table></div>
    {preview.rows.length === 0 && <p role="status" className="text-sm">Nenhuma linha retornada nesta amostra{preview.partial ? '; isso não confirma ausência de estudantes' : ''}.</p>}
    <details className="text-xs text-muted-foreground"><summary>Origem e datas da leitura</summary><p>Coleta: {formatUtcDateTime(preview.referenceAt)} · Prévia: {formatUtcDateTime(preview.generatedAt)}</p>{preview.coverage.map((item, index) => <p key={index}>{item.family}: {item.source} · leitura {item.readAt ? formatUtcDateTime(item.readAt) : '—'} · snapshot {item.snapshotAt ? formatUtcDateTime(item.snapshotAt) : '—'} · {item.complete ? 'coberto' : 'incompleto'}{item.uncertainCount != null ? ` · ${item.uncertainCount} participante(s) sem classificação segura` : ''}</p>)}</details>
  </section>;
}

function formatUtcDateTime(value: string | number | null | undefined): string {
  if (typeof value !== 'string') return String(value ?? '');
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return value;
  return `${new Intl.DateTimeFormat('pt-BR', {
    day: '2-digit', month: '2-digit', year: 'numeric',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone: 'UTC',
  }).format(date)} UTC`;
}
