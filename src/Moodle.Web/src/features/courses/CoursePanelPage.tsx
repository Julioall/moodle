import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ArrowDownAZ, ArrowLeft, Search, Users } from 'lucide-react';
import { Link, useParams } from 'react-router-dom';

import { Button } from '../../components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { courseOverviewGateway } from './course-overview-gateway';
import { AppHttpError, retryAwarePollingInterval } from '../../integrations/http/api-client';

function formatDate(value?: string | null) {
  if (!value) return 'Sem dado';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Sem dado' : new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short' }).format(date);
}

function formatDateTime(value?: string | null) {
  if (!value) return 'sem data conhecida';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'sem data conhecida' : new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' }).format(date);
}

function formatGrade(value: number, max?: number | null) {
  const grade = new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 }).format(value);
  return max == null ? grade : `${grade} de ${new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 }).format(max)}`;
}

function snapshotStatus(
  label: string,
  freshness: { state: string; updatedAt?: string | null },
  refresh: { state: string },
) {
  const lastUpdated = freshness.updatedAt ? formatDateTime(freshness.updatedAt) : null;

  if (refresh.state === 'queued') {
    return `${label}: ${lastUpdated ? `atualizando · dados de ${lastUpdated}` : 'carregando primeiros dados'}`;
  }
  if (refresh.state === 'deferred') {
    return `${label}: ${lastUpdated ? `sem nova sincronização · dados de ${lastUpdated}` : 'sem dados salvos'}`;
  }
  if (['stale', 'expired'].includes(freshness.state)) {
    return `${label}: dados desatualizados · ${lastUpdated ?? 'sem data conhecida'}`;
  }
  if (freshness.state === 'fresh' && lastUpdated) {
    return `${label}: ${lastUpdated}`;
  }
  return null;
}

function StudentsTableSkeleton({ gradesVisible }: { gradesVisible: boolean }) {
  return (
    <div className="overflow-x-auto border-y" aria-hidden="true">
      <div className="min-w-[640px] divide-y">
        {Array.from({ length: 5 }, (_, index) => <div key={index} className={`grid ${gradesVisible ? 'grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,0.8fr)]' : 'grid-cols-[minmax(0,2fr)_minmax(0,1fr)]'} items-center gap-4 py-3`}>
          <div className="flex items-center gap-3"><Skeleton className="h-8 w-8 rounded-full" /><Skeleton className="h-4 w-2/3" /></div>
          <Skeleton className="h-4 w-24" />
          {gradesVisible && <Skeleton className="ml-auto h-4 w-12" />}
        </div>)}
      </div>
    </div>
  );
}

