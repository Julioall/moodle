import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../features/courses/course-overview-gateway', () => ({ courseOverviewGateway: { get: vi.fn() } }));

import { CoursePanelPage } from '../features/courses/CoursePanelPage';
import { courseOverviewGateway, type CourseOverviewResponse } from '../features/courses/course-overview-gateway';

const overview: CourseOverviewResponse = {
  schemaVersion: 1,
  course: { courseId: '42', connectionRef: 'demo', name: 'Curso de demonstração', categoryName: 'TDS' },
  summary: { observedStudentCount: 2, totalStudentCount: null, studentCountState: 'partial', knownGradeCount: null, averagePercentage: null, averageDenominator: null, averageState: 'unavailable' },
  students: [
    { studentId: 'student-1', fullName: 'Ana Clara', profilePath: '/cursos/demo/42/alunos/student-1', lastCourseAccessAt: null, lastAccessState: 'unknown', grade: { value: null, max: null, percentage: null, state: 'unavailable' } },
    { studentId: 'student-2', fullName: 'Bruno Oliveira', profilePath: '/cursos/demo/42/alunos/student-2', lastCourseAccessAt: '2026-09-28T14:32:00Z', lastAccessState: 'known', grade: { value: 8.5, max: 10, percentage: 85, state: 'covered' } },
  ],
  pagination: { page: 1, pageSize: 10, filteredTotal: 2, hasMore: false, populationComplete: false },
  coverage: { course: { state: 'complete', reasons: [] }, students: { state: 'partial', reasons: ['partial_roster'] }, gradebook: { state: 'unavailable', reasons: ['missing_snapshot'] } },
  freshness: {
    course: { state: 'fresh' },
    students: { state: 'fresh' },
    gradebook: { state: 'unavailable' },
  },
  refresh: { students: { state: 'not_needed', reason: 'snapshot_current' }, gradebook: { state: 'not_requested', reason: 'snapshot_only_policy' } },
};

