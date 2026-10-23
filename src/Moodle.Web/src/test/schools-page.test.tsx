import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EditModeProvider } from '../components/layout/edit-mode';
import { SchoolsPage } from '../features/schools/SchoolsPage';
import { coursesGateway, type Course, type ListResponse } from '../features/courses/courses-gateway';

vi.mock('../features/connections/useConnectionScope', () => ({
  useConnectionScope: () => ({ connectionRef: 'fieg', selectedConnection: { connectionId: 'connection-fieg' }, connections: { isPending: false } }),
}));

vi.mock('../features/auth/useSession', () => ({
  useSession: () => ({ user: { id: 'synthetic-owner' }, can: () => false, reportTemplatesEnabled: false }),
}));

vi.mock('../features/courses/course-visibility', () => ({
  useIgnoredCourses: () => ({ ignoredCourseIds: new Set(), restoreCourse: vi.fn() }),
}));

vi.mock('../features/courses/course-tracking', () => ({
  useTrackedCourses: () => ({ trackedCourseIds: new Set(), trackCourse: vi.fn(), untrackCourse: vi.fn() }),
}));

vi.mock('../features/connections/connections-gateway', () => ({
  connectionsGateway: { list: vi.fn().mockResolvedValue({ data: [], meta: { generatedAt: '2026-09-14T00:00:00Z' } }) },
}));

vi.mock('../features/courses/courses-gateway', () => ({
  coursesGateway: {
    hierarchy: vi.fn(),
    listAllByCategory: vi.fn(),
    listAll: vi.fn(),
    search: vi.fn(),
  },
}));

const allCourses: Course[] = [
  { connectionRef: 'fieg', courseId: 'active', fullName: 'Curso em andamento', categoryName: 'Escola A', startDate: '2020-01-01T00:00:00Z', endDate: '2099-12-31T23:59:59Z' },
  { connectionRef: 'fieg', courseId: 'future', fullName: 'Curso não iniciado', categoryName: 'Escola B', startDate: '2099-01-01T00:00:00Z', endDate: '2099-12-31T23:59:59Z' },
  { connectionRef: 'fieg', courseId: 'finished', fullName: 'Curso finalizado', categoryName: 'Escola C', startDate: '2020-01-01T00:00:00Z', endDate: '2021-12-31T23:59:59Z' },
];

function hierarchy() {
  return {
    data: [
      { path: 'Escola A', name: 'Escola A', level: 0, courseCount: 1 },
      { path: 'Escola B', name: 'Escola B', level: 0, courseCount: 1 },
      { path: 'Escola C', name: 'Escola C', level: 0, courseCount: 1 },
    ],
    meta: { generatedAt: '2026-09-14T00:00:00Z', complete: true },
  };
}

function catalog(courses: Course[]): ListResponse<Course> {
  return {
    data: courses,
    meta: { page: 1, pageSize: 100, returned: courses.length, total: courses.length, hasMore: false, complete: true, generatedAt: '2026-09-14T00:00:00Z' },
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(coursesGateway.hierarchy).mockResolvedValue(hierarchy());
  vi.mocked(coursesGateway.listAll).mockResolvedValue(catalog(allCourses));
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ courseDetailsOverviewV1Enabled: false }), { status: 200 })));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderSchoolsPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <EditModeProvider>
        <MemoryRouter><SchoolsPage /></MemoryRouter>
      </EditModeProvider>
    </QueryClientProvider>,
  );
}

describe('SchoolsPage', () => {
  it('does not expose report generation controls', () => {
    renderSchoolsPage();

    expect(screen.queryByRole('button', { name: /Gerar relatório/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Cancelar seleção/i })).not.toBeInTheDocument();
  });

  it('filters the complete schools catalog by lifecycle status and restores all schools', async () => {
    renderSchoolsPage();

    expect(await screen.findByText('Escola A')).toBeInTheDocument();
    expect(screen.getByText('Escola B')).toBeInTheDocument();
    expect(screen.getByText('Escola C')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Em andamento' }));
    await waitFor(() => expect(screen.getByText('Escola A')).toBeInTheDocument());
    expect(screen.queryByText('Escola B')).not.toBeInTheDocument();
    expect(screen.queryByText('Escola C')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Todos' }));
    expect(screen.getByText('Escola B')).toBeInTheDocument();
    expect(screen.getByText('Escola C')).toBeInTheDocument();
    expect(coursesGateway.listAll).toHaveBeenCalledWith('fieg', 100, expect.objectContaining({ signal: expect.any(AbortSignal) }));
  });

  it.each([
    ['Em andamento', 'Escola A', 'Escola B', 'Escola C'],
    ['Não iniciados', 'Escola B', 'Escola A', 'Escola C'],
    ['Finalizados', 'Escola C', 'Escola A', 'Escola B'],
  ])('shows only matching schools for %s', async (filter, matchingSchool, otherSchoolOne, otherSchoolTwo) => {
    renderSchoolsPage();

    fireEvent.click(screen.getByRole('button', { name: filter }));

    expect(await screen.findByText(matchingSchool)).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.queryByText(otherSchoolOne)).not.toBeInTheDocument();
      expect(screen.queryByText(otherSchoolTwo)).not.toBeInTheDocument();
    });
  });

  it('finishes local search under a status filter and displays an empty result', async () => {
    renderSchoolsPage();
    fireEvent.click(screen.getByRole('button', { name: 'Em andamento' }));
    await screen.findByText('Escola A');
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'inexistente' } });
    expect(await screen.findByText('Nenhum curso corresponde ao filtro selecionado')).toBeInTheDocument();
    expect(screen.queryByText('Preparando cursos e categorias do Moodle…')).not.toBeInTheDocument();
    expect(coursesGateway.search).not.toHaveBeenCalled();
  });

  it('opens filtered course cards from the loaded catalog without another category request', async () => {
    renderSchoolsPage();
    fireEvent.click(screen.getByRole('button', { name: 'Em andamento' }));
    const school = await screen.findByText('Escola A');
    const details = school.closest('details')!;
    details.open = true;
    fireEvent(details, new Event('toggle'));
    expect(await screen.findByRole('heading', { name: 'Curso em andamento' })).toBeInTheDocument();
    expect(coursesGateway.listAllByCategory).not.toHaveBeenCalled();
  });
});
