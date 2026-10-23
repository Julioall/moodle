import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ReportTemplatesPage } from '../features/reports/templates/ReportTemplatesPage';
import { AppHttpError } from '../integrations/http/api-client';

const mocks = vi.hoisted(() => ({ list: vi.fn(), delete: vi.fn(), owner: 'owner-a' }));
vi.mock('../features/auth/useSession', () => ({ useSession: () => ({ session: { isPending: false }, user: { id: mocks.owner }, can: () => true, reportTemplatesEnabled: true }) }));
vi.mock('../features/reports/templates/report-templates-gateway', () => ({ reportTemplatesGateway: mocks }));
const model = { id: 'model', name: 'Modelo sintético', revision: 3, definition: { columns: [{ fieldId: 'student.name' }] } };
function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const page = <QueryClientProvider client={client}><MemoryRouter><ReportTemplatesPage /></MemoryRouter></QueryClientProvider>;
  return { ...render(page), page };
}
beforeEach(() => { vi.clearAllMocks(); mocks.owner = 'owner-a'; mocks.list.mockResolvedValue({ items: [model], total: 1, page: 1, pageSize: 20 }); mocks.delete.mockResolvedValue(undefined); });
afterEach(cleanup);
describe('modelos privados', () => {
  it('shows ready models in the editable and deletable list under the Models tab', async () => {
    mocks.list.mockResolvedValue({ items: [{ ...model, id: 'ready-council', name: 'Preparação para conselho de classe', description: 'Indicadores para apoiar a análise coletiva.', revision: 1, definition: { columns: [{ fieldId: 'grade.below_minimum_items' }, { fieldId: 'activity.awaiting_grading' }, { fieldId: 'tracking.situation' }] } }], total: 6, page: 1, pageSize: 20 });
    mount();

    expect(await screen.findByRole('cell', { name: 'Preparação para conselho de classe Indicadores para apoiar a análise coletiva.' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Relatórios', level: 1 })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Modelos de relatório', level: 2 })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Novo modelo' })).toHaveAttribute('href', '/relatorios/modelos/novo');
    expect(screen.queryByRole('button', { name: 'Atualizar' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Histórico' })).toHaveAttribute('href', '/relatorios');
    expect(screen.getByRole('link', { name: 'Modelos' })).toHaveAttribute('aria-current', 'page');
    const edit = screen.getByRole('link', { name: 'Editar Preparação para conselho de classe' });
    expect(edit).toHaveTextContent('Editar');
    expect(edit).toHaveAttribute('href', '/relatorios/modelos/ready-council/editar');
    expect(screen.getByRole('button', { name: 'Excluir Preparação para conselho de classe' })).toHaveTextContent('Excluir');
    expect(screen.queryByRole('button', { name: /Usar modelo/ })).not.toBeInTheDocument();
  });

  it('shows table skeletons while loading without the legacy spinner', () => {
    mocks.list.mockImplementation(() => new Promise(() => {}));
    mount();

    expect(screen.getByRole('status')).toHaveTextContent('Carregando modelos…');
    expect(screen.queryByTestId('spinner')).not.toBeInTheDocument();
  });

  it('requires confirmation and submits the displayed revision once while pending', async () => {
    mocks.delete.mockImplementation(() => new Promise(() => {}));
    const user = userEvent.setup(); mount();
    await user.click(await screen.findByRole('button', { name: 'Excluir Modelo sintético' }));
    expect(mocks.delete).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(mocks.delete).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Excluir Modelo sintético' }));
    await user.dblClick(screen.getByRole('button', { name: 'Confirmar exclusão' }));
    expect(mocks.delete).toHaveBeenCalledTimes(1);
    expect(mocks.delete).toHaveBeenCalledWith('model', 3);
    expect(screen.getByRole('button', { name: 'Excluindo…' })).toBeDisabled();
  });
  it('preserves a conflict and requires an explicit refresh before confirming the new revision', async () => {
    mocks.delete.mockRejectedValueOnce(new AppHttpError(409, 'Conflict'));
    const user = userEvent.setup(); mount();
    await user.click(await screen.findByRole('button', { name: 'Excluir Modelo sintético' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar exclusão' }));
    await screen.findByRole('alert');
    mocks.list.mockResolvedValue({ items: [{ ...model, revision: 4 }], total: 1, page: 1, pageSize: 20 });
    await user.click(screen.getByRole('button', { name: 'Atualizar lista' }));
    await waitFor(() => expect(screen.getByRole('cell', { name: '4' })).toBeInTheDocument());
    expect(mocks.delete).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole('button', { name: 'Excluir Modelo sintético' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar exclusão' }));
    await waitFor(() => expect(mocks.delete).toHaveBeenLastCalledWith('model', 4));
  });
  it('clears the old owner deletion dialog when the authenticated owner changes', async () => {
    const user = userEvent.setup(); const view = mount();
    await user.click(await screen.findByRole('button', { name: 'Excluir Modelo sintético' }));
    mocks.owner = 'owner-b'; mocks.list.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 });
    view.rerender(<QueryClientProvider client={new QueryClient()}><MemoryRouter><ReportTemplatesPage /></MemoryRouter></QueryClientProvider>);
    await screen.findByText('Nenhum modelo nesta página.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(mocks.delete).not.toHaveBeenCalled();
  });
});