describe('CoursePanelPage', () => {
  afterEach(cleanup);

  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(courseOverviewGateway.get).mockResolvedValue(overview);
  });

  function renderPage(initialEntry = '/cursos/demo/42') {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const rendered = render(<QueryClientProvider client={queryClient}><MemoryRouter initialEntries={[initialEntry]}><Routes>
      <Route path="/cursos/:connectionRef/:courseId" element={<CoursePanelPage />} />
      <Route path="/cursos/:connectionRef/:courseId/alunos/:studentId" element={<h1>Perfil existente</h1>} />
    </Routes></MemoryRouter></QueryClientProvider>);
    return { queryClient, ...rendered };
  }

  it('shows a skeleton and an announced message while initial details are loading', async () => {
    let resolveOverview!: (value: CourseOverviewResponse) => void;
    vi.mocked(courseOverviewGateway.get).mockReturnValue(new Promise((resolve) => { resolveOverview = resolve; }));
    renderPage();

    expect(screen.getByRole('main')).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('status')).toHaveTextContent('Carregando dados da turma...');

    resolveOverview(overview);
    expect(await screen.findByRole('heading', { name: 'Curso de demonstração' })).toBeInTheDocument();
  });

  it('announces a background refresh while keeping the current snapshot visible', async () => {
    let resolveRefresh!: (value: CourseOverviewResponse) => void;
    vi.mocked(courseOverviewGateway.get)
      .mockResolvedValueOnce(overview)
      .mockImplementationOnce(() => new Promise((resolve) => { resolveRefresh = resolve; }));
    const { queryClient } = renderPage();
    await screen.findByRole('heading', { name: 'Curso de demonstração' });

    const refresh = queryClient.refetchQueries({ queryKey: ['app', 'course-overview', 'demo', '42'] });
    expect(await screen.findByText('Atualizando os dados exibidos...')).toBeInTheDocument();
    expect(screen.getByText('Ana Clara')).toBeInTheDocument();

    resolveRefresh(overview);
    await refresh;
  });

  it('shows the latest saved snapshot time for students and grades', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      freshness: {
        ...overview.freshness,
        students: { state: 'fresh', updatedAt: '2026-10-03T13:46:00Z' },
        gradebook: { state: 'fresh', updatedAt: '2026-10-03T14:24:00Z' },
      },
      summary: { ...overview.summary, averageState: 'partial' },
      coverage: { ...overview.coverage, gradebook: { state: 'partial', reasons: ['partial_gradebook'] } },
    });
    renderPage();

    expect(await screen.findByText(/Alunos: 03\/10\/2026/)).toBeInTheDocument();
    expect(screen.getByText(/Notas: 03\/10\/2026/)).toBeInTheDocument();
  });

  it('shows a raw-scale class average without adding a percent sign', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      summary: {
        ...overview.summary,
        knownGradeCount: 2,
        averagePercentage: null,
        averageGradeValue: 7,
        averageDenominator: 2,
        averageState: 'complete',
      },
      coverage: { ...overview.coverage, gradebook: { state: 'complete', reasons: [] } },
    });
    renderPage();

    expect(await screen.findByText('Escala original · 2 notas')).toBeInTheDocument();
    expect(screen.getByText('7')).toBeInTheDocument();
  });

  it('explains when an update has not started and identifies the snapshot still in use', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      freshness: {
        ...overview.freshness,
        gradebook: { state: 'stale', updatedAt: '2026-10-03T14:24:00Z' },
      },
      summary: { ...overview.summary, averageState: 'partial' },
      coverage: { ...overview.coverage, gradebook: { state: 'partial', reasons: ['partial_gradebook'] } },
      refresh: { ...overview.refresh, gradebook: { state: 'deferred', reason: 'refresh_not_queued' } },
    });
    renderPage();

    expect(await screen.findByText(/Notas: sem nova sincronização/)).toBeInTheDocument();
    expect(screen.getByText(/dados de 03\/10\/2026/)).toBeInTheDocument();
  });

  it('renders the reduced roster and represents missing course access as unavailable', async () => {
    renderPage();

    await screen.findByRole('heading', { name: 'Curso de demonstração' });
    expect(screen.getByText('Ana Clara')).toHaveAttribute('href', '/cursos/demo/42/alunos/student-1');
    expect(screen.getByText('Sem dado')).toBeInTheDocument();
    expect(screen.getByText('Notas ainda não disponíveis.')).toBeInTheDocument();
    expect(screen.queryByText('Pendentes de envio')).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('navigates from a student name to the existing profile route', async () => {
    renderPage();
    const user = userEvent.setup();

    await user.click(await screen.findByRole('link', { name: 'Ana Clara' }));
    expect(await screen.findByRole('heading', { name: 'Perfil existente' })).toBeInTheDocument();
  });

  it('hides grade and average surfaces when the account is not authorized', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      summary: { ...overview.summary, averageState: 'not_authorized' },
      coverage: { ...overview.coverage, gradebook: { state: 'not_authorized', reasons: [] } },
      students: overview.students.map((student) => ({ ...student, grade: null })),
    });
    renderPage();

    await screen.findByRole('heading', { name: 'Curso de demonstração' });
    expect(screen.queryByText('Média da turma')).not.toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Nota total' })).not.toBeInTheDocument();
  });

  it('shows unavailable instead of zero when the student snapshot is missing', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      summary: { ...overview.summary, observedStudentCount: null, totalStudentCount: null, studentCountState: 'unavailable' },
      students: [],
      pagination: { ...overview.pagination, filteredTotal: 0 },
      coverage: { ...overview.coverage, students: { state: 'unavailable', reasons: ['missing_snapshot'] } },
      freshness: { ...overview.freshness, students: { state: 'unavailable' } },
    });
    renderPage();

    await screen.findByRole('heading', { name: 'Curso de demonstração' });
    expect(screen.getByText('Indisponível')).toBeInTheDocument();
    expect(screen.queryByText('0 alunos observados')).not.toBeInTheDocument();
    expect(screen.getByText('A lista de alunos ainda não está disponível.')).toBeInTheDocument();
  });

  it('shows stale and scale-limited student coverage', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValue({
      ...overview,
      summary: { ...overview.summary, observedStudentCount: 500, studentCountState: 'partial' },
      coverage: { ...overview.coverage, students: { state: 'partial', reasons: ['scale_limit'] } },
      freshness: { ...overview.freshness, students: { state: 'stale', updatedAt: '2026-09-28T14:32:00Z' } },
    });
    renderPage();

    expect(await screen.findByText(/Alunos: dados desatualizados/)).toBeInTheDocument();
    expect(screen.getByText(/Lista limitada a 500 alunos observados/)).toBeInTheDocument();
  });

  it('polls while snapshots are queued and stops after the updated data arrives', async () => {
    vi.mocked(courseOverviewGateway.get).mockResolvedValueOnce({
      ...overview,
      summary: { ...overview.summary, observedStudentCount: null, totalStudentCount: null, studentCountState: 'unavailable' },
      students: [],
      pagination: { ...overview.pagination, filteredTotal: 0 },
      coverage: {
        ...overview.coverage,
        students: { state: 'unavailable', reasons: ['missing_snapshot'] },
        gradebook: { state: 'unavailable', reasons: ['missing_snapshot'] },
      },
      freshness: {
        ...overview.freshness,
        students: { state: 'unavailable' },
        gradebook: { state: 'unavailable' },
      },
      refresh: {
        ...overview.refresh,
        students: { state: 'queued', reason: 'snapshot_missing_or_stale', retryAfterSeconds: 1 },
        gradebook: { state: 'queued', reason: 'snapshot_missing_stale_or_roster_changed', retryAfterSeconds: 1 },
      },
    }).mockResolvedValueOnce(overview);
    const { container } = renderPage();

    expect(await screen.findByText(/Alunos: carregando primeiros dados/)).toBeInTheDocument();
    expect(screen.getByText(/Notas: carregando primeiros dados/)).toBeInTheDocument();
    expect(screen.getByText('Carregando matrículas...')).toBeInTheDocument();
    expect(screen.getByText('Carregando notas...')).toBeInTheDocument();
    expect(screen.getByText('Carregando alunos...')).toBeInTheDocument();
    expect(container.querySelectorAll('.animate-pulse').length).toBeGreaterThan(0);
    expect(screen.queryByText('A lista de alunos ainda não está disponível.')).not.toBeInTheDocument();
    await waitFor(() => expect(courseOverviewGateway.get).toHaveBeenCalledTimes(2), { timeout: 3_000, interval: 50 });
    expect(await screen.findByText('Ana Clara')).toBeInTheDocument();
    await new Promise((resolve) => window.setTimeout(resolve, 1_200));
    expect(courseOverviewGateway.get).toHaveBeenCalledTimes(2);
  });

  it('sends name search and sort through the aggregate endpoint contract', async () => {
    renderPage();
    const user = userEvent.setup();

    await user.type(await screen.findByRole('textbox', { name: 'Buscar aluno pelo nome' }), 'Ana');
    await waitFor(() => expect(courseOverviewGateway.get).toHaveBeenLastCalledWith('demo', '42', expect.objectContaining({ query: 'Ana', page: 1, pageSize: 10, sort: 'name_asc' })));
    await user.click(await screen.findByRole('button', { name: /Aluno/ }));
    await waitFor(() => expect(courseOverviewGateway.get).toHaveBeenLastCalledWith('demo', '42', expect.objectContaining({ query: 'Ana', sort: 'name_desc' })));
  });

  it('changes local page size and sends the next page to the same aggregate endpoint', async () => {
    vi.mocked(courseOverviewGateway.get).mockImplementation(async (_connection, _course, options) => ({
      ...overview,
      pagination: { ...overview.pagination, page: options.page, pageSize: options.pageSize, hasMore: options.page === 1 },
    }));
    renderPage();
    const user = userEvent.setup();

    await user.selectOptions(await screen.findByLabelText('Por página'), '25');
    await waitFor(() => expect(courseOverviewGateway.get).toHaveBeenLastCalledWith('demo', '42', expect.objectContaining({ page: 1, pageSize: 25 })));
    await user.click(screen.getByRole('button', { name: 'Próxima' }));
    await waitFor(() => expect(courseOverviewGateway.get).toHaveBeenLastCalledWith('demo', '42', expect.objectContaining({ page: 2, pageSize: 25 })));
  });
});