export function CoursePanelPage() {
  const { connectionRef = '', courseId = '' } = useParams();
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(10);
  const [sort, setSort] = useState<'name_asc' | 'name_desc'>('name_asc');

  useEffect(() => {
    setSearch('');
    setQuery('');
    setPage(1);
    setPageSize(10);
    setSort('name_asc');
  }, [connectionRef, courseId]);

  useEffect(() => {
    const timeout = window.setTimeout(() => {
      setQuery(search.trim());
      setPage(1);
    }, 250);
    return () => window.clearTimeout(timeout);
  }, [search]);

  const overview = useQuery({
    queryKey: ['app', 'course-overview', connectionRef, courseId, query, page, pageSize, sort],
    queryFn: ({ signal }) => courseOverviewGateway.get(connectionRef, courseId, { query, page, pageSize, sort, signal }),
    enabled: Boolean(connectionRef && courseId),
    retry: false,
    staleTime: 2_000,
    refetchInterval: (query) => {
      if (query.state.error instanceof AppHttpError && query.state.error.origin === 'connector') {
        return retryAwarePollingInterval(query.state.error, 15_000);
      }
      const pending = Object.values(query.state.data?.refresh ?? {}).filter((item) => item.state === 'queued');
      if (pending.length === 0) return false;
      const retryAfterSeconds = Math.min(...pending.map((item) => item.retryAfterSeconds ?? 5));
      return Math.min(15_000, Math.max(1_000, retryAfterSeconds * 1_000));
    },
  });

  const response = overview.data;
  const averageIsPercentage = response?.summary.averagePercentage != null;
  const average = response?.summary.averagePercentage ?? response?.summary.averageGradeValue;
  const averageVisible = response?.summary.averageState !== 'not_authorized';
  const gradesVisible = response?.coverage.gradebook.state !== 'not_authorized';

  if (overview.isPending) {
    return <main className="content-frame space-y-6" aria-busy="true"><p role="status" className="sr-only">Carregando dados da turma...</p><Skeleton className="h-10 w-2/3" /><div className="grid gap-4 md:grid-cols-2"><Skeleton className="h-24" /><Skeleton className="h-24" /></div><Skeleton className="h-80" /></main>;
  }

  if (overview.isError || !response) {
    return <main className="content-frame" aria-labelledby="course-error-title"><h1 id="course-error-title" className="text-xl font-semibold">Não foi possível carregar esta turma</h1><p className="mt-2 text-sm text-muted-foreground">A turma pode estar indisponível para esta conexão ou sua sessão precisa ser atualizada.</p><Button asChild variant="outline" className="mt-4"><Link to={`/meus-cursos?connectionRef=${encodeURIComponent(connectionRef)}`}><ArrowLeft className="h-4 w-4" />Meus cursos</Link></Button></main>;
  }

  const studentsPending = response.summary.studentCountState === 'unavailable' && response.refresh.students.state === 'queued';
  const gradebookPending = gradesVisible && response.coverage.gradebook.state === 'unavailable' && response.refresh.gradebook.state === 'queued';
  const countLabel = studentsPending
    ? 'Carregando...'
    : response.summary.observedStudentCount == null
    ? 'Indisponível'
    : response.summary.totalStudentCount == null
      ? `${response.summary.observedStudentCount} alunos observados`
      : `${response.summary.totalStudentCount} alunos`;
  const scaleLimited = response.coverage.students.reasons.includes('scale_limit');
  const studentStatus = snapshotStatus('Alunos', response.freshness.students, response.refresh.students);
  const gradebookStatus = gradesVisible
    ? snapshotStatus('Notas', response.freshness.gradebook, response.refresh.gradebook)
    : null;

  return (
    <main className="content-frame space-y-6" aria-labelledby="course-title">
      <header className="flex flex-col gap-3">
        <Button asChild variant="ghost" size="sm" className="w-fit px-0"><Link to={`/meus-cursos?connectionRef=${encodeURIComponent(connectionRef)}`}><ArrowLeft className="h-4 w-4" />Meus cursos</Link></Button>
        <div>
          <p className="eyebrow">{response.course.categoryName ?? `Curso · ${connectionRef}`}</p>
          <h1 id="course-title" className="text-2xl font-bold">{response.course.name}</h1>
          <div className="mt-1 flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
            <p className="text-sm text-muted-foreground">{response.course.startAt ? `Início ${formatDate(response.course.startAt)}` : 'Início sem dado'}{response.course.endAt ? ` · Término ${formatDate(response.course.endAt)}` : ''}</p>
            <div aria-live="polite" aria-atomic="true" className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground sm:justify-end">
              {overview.isFetching && <div role="status" className="flex items-center gap-2"><span className="sr-only">Atualizando os dados exibidos...</span><Skeleton className="h-3 w-40" aria-hidden="true" /></div>}
              {studentStatus && <span role={response.refresh.students.state === 'queued' || response.refresh.students.state === 'deferred' ? 'status' : undefined}>{studentStatus}</span>}
              {studentStatus && gradebookStatus && <span aria-hidden="true">·</span>}
              {gradebookStatus && <span role={response.refresh.gradebook.state === 'queued' || response.refresh.gradebook.state === 'deferred' ? 'status' : undefined}>{gradebookStatus}</span>}
            </div>
          </div>
        </div>
        {response.freshness.course.state === 'stale' && <p className="text-sm text-muted-foreground">Os dados locais do curso podem estar desatualizados.</p>}
      </header>

      <section className="grid gap-4 md:grid-cols-2" aria-label="Resumo da turma">
        <Card>
          <CardHeader className="flex-row items-center gap-3 space-y-0 pb-2"><Users className="h-5 w-5 text-primary" /><div><CardTitle className="text-sm">Alunos cobertos</CardTitle><CardDescription>{studentsPending ? 'Carregando matrículas...' : response.summary.studentCountState === 'unavailable' ? 'Lista de alunos indisponível' : response.summary.studentCountState === 'partial' ? 'A lista pode estar incompleta' : 'Matrículas ativas observadas'}</CardDescription></div></CardHeader>
          <CardContent>{studentsPending ? <Skeleton className="h-8 w-24" aria-hidden="true" /> : <p className="tabular-nums text-2xl font-semibold">{countLabel}</p>}</CardContent>
        </Card>
        {averageVisible && <Card>
          <CardHeader className="pb-2"><CardTitle className="text-sm">Média da turma</CardTitle><CardDescription>{gradebookPending ? 'Carregando notas...' : response.summary.averageState === 'complete' ? `${averageIsPercentage ? 'Percentual' : 'Escala original'} · ${response.summary.averageDenominator} notas` : response.summary.averageState === 'partial' ? response.summary.averageDenominator ? `${averageIsPercentage ? 'Percentual' : 'Escala original'} · média de ${response.summary.averageDenominator} notas disponíveis` : 'Dados parciais; nenhuma nota disponível' : 'Notas indisponíveis'}</CardDescription></CardHeader>
          <CardContent>{gradebookPending ? <Skeleton className="h-8 w-24" aria-hidden="true" /> : <p className="tabular-nums text-2xl font-semibold">{average == null ? '—' : `${new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 1 }).format(average)}${averageIsPercentage ? '%' : ''}`}</p>}</CardContent>
        </Card>}
      </section>

      <section aria-labelledby="students-title" className="space-y-4">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <div><h2 id="students-title" className="text-lg font-semibold">Alunos</h2><p className="text-sm text-muted-foreground">{studentsPending ? 'Carregando alunos...' : `${response.pagination.filteredTotal} encontrados${!response.pagination.populationComplete ? ' na população coberta' : ''}`}</p></div>
          <label className="relative block w-full sm:max-w-xs"><span className="sr-only">Buscar aluno pelo nome</span><Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" /><Input className="pl-9" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Buscar aluno" /></label>
        </div>

        {scaleLimited && <p role="status" className="text-sm text-muted-foreground">Lista limitada a 500 alunos observados; busca, contagem e média cobrem somente essa população.</p>}
        {gradesVisible && response.coverage.gradebook.state === 'unavailable' && !gradebookPending && <p role="status" className="text-sm text-muted-foreground">Notas ainda não disponíveis.</p>}
        {response.students.length === 0 && response.summary.studentCountState === 'unavailable'
          ? studentsPending ? <StudentsTableSkeleton gradesVisible={gradesVisible} /> : <p className="py-8 text-sm text-muted-foreground">A lista de alunos ainda não está disponível.</p>
          : response.students.length === 0
            ? <p className="py-8 text-sm text-muted-foreground">Nenhum aluno encontrado.</p>
            : <div className="overflow-x-auto border-y">
              <table className="w-full min-w-[640px] border-collapse text-left text-sm">
                <thead><tr className="border-b text-xs text-muted-foreground"><th scope="col" aria-sort={sort === 'name_asc' ? 'ascending' : 'descending'} className="py-3 pr-4 font-medium"><button type="button" className="inline-flex items-center gap-1" aria-label={`Aluno, ordenação ${sort === 'name_asc' ? 'crescente' : 'decrescente'}; alternar`} onClick={() => { setSort((value) => value === 'name_asc' ? 'name_desc' : 'name_asc'); setPage(1); }}>Aluno <ArrowDownAZ aria-hidden="true" className="h-3.5 w-3.5" /></button></th><th scope="col" className="py-3 pr-4 font-medium">Último acesso ao curso</th>{gradesVisible && <th scope="col" className="py-3 text-right font-medium">Nota total</th>}</tr></thead>
                <tbody>{response.students.map((student) => <tr key={student.studentId} className="border-b last:border-0">
                  <td className="py-3 pr-4"><Link className="font-medium text-primary underline-offset-4 hover:underline" to={student.profilePath}>{student.fullName}</Link></td>
                  <td className="py-3 pr-4 text-muted-foreground">{formatDate(student.lastCourseAccessAt)}</td>
                  {gradesVisible && <td className="py-3 text-right tabular-nums">{gradebookPending ? <Skeleton className="ml-auto h-4 w-12" aria-hidden="true" /> : student.grade?.value == null ? '—' : formatGrade(student.grade.value, student.grade.max)}</td>}
                </tr>)}</tbody>
              </table>
            </div>}

        {response.pagination.filteredTotal > 0 && <nav aria-label="Paginação de alunos" className="flex flex-wrap items-center justify-between gap-3 border-t pt-4 text-sm">
          <span className="text-muted-foreground">Página {response.pagination.page} · {response.pagination.filteredTotal} alunos</span>
          <div className="flex items-center gap-2"><label className="text-muted-foreground" htmlFor="course-overview-page-size">Por página</label><select id="course-overview-page-size" className="h-9 rounded-md border bg-background px-2" value={pageSize} onChange={(event) => { setPageSize(Number(event.target.value)); setPage(1); }}><option value={10}>10</option><option value={25}>25</option><option value={50}>50</option></select><Button variant="outline" size="sm" onClick={() => setPage((value) => Math.max(1, value - 1))} disabled={page <= 1}>Anterior</Button><Button variant="outline" size="sm" onClick={() => setPage((value) => value + 1)} disabled={!response.pagination.hasMore}>Próxima</Button></div>
        </nav>}
      </section>
    </main>
  );
}
