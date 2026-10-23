import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EditModeProvider } from '../components/layout/edit-mode';
import { SchoolsPage } from '../features/schools/SchoolsPage';
import { reportSelectionStore } from '../features/reports/selection/report-selection';

const mocks = vi.hoisted(() => ({ connectionId: 'synthetic-id', hierarchy: vi.fn(), byCategory: vi.fn(), listAllByCategory: vi.fn(), listAll: vi.fn(), search: vi.fn() }));
vi.mock('../features/auth/useSession', () => ({ useSession: () => ({ user: { id: 'synthetic-owner' }, can: () => true, reportTemplatesEnabled: true }) }));
vi.mock('../features/connections/useConnectionScope', () => ({ useConnectionScope: () => ({ connectionRef: 'synthetic', selectedConnection: { connectionId: mocks.connectionId }, connections: { isPending: false } }) }));
vi.mock('../features/courses/courses-gateway', () => ({ coursesGateway: mocks }));
vi.mock('../features/courses/course-tracking', () => ({ useTrackedCourses: () => ({ trackedCourseIds: new Set(), trackCourse: vi.fn(), untrackCourse: vi.fn() }) }));
vi.mock('../features/courses/course-visibility', () => ({ useIgnoredCourses: () => ({ ignoredCourseIds: new Set(), restoreCourse: vi.fn() }) }));
function page(number: number, ids: string[], hasMore = false) {
  return { data: ids.map((courseId) => ({ courseId, connectionRef: 'synthetic', fullName: `UC ${courseId}`, categoryName: 'Root > Catálogo livre' })), meta: { page: number, pageSize: 100, returned: ids.length, hasMore, complete: true, generatedAt: '2026-10-02T00:00:00Z' } };
}
function GenerationDestination() {
  const location = useLocation();
  return <p>Return path: {(location.state as { returnTo?: string } | null)?.returnTo ?? 'missing'}</p>;
}
function RouteHarness() {
  return <Routes><Route path="/escolas" element={<SchoolsPage />} /><Route path="/relatorios/gerar" element={<GenerationDestination />} /></Routes>;
}
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><EditModeProvider><MemoryRouter initialEntries={['/escolas?connectionRef=synthetic']}><RouteHarness /></MemoryRouter></EditModeProvider></QueryClientProvider>);
}
beforeEach(() => {
  vi.clearAllMocks(); reportSelectionStore.cancel(); mocks.connectionId = 'synthetic-id';
  mocks.hierarchy.mockResolvedValue({ data: [{ path: 'Root > Catálogo livre', name: 'Catálogo livre', level: 1, courseCount: 2 }], meta: { complete: true } });
  mocks.listAllByCategory.mockResolvedValue(page(1, ['1', '2']));
});
afterEach(() => { cleanup(); reportSelectionStore.cancel(); });

