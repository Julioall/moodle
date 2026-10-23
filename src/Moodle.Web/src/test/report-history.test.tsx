import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ReportsPage } from '../features/reports/ReportsPage';
import { reportSelectionStore } from '../features/reports/selection/report-selection';
import type { ReportJob } from '../features/reports/reports-gateway';

const mocks = vi.hoisted(() => ({ enabled: true, allowed: true, explicit: true, jobs: vi.fn(), getJob: vi.fn(), refreshJob: vi.fn(), deleteJob: vi.fn(), createJob: vi.fn(), selectConnection: vi.fn() }));
vi.mock('../features/auth/useSession', () => ({ useSession: () => ({ user: { id: 'synthetic-owner' }, session: { isPending: false }, can: () => mocks.allowed, reportTemplatesEnabled: mocks.enabled }) }));
vi.mock('../features/connections/useConnectionScope', () => ({ useConnectionScope: () => ({ connectionRef: 'synthetic', selectedConnection: mocks.explicit ? { connectionId: 'synthetic-id' } : undefined, selectConnection: mocks.selectConnection, connections: { data: { data: [{ connectionId: 'synthetic-id', connectionRef: 'synthetic', alias: 'Moodle sintético' }] } } }) }));
vi.mock('../features/reports/reports-gateway', () => ({ reportsGateway: mocks }));
const legacyJob: ReportJob = { id: 'legacy-job', reportType: 'grades', scopeType: 'courses', totalCourses: 2, connectionRef: 'synthetic', status: 'completed', progressPercent: 100, processedCourses: 2, fileSizeBytes: 2048, downloadUrl: '/api/reports/jobs/legacy-job/download', requestedAt: '2026-10-02T00:00:00Z', updatedAt: '2026-10-02T00:00:00Z' };
function response(data: ReportJob[] = [legacyJob], hasMore = false) { return { data, meta: { page: 1, pageSize: 20, total: data.length, hasMore, storageUsedBytes: 2048, storageLimitBytes: 314572800, storageAvailableBytes: 314570752 } }; }
function mount() { const client = new QueryClient({ defaultOptions: { queries: { retry: false } } }); return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={['/relatorios']}><Routes><Route path="/relatorios" element={<ReportsPage />} /><Route path="/meus-cursos" element={<p>Seleção de cursos</p>} /></Routes></MemoryRouter></QueryClientProvider>); }
beforeEach(() => { vi.clearAllMocks(); reportSelectionStore.cancel(); mocks.enabled = true; mocks.allowed = true; mocks.explicit = true; mocks.jobs.mockResolvedValue(response()); mocks.getJob.mockResolvedValue({ ...legacyJob, definitionSnapshot: null, courseIds: ['1', '2'] }); mocks.refreshJob.mockResolvedValue({ id: 'refreshed-job', status: 'queued' }); mocks.deleteJob.mockResolvedValue(undefined); });
afterEach(() => { cleanup(); reportSelectionStore.cancel(); });

