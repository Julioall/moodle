import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EditModeProvider } from '../components/layout/edit-mode';
import { MyCoursesPage } from '../features/courses/MyCoursesPage';
import { coursesGateway, type ListResponse } from '../features/courses/courses-gateway';

vi.mock('../features/connections/useConnectionScope', () => ({
  useConnectionScope: () => ({ connectionRef: 'fieg', selectedConnection: { connectionId: 'connection-fieg' }, connections: { isPending: false } }),
}));

vi.mock('../features/auth/useSession', () => ({
  useSession: () => ({ user: { id: 'synthetic-owner' }, can: () => false, reportTemplatesEnabled: false }),
}));

vi.mock('../features/courses/course-tracking', () => ({
  useTrackedCourses: () => ({ trackedCourseIds: new Set(['one', 'two']), isLoading: false }),
}));

vi.mock('../features/courses/course-visibility', () => ({
  useIgnoredCourses: () => ({ ignoredCourseIds: new Set(), ignoreCourse: vi.fn(), ignoreCourses: vi.fn() }),
}));

vi.mock('../features/courses/courses-gateway', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../features/courses/courses-gateway')>();
  return { ...actual, coursesGateway: { ...actual.coursesGateway, listAll: vi.fn() } };
});

const page = (pageNumber: number, courseId: string): ListResponse<{ courseId: string; connectionRef: string; fullName: string; categoryName: string }> => ({
  data: [{ courseId, connectionRef: 'fieg', fullName: `Curso ${courseId}`, categoryName: 'Categoria FIEG' }],
  meta: {
    page: pageNumber,
    pageSize: 100,
    returned: 1,
    total: 2,
    hasMore: pageNumber < 2,
    generatedAt: '2026-09-24T12:00:00Z',
    snapshotAt: '2026-09-24T12:00:00Z',
  },
});

describe('MyCoursesPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => new Response(JSON.stringify({ courseDetailsOverviewV1Enabled: false }), { status: 200 })));
  });
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('renders the first catalog page while later pages are still loading', async () => {
    let publishPage: ((response: ListResponse<never>) => void) | undefined;
    let finishLoad: ((response: ListResponse<never>) => void) | undefined;
    vi.mocked(coursesGateway.listAll).mockImplementation((_connectionRef, _pageSize, options) => {
      publishPage = options?.onPage as typeof publishPage;
      return new Promise((resolve) => { finishLoad = resolve; });
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EditModeProvider>
          <MemoryRouter><MyCoursesPage /></MemoryRouter>
        </EditModeProvider>
      </QueryClientProvider>,
    );

    await waitFor(() => expect(publishPage).toBeDefined());
    act(() => publishPage?.(page(1, 'one') as ListResponse<never>));

    expect(await screen.findByText('1 curso')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('Atualizando categorias e cursos do Moodle');

    await act(async () => {
      publishPage?.(page(2, 'two') as ListResponse<never>);
      finishLoad?.({
        data: [...page(1, 'one').data, ...page(2, 'two').data],
        meta: { ...page(1, 'one').meta, returned: 2, hasMore: false, complete: true },
      } as ListResponse<never>);
    });

    await waitFor(() => expect(screen.getByText('2 cursos')).toBeInTheDocument());
  });

  it('exposes course details from the catalog only when the server feature flag is enabled', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => new Response(JSON.stringify({ courseDetailsOverviewV1Enabled: true }), { status: 200 })));
    vi.mocked(coursesGateway.listAll).mockResolvedValue({
      ...page(1, 'one'),
      meta: { ...page(1, 'one').meta, total: 1, hasMore: false, complete: true },
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EditModeProvider>
          <MemoryRouter><MyCoursesPage /></MemoryRouter>
        </EditModeProvider>
      </QueryClientProvider>,
    );

    await screen.findByText('1 curso');
    await screen.findByRole('button', { name: /Categoria FIEG/ }).then((button) => button.click());

    expect(await screen.findByRole('link', { name: 'Abrir detalhes de Curso one' })).toHaveAttribute('href', '/cursos/fieg/one');
  });

  it('keeps course cards unlinked when the server feature flag is disabled', async () => {
    vi.mocked(coursesGateway.listAll).mockResolvedValue({
      ...page(1, 'one'),
      meta: { ...page(1, 'one').meta, total: 1, hasMore: false, complete: true },
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <QueryClientProvider client={queryClient}>
        <EditModeProvider>
          <MemoryRouter><MyCoursesPage /></MemoryRouter>
        </EditModeProvider>
      </QueryClientProvider>,
    );

    await screen.findByText('1 curso');
    await screen.findByRole('button', { name: /Categoria FIEG/ }).then((button) => button.click());

    expect(screen.queryByRole('link', { name: 'Abrir detalhes de Curso one' })).not.toBeInTheDocument();
  });
});