describe('seleção nas árvores do portal', () => {
  it('selects only courses matching the status filter from a collapsed category', async () => {
    const courses = page(1, ['1', '2']);
    mocks.listAll.mockResolvedValue({ ...courses, data: courses.data.map((course, index) => ({ ...course, startDate: '2020-01-01T00:00:00Z', endDate: index === 0 ? '2099-01-01T00:00:00Z' : '2021-01-01T00:00:00Z' })) });
    mocks.byCategory.mockResolvedValue(courses);
    const user = userEvent.setup(); mount();
    await user.click(screen.getByRole('button', { name: 'Em andamento' }));
    await screen.findByText('1 curso');
    await user.click(screen.getByRole('button', { name: 'Gerar relatório' }));
    await user.click(screen.getByRole('checkbox', { name: 'Selecionar Root > Catálogo livre para relatório' }));
    await waitFor(() => expect([...reportSelectionStore.getSnapshot().ids]).toEqual(['1']));
    expect(mocks.byCategory).not.toHaveBeenCalled();
    await user.click(screen.getByRole('checkbox', { name: 'Selecionar Root > Catálogo livre para relatório' }));
    expect(reportSelectionStore.getSnapshot().ids.size).toBe(0);
  });
  it('blocks category selection when the filtered catalog is incomplete', async () => {
    const courses = page(1, ['1']);
    mocks.listAll.mockResolvedValue({ ...courses, meta: { ...courses.meta, complete: false } });
    const user = userEvent.setup(); mount();
    await user.click(screen.getByRole('button', { name: 'Em andamento' }));
    await screen.findByText('1 curso');
    await user.click(screen.getByRole('button', { name: 'Gerar relatório' }));
    expect(screen.getByRole('checkbox', { name: 'Selecionar Root > Catálogo livre para relatório' })).toBeDisabled();
    expect(reportSelectionStore.getSnapshot().ids.size).toBe(0);
  });
  it('loads collapsed canonical category pages and disables advance until the last page', async () => {
    let finish!: (value: ReturnType<typeof page>) => void;
    mocks.byCategory.mockImplementation(async (_path: string, _connection: string, number: number) => number === 1 ? page(1, ['1'], true) : new Promise((resolve) => { finish = resolve; }));
    const user = userEvent.setup(); mount();
    await user.click(await screen.findByRole('button', { name: 'Gerar relatório' }));
    await user.click(await screen.findByRole('checkbox', { name: 'Selecionar Root > Catálogo livre para relatório' }));
    await waitFor(() => expect(mocks.byCategory).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('button', { name: 'Avançar' })).toBeDisabled();
    expect(screen.getByRole('checkbox')).toHaveAttribute('aria-checked', 'mixed');
    finish(page(2, ['2']));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Avançar' })).toBeEnabled());
    expect(screen.getByText('2 cursos / UCs selecionados')).toBeInTheDocument();
    expect(mocks.byCategory.mock.calls[0].slice(0, 3)).toEqual(['Root > Catálogo livre', 'synthetic', 1]);
    expect(mocks.listAllByCategory).not.toHaveBeenCalled();
  });
  it('preserves confirmed IDs after a page error and resolves the same selection on retry', async () => {
    let failed = true;
    mocks.byCategory.mockImplementation(async (_path: string, _connection: string, number: number) => {
      if (number === 2 && failed) throw new Error('Falha sintética de paginação');
      return page(number, [String(number)], number === 1);
    });
    const user = userEvent.setup(); mount();
    await user.click(await screen.findByRole('button', { name: 'Gerar relatório' }));
    await user.click(await screen.findByRole('checkbox'));
    await screen.findByText('Falha sintética de paginação');
    expect(screen.getByText('1 cursos / UCs selecionados')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Avançar' })).toBeDisabled();
    failed = false;
    await user.click(screen.getByRole('button', { name: 'Tentar novamente' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Avançar' })).toBeEnabled());
    expect(screen.getByRole('checkbox')).toHaveAttribute('aria-checked', 'true');
  });
  it('cancels selection explicitly and resets on a replaced connection with the same alias', async () => {
    mocks.byCategory.mockResolvedValue(page(1, ['1', '2']));
    const user = userEvent.setup(); const view = mount();
    await user.click(await screen.findByRole('button', { name: 'Gerar relatório' }));
    await user.click(await screen.findByRole('checkbox'));
    await screen.findByText('2 cursos / UCs selecionados');
    mocks.connectionId = 'synthetic-replacement';
    view.rerender(<QueryClientProvider client={new QueryClient()}><EditModeProvider><MemoryRouter initialEntries={['/escolas?connectionRef=synthetic']}><RouteHarness /></MemoryRouter></EditModeProvider></QueryClientProvider>);
    await screen.findByText('A conexão mudou. Selecione novamente os cursos.');
    expect(reportSelectionStore.getSnapshot().ids.size).toBe(0);
    await user.click(screen.getByRole('button', { name: 'Gerar relatório' }));
    await user.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(screen.queryByRole('button', { name: 'Avançar' })).not.toBeInTheDocument();
  });
  it('opens the generation route while preserving the selection page as its return path', async () => {
    mocks.byCategory.mockResolvedValue(page(1, ['1', '2']));
    const user = userEvent.setup();
    mount();
    await user.click(await screen.findByRole('button', { name: 'Gerar relatório' }));
    await user.click(await screen.findByRole('checkbox', { name: 'Selecionar Root > Catálogo livre para relatório' }));
    const advance = await screen.findByRole('button', { name: 'Avançar' });
    await waitFor(() => expect(advance).toBeEnabled());
    await user.click(advance);
    await screen.findByText('Return path: /escolas?connectionRef=synthetic');
    expect([...reportSelectionStore.getSnapshot().ids]).toEqual(['1', '2']);
  });
});