describe('histórico privado', () => {
  it('allows selection with the resolved default connection before an explicit topbar choice', async () => {
    mocks.explicit = false; const user = userEvent.setup(); mount();
    await screen.findByText('Relatório de notas legado');
    expect(screen.getByRole('link', { name: 'Histórico' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Modelos' })).toHaveAttribute('href', '/relatorios/modelos');
    expect(screen.getByRole('button', { name: 'Novo Relatório' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Atualizar' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Novo Relatório' }));
    await screen.findByText('Seleção de cursos');
    expect(reportSelectionStore.getSnapshot().scope).toBe('synthetic-owner:synthetic-id');
    expect(reportSelectionStore.getSnapshot().active).toBe(true);
  });
  it('keeps legacy downloads and terminal actions with the templates flag off', async () => {
    mocks.enabled = false; const user = userEvent.setup(); mount();
    expect(await screen.findByRole('link', { name: 'Baixar arquivo' })).toHaveAttribute('href', legacyJob.downloadUrl);
    expect(screen.getByText('Moodle sintético', { selector: 'p' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Gerar relatório' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Novo Relatório' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Novo Relatório' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Ações de Relatório de notas legado' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Atualizar dados' }));
    await screen.findByText('Atualização solicitada. O novo arquivo aparecerá no histórico quando estiver pronto.');
    expect(mocks.refreshJob).toHaveBeenCalledWith('legacy-job');
    expect(screen.queryByText('Seleção de cursos')).not.toBeInTheDocument();
    expect(mocks.createJob).not.toHaveBeenCalled();
  });
  it('shows course and model in the title and removes scope disclosures', async () => {
    mocks.jobs.mockResolvedValue(response([{ ...legacyJob, reportType: 'template', templateNameSnapshot: 'Entregas e correções', totalCourses: 1, courses: [{ name: 'Turma Goiás', categoryName: 'SENAI' }], resultMetadata: { coverage: [], warnings: [], partial: false, truncated: false, collectionStartedAt: '', collectionEndedAt: '', generatedAt: '' } }]));
    mount();
    expect(await screen.findByText('Turma Goiás · Entregas e correções')).toBeInTheDocument();
    expect(screen.queryByText('Origem e cobertura')).not.toBeInTheDocument();
    expect(screen.queryByText('Cursos incluídos')).not.toBeInTheDocument();
  });
  it('explains incomplete activity coverage and actionable generation failures', async () => {
    mocks.jobs.mockResolvedValue(response([
      { ...legacyJob, id: 'partial-job', reportType: 'template', status: 'completed', templateNameSnapshot: 'Atividades', resultMetadata: { coverage: [{ courseId: '1', family: 'activities', fieldIds: ['activity.pending'], source: 'unavailable', complete: false, reason: 'activity_submission_mode_unknown' }], warnings: ['activity_submission_mode_unknown'], partial: true, truncated: false, collectionStartedAt: '', collectionEndedAt: '', generatedAt: '' } },
      { ...legacyJob, id: 'failed-job', reportType: 'template', status: 'failed', errorMessage: 'report_activity_limit', templateNameSnapshot: 'Atividades' },
    ]));
    mount();
    expect(await screen.findByText('Relatório parcial: o modo de submissão de algumas atividades não pôde ser confirmado.')).toBeInTheDocument();
    expect(screen.getByText(/O curso excedeu o limite de 25 atividades/)).toBeInTheDocument();
    expect(screen.queryByText('report_activity_limit')).not.toBeInTheDocument();
  });
  it('paginates on the server and resets the page when status, search or connection changes', async () => {
    mocks.jobs.mockResolvedValue(response([legacyJob], true)); const user = userEvent.setup(); mount();
    await screen.findByText('Relatório de notas legado');
    await user.click(screen.getByRole('button', { name: 'Próxima' }));
    await waitFor(() => expect(mocks.jobs).toHaveBeenLastCalledWith(2, 20, expect.objectContaining({ page: 2 }), expect.any(AbortSignal)));
    await user.selectOptions(screen.getByLabelText('Status'), 'failed');
    await waitFor(() => expect(mocks.jobs).toHaveBeenLastCalledWith(1, 20, expect.objectContaining({ status: 'failed' }), expect.any(AbortSignal)));
    await user.type(screen.getByLabelText('Buscar relatório'), 'Modelo');
    await waitFor(() => expect(mocks.jobs).toHaveBeenLastCalledWith(1, 20, expect.objectContaining({ search: 'Modelo' }), expect.any(AbortSignal)));
    await user.selectOptions(screen.getByLabelText('Conexão'), 'synthetic');
    await waitFor(() => expect(mocks.jobs).toHaveBeenLastCalledWith(1, 20, expect.objectContaining({ connectionRef: 'synthetic' }), expect.any(AbortSignal)));
    await user.click(screen.getByRole('button', { name: 'Limpar filtros' }));
    expect(screen.getByLabelText('Buscar relatório')).toHaveValue('');
    expect(screen.getByLabelText('Status')).toHaveValue('');
    expect(screen.getByLabelText('Conexão')).toHaveValue('');
    expect(mocks.jobs).toHaveBeenCalledWith(1, 20, { page: 1, search: '', status: '', connectionRef: '' }, expect.any(AbortSignal));
  });
  it('requires confirmation before deleting a terminal file', async () => {
    const user = userEvent.setup(); mount(); await screen.findByText('Relatório de notas legado');
    await user.click(screen.getByRole('button', { name: 'Ações de Relatório de notas legado' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Excluir' }));
    expect(mocks.deleteJob).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Excluir relatório' }));
    await waitFor(() => expect(mocks.deleteJob).toHaveBeenCalledWith('legacy-job'));
  });
  it('guards the direct history URL without querying jobs when permission is absent', async () => {
    mocks.allowed = false; mount(); await screen.findByText('Seleção de cursos'); expect(mocks.jobs).not.toHaveBeenCalled();
  });
});
