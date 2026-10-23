import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, LoaderCircle, MoreHorizontal } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Badge } from '@/components/ui/badge';
import { Progress } from '@/components/ui/progress';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { useSession } from '../auth/useSession';
import { useConnectionScope } from '../connections/useConnectionScope';
import { useReportSelection } from './selection/useReportSelection';
import { reportsGateway, type ReportJob } from './reports-gateway';
import { ReportSectionHeader } from './ReportSectionHeader';
import { retryAwarePollingInterval } from '../../integrations/http/api-client';

const statusLabels = { queued: 'Na fila', running: 'Processando', completed: 'Disponível', failed: 'Falhou' };
function failureMessage(code?: string) {
  switch (code) {
    case 'participant_classification_incomplete': return 'A população inclui papéis que não puderam ser classificados. Revise os papéis na conexão Moodle e gere novamente.';
    case 'report_row_limit': return 'O resultado excedeu o limite de 10.000 linhas. Reduza o número de cursos ou o público do relatório.';
    case 'report_activity_limit': return 'O curso excedeu o limite de 25 atividades. Remova os campos de atividade/situação do modelo ou reduza o escopo.';
    case 'report_deadline_exceeded': return 'A geração excedeu o limite de 10 minutos. Reduza cursos ou campos e tente novamente.';
    case 'report_storage_limit_exceeded': return 'O arquivo excederia o espaço disponível. Exclua relatórios antigos ou reduza o escopo.';
    case 'report_permission_denied': return 'Sua permissão para gerar relatórios mudou. Atualize a sessão ou peça acesso ao administrador.';
    case 'connection_changed': return 'A conexão Moodle mudou. Volte à lista, confirme a conexão e gere novamente.';
    case 'course_unavailable': return 'Um ou mais cursos não estão disponíveis nesta conexão. Revise a seleção.';
    case 'population_unavailable': return 'A população de participantes não pôde ser carregada. Verifique a conexão e tente novamente.';
    case 'invalid_report_snapshot': return 'A definição ou seleção capturada não está mais disponível. Crie um novo relatório.';
    case 'report_lease_lost': return 'Este processamento foi interrompido e será necessário gerar um novo relatório.';
    case 'report_course_limit': return 'A seleção excedeu o limite de cursos por relatório. Reduza a seleção e tente novamente.';
    case 'invalid_course_ids': return 'A seleção não está mais disponível. Volte à lista e selecione os cursos novamente.';
    default: return 'Não foi possível gerar o arquivo. Revise a seleção e tente novamente.';
  }
}
function incompleteMessage(job: ReportJob) {
  const reasons = job.resultMetadata?.coverage.map((coverage) => coverage.reason) ?? [];
  if (reasons.includes('activity_submission_mode_unknown'))
    return 'Relatório parcial: o modo de submissão de algumas atividades não pôde ser confirmado.';
  if (reasons.includes('activity_coverage_incomplete'))
    return 'Relatório parcial: a cobertura de atividades ficou incompleta.';
  return 'Relatório incompleto: há campos sem cobertura suficiente.';
}
function formatDate(value?: string) { return value ? new Date(value).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' }) : '—'; }
function formatBytes(bytes: number) { return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KiB` : `${(bytes / (1024 * 1024)).toFixed(1)} MiB`; }
function title(job: ReportJob) {
  const reportName = job.templateNameSnapshot ?? (job.reportType === 'grades' ? 'Relatório de notas legado' : 'Relatório');
  const courseNames = job.courses?.map((course) => course.name.trim()).filter(Boolean) ?? [];
  const courseName = courseNames.length === 1 ? courseNames[0]
    : courseNames.length > 1 ? `${courseNames[0]} + ${Math.max(job.totalCourses - 1, courseNames.length - 1)} cursos`
      : undefined;
  return courseName ? `${courseName} · ${reportName}` : reportName;
}

export function ReportHistoryPage() {
  const { user, can, reportTemplatesEnabled } = useSession();
  const { connections } = useConnectionScope();
  const selection = useReportSelection();
  const navigate = useNavigate();
  const client = useQueryClient();
  const [criteria, setCriteria] = useState<{ page: number; search: string; status: ReportJob['status'] | ''; connectionRef: string }>({ page: 1, search: '', status: '', connectionRef: '' });
  const [deleting, setDeleting] = useState<ReportJob>();
  const jobs = useQuery({
    queryKey: ['app', 'report-history', user?.id, criteria, connections.data?.data.map((connection) => connection.connectionId)],
    queryFn: ({ signal }) => reportsGateway.jobs(criteria.page, 20, criteria, signal),
    enabled: Boolean(user && can('reports.view')), retry: false, staleTime: 2_000,
    refetchInterval: (query) => query.state.data?.data.some((job) => job.status === 'queued' || job.status === 'running')
      ? retryAwarePollingInterval(query.state.error, 10_000)
      : false,
  });
  const remove = useMutation({
    mutationFn: (id: string) => reportsGateway.deleteJob(id), retry: false,
    onSuccess: () => { setDeleting(undefined); void client.invalidateQueries({ queryKey: ['app', 'report-history'] }); void client.invalidateQueries({ queryKey: ['app', 'report-jobs-notifications'] }); },
  });
  const refresh = useMutation({
    mutationFn: (id: string) => reportsGateway.refreshJob(id), retry: false,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['app', 'report-history'] });
      void client.invalidateQueries({ queryKey: ['app', 'report-jobs-notifications'] });
    },
  });
  const start = () => { selection.store.start(); navigate('/meus-cursos'); };
  const setFilter = (patch: Partial<typeof criteria>) => setCriteria((current) => ({ ...current, ...patch, page: 1 }));
  const used = jobs.data?.meta.storageUsedBytes ?? 0;
  const limit = jobs.data?.meta.storageLimitBytes ?? 300 * 1024 * 1024;
  return <main className="space-y-6 animate-fade-in" aria-labelledby="reports-page-title">
    <ReportSectionHeader active="history" showTabs={reportTemplatesEnabled} action={reportTemplatesEnabled && <Button disabled={!selection.available} onClick={start}>Novo Relatório</Button>} />
    <section className="space-y-2 rounded-lg border bg-card p-4" aria-label="Armazenamento"><div className="flex justify-between gap-3 text-sm"><span>Armazenamento de arquivos</span><span>{formatBytes(used)} / {formatBytes(limit)}</span></div><Progress value={limit ? Math.min(100, used / limit * 100) : 0} aria-label="Uso de armazenamento" /><p className="text-xs text-muted-foreground">{formatBytes(jobs.data?.meta.storageAvailableBytes ?? limit)} disponíveis</p></section>
    <section className="flex flex-wrap items-end gap-3 rounded-lg border bg-card p-4" aria-label="Filtros do histórico">
      <label className="min-w-48 flex-1 text-sm">Buscar relatório<Input type="search" maxLength={120} value={criteria.search} placeholder="Nome, curso ou arquivo" onChange={(event) => setFilter({ search: event.target.value })} /></label>
      <label className="text-sm">Status<select className="mt-1 block h-10 rounded-md border bg-background px-3" value={criteria.status} onChange={(event) => setFilter({ status: event.target.value as typeof criteria.status })}><option value="">Todos</option>{Object.entries(statusLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label className="text-sm">Conexão<select className="mt-1 block h-10 max-w-60 rounded-md border bg-background px-3" value={criteria.connectionRef} onChange={(event) => setFilter({ connectionRef: event.target.value })}><option value="">Todas</option>{connections.data?.data.map((connection) => <option key={connection.connectionId ?? connection.connectionRef} value={connection.connectionRef}>{connection.alias || connection.connectionRef}</option>)}</select></label>
      <Button variant="ghost" onClick={() => setCriteria({ page: 1, search: '', status: '', connectionRef: '' })}>Limpar filtros</Button>
    </section>
    {jobs.isPending && <p role="status">Carregando relatórios…</p>}
    {jobs.isError && <p role="alert" className="text-destructive">Não foi possível atualizar o histórico. <Button variant="link" onClick={() => void jobs.refetch()}>Tentar novamente</Button></p>}
    {refresh.isSuccess && <p role="status">Atualização solicitada. O novo arquivo aparecerá no histórico quando estiver pronto.</p>}
    {refresh.isError && <p role="alert" className="text-destructive">Não foi possível atualizar os dados. Verifique se a conexão Moodle e a seleção original continuam disponíveis.</p>}
    {jobs.data && jobs.data.data.length === 0 && <div className="rounded-lg border border-dashed p-12 text-center"><p className="font-medium">Nenhum relatório encontrado</p><p className="text-sm text-muted-foreground">Ajuste os filtros ou selecione cursos para gerar um relatório.</p></div>}
    {jobs.data && jobs.data.data.length > 0 && <div className="overflow-x-auto rounded-lg border bg-card"><table className="w-full text-left text-sm"><thead className="bg-muted"><tr>{['Relatório', 'Escopo / conexão', 'Solicitado em', 'Estado', 'Arquivo / progresso', 'Ações'].map((label) => <th key={label} className="whitespace-nowrap px-4 py-3 font-medium">{label}</th>)}</tr></thead><tbody>{jobs.data.data.map((job) => {
      const terminal = job.status === 'completed' || job.status === 'failed';
      const incomplete = job.resultMetadata?.partial || job.resultMetadata?.coverage.some((family) => !family.complete);
      const connectionAlias = connections.data?.data.find((connection) => connection.connectionRef === job.connectionRef)?.alias;
      return <tr key={job.id} className="border-t align-top"><td className="min-w-56 px-4 py-4"><p className="font-medium">{title(job)}</p>{job.templateRevisionSnapshot && <p className="text-xs text-muted-foreground">Revisão {job.templateRevisionSnapshot} capturada</p>}{job.status === 'completed' && incomplete && <p className="mt-2 font-medium text-amber-700">{incompleteMessage(job)}</p>}{job.status === 'failed' && <p className="mt-2 text-destructive">{failureMessage(job.errorMessage)}</p>}</td>
        <td className="min-w-40 px-4 py-4"><p>{job.totalCourses} cursos / UCs</p><p className="text-xs text-muted-foreground">{connectionAlias || job.connectionRef}</p>{job.scopeType === 'category' && <p className="text-xs">{job.categoryPath}</p>}</td>
        <td className="whitespace-nowrap px-4 py-4">{formatDate(job.requestedAt)}</td><td className="px-4 py-4"><Badge variant={job.status === 'failed' ? 'destructive' : job.status === 'completed' ? 'default' : 'outline'}>{statusLabels[job.status]}</Badge></td>
        <td className="min-w-40 px-4 py-4">{job.status === 'completed' && job.downloadUrl ? <><Button asChild size="sm" variant="outline"><a href={job.downloadUrl} download><Download />{job.contentType === 'application/zip' ? 'Baixar ZIP' : 'Baixar arquivo'}</a></Button><p className="mt-1 text-xs text-muted-foreground">{formatBytes(job.fileSizeBytes)}</p></> : job.status === 'running' && job.reportType === 'template' ? <p className="flex items-center gap-2 text-sm" role="status"><LoaderCircle className="size-4 animate-spin" aria-hidden="true" />Gerando arquivo…</p> : job.status === 'running' ? <><Progress value={job.progressPercent} aria-label={`Progresso de ${title(job)}`} /><p className="mt-1 text-xs">{job.progressPercent}% · {job.processedCourses}/{job.totalCourses} cursos</p></> : '—'}</td>
        <td className="px-4 py-4"><DropdownMenu><DropdownMenuTrigger asChild><Button variant="ghost" size="icon" aria-label={`Ações de ${title(job)}`}><MoreHorizontal /></Button></DropdownMenuTrigger><DropdownMenuContent align="end">{terminal && (job.reportType === 'grades' || reportTemplatesEnabled && job.reportType === 'template') && <DropdownMenuItem disabled={refresh.isPending} onSelect={() => refresh.mutate(job.id)}>Atualizar dados</DropdownMenuItem>}<DropdownMenuItem disabled={!terminal || remove.isPending} onSelect={() => { remove.reset(); setDeleting(job); }}>Excluir</DropdownMenuItem></DropdownMenuContent></DropdownMenu></td></tr>;
    })}</tbody></table></div>}
    {jobs.data && <nav className="flex items-center justify-between gap-3" aria-label="Paginação do histórico"><span className="text-sm text-muted-foreground">Página {criteria.page} · {jobs.data.meta.total ?? jobs.data.meta.returned} relatórios</span><div className="flex gap-2"><Button variant="outline" disabled={criteria.page === 1 || jobs.isFetching} onClick={() => setCriteria((current) => ({ ...current, page: current.page - 1 }))}>Anterior</Button><Button variant="outline" disabled={!jobs.data.meta.hasMore || jobs.isFetching} onClick={() => setCriteria((current) => ({ ...current, page: current.page + 1 }))}>Próxima</Button></div></nav>}
    <Dialog open={Boolean(deleting)} onOpenChange={(open) => { if (!open && !remove.isPending) setDeleting(undefined); }}><DialogContent><DialogHeader><DialogTitle>Excluir relatório?</DialogTitle><DialogDescription>O arquivo e a solicitação serão removidos do histórico, liberando espaço de armazenamento.</DialogDescription></DialogHeader>{remove.isError && <p role="alert">Não foi possível excluir. O relatório pode ter mudado ou já ter sido removido.</p>}<DialogFooter><Button variant="outline" disabled={remove.isPending} onClick={() => setDeleting(undefined)}>Cancelar</Button><Button variant="destructive" disabled={remove.isPending} onClick={() => deleting && remove.mutate(deleting.id)}>Excluir relatório</Button></DialogFooter></DialogContent></Dialog>
  </main>;
}
