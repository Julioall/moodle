import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { useSession } from '../../auth/useSession';
import { useMoodleConnections } from '../../connections/useConnectionScope';
import { coursesGateway } from '../../courses/courses-gateway';
import { useReportPreview } from '../useReportPreview';
import { ReportPreviewTable } from '../ReportPreviewTable';
import { ReportPreviewColumnHeaders, type ReportPreviewColumnActions } from '../ReportPreviewColumnHeaders';
import type { ReportDefinition, ReportField } from './report-templates-gateway';

function previewErrorMessage(error: unknown) {
  const failure = typeof error === 'object' && error !== null ? error as { status?: unknown; body?: { error?: { code?: unknown } } } : undefined;
  const code = typeof failure?.body?.error?.code === 'string' ? failure.body.error.code : undefined;
  const status = typeof failure?.status === 'number' ? failure.status : undefined;
  switch (code) {
    case 'preview_busy': return 'Outra leitura está em andamento. Aguarde alguns segundos e tente novamente.';
    case 'connector_rate_limited': return 'Muitas consultas em pouco tempo. Aguarde o prazo indicado e tente novamente.';
    case 'preview_timeout': return 'A leitura ultrapassou o tempo disponível. Tente outro curso ou reduza os campos.';
    case 'connection_changed': return 'A conexão foi alterada. Selecione-a novamente para continuar.';
    case 'course_unavailable': return 'Não foi possível confirmar o curso na conexão selecionada.';
    case 'population_unavailable': return 'Não foi possível consultar a população deste curso. Verifique as permissões da conexão Moodle.';
    case 'report_source_unavailable': return 'O Moodle não respondeu à leitura dos dados do relatório.';
    case 'invalid_definition': return 'Confira os campos e títulos selecionados antes de carregar a amostra.';
  }
  switch (status) {
    case 429: return 'Muitas consultas em pouco tempo. Aguarde e tente novamente.';
    case 400: return 'A solicitação da prévia não foi aceita. Confira os campos e o curso selecionados.';
    case 401: return 'Sua sessão expirou. Entre novamente para carregar a amostra.';
    case 403: return 'Sua conta não tem acesso aos dados deste curso.';
    case 404: return 'A conexão ou o curso não foi encontrado. Selecione-os novamente.';
    case 409: return 'A conexão ou o curso mudou. Atualize a seleção e tente novamente.';
    case 502: return 'Não foi possível ler os dados necessários do Moodle.';
    case 504: return 'A leitura ultrapassou o tempo disponível. Tente outro curso ou reduza os campos.';
    default: return 'Não foi possível carregar a amostra. Tente novamente.';
  }
}

/** Mounted only after an explicit choice to load a temporary sample. */
export function ReportTemplateSample({ definition, fields, columnActions, valid }: { definition: ReportDefinition; fields: ReportField[]; columnActions: ReportPreviewColumnActions; valid: boolean }) {
  const { user } = useSession();
  const connections = useMoodleConnections();
  const [connectionId, setConnectionId] = useState('');
  const [courseId, setCourseId] = useState('');
  const [page, setPage] = useState(1);
  const connection = connections.data?.data.find(item => item.connectionId === connectionId);
  const scope = user && connection ? `editor:${user.id}:${connection.connectionId}` : '';
  const courses = useQuery({ queryKey: ['report-sample-courses', scope, page], queryFn: ({ signal }) => coursesGateway.list(connection!.connectionRef, page, 20, signal), enabled: !!scope, retry: false, gcTime: 0 });
  const preview = useReportPreview(scope, valid && connection && courseId ? { definition, connectionRef: connection.connectionRef, courseIds: [courseId] } : undefined);
  const previewColumns = definition.columns.map(column => ({ fieldId: column.fieldId, label: column.label?.trim() || fields.find(field => field.fieldId === column.fieldId)?.label || column.fieldId }));
  const changePage = (next: number) => { setCourseId(''); setPage(next); };
  return <section className="min-w-0 space-y-4" aria-label="Curso de amostra">
    <p className="text-sm text-muted-foreground">Até 20 linhas após leitura real. A conexão e o curso de amostra não são salvos no modelo.</p>
    <label className="block space-y-2"><span>Conexão de amostra</span><select className="h-10 w-full min-w-0 rounded-md border bg-background px-3" value={connectionId} onChange={event => { setConnectionId(event.target.value); setCourseId(''); setPage(1); }}><option value="">Selecione uma conexão</option>{connections.data?.data.filter(item => item.connectionId).map(item => <option key={item.connectionId} value={item.connectionId}>{item.alias}</option>)}</select></label>
    {connections.isError && <p role="alert">Não foi possível carregar as conexões. <Button variant="link" onClick={() => void connections.refetch()}>Tentar novamente</Button></p>}
    {scope && <><label className="block space-y-2"><span>Curso / UC de amostra</span><select className="h-10 w-full min-w-0 rounded-md border bg-background px-3" value={courseId} disabled={courses.isPending || courses.isError} onChange={event => setCourseId(event.target.value)}><option value="">Selecione um curso</option>{courses.data?.data.map(item => <option key={item.courseId} value={item.courseId}>{item.fullName}</option>)}</select></label>
      <div className="flex items-center gap-2"><Button variant="outline" size="sm" disabled={page === 1 || courses.isFetching} onClick={() => changePage(page - 1)}>Cursos anteriores</Button><span>Página {page}</span><Button variant="outline" size="sm" disabled={!courses.data?.meta.hasMore || courses.isFetching} onClick={() => changePage(page + 1)}>Próximos cursos</Button></div>
      {courses.isPending && <p role="status">Carregando cursos…</p>}{courses.isError && <p role="alert">Não foi possível carregar os cursos. <Button variant="link" onClick={() => void courses.refetch()}>Tentar novamente</Button></p>}
      {courses.isSuccess && !courses.data.data.length && <p>Nenhum curso disponível nesta página.</p>}</>}
    {courseId && !valid && <p role="status">Confira os campos e títulos antes de carregar a amostra.</p>}
    {courseId && valid && preview.isPending && <p role="status">Carregando amostra…</p>}
    {preview.isError && <p role="alert">{previewErrorMessage(preview.error)} <Button variant="link" onClick={() => void preview.refetch()}>Tentar novamente</Button></p>}
    {preview.data && <ReportPreviewTable preview={preview.data} columns={previewColumns} columnActions={columnActions} />}
    {!preview.data && !preview.isError && <div className="max-h-[65vh] overflow-auto rounded-md border"><table className="w-full text-left text-sm"><thead className="sticky top-0"><ReportPreviewColumnHeaders columns={previewColumns} actions={columnActions} /></thead><tbody><tr><td colSpan={Math.max(previewColumns.length, 1)} className="px-3 py-6 text-center text-muted-foreground">{courseId && preview.isPending && valid ? 'Carregando amostra…' : 'Os dados da amostra serão exibidos aqui após escolher um curso conectado.'}</td></tr></tbody></table></div>}
  </section>;
}
