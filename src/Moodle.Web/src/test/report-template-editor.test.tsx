import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ReportTemplateEditorPage } from '../features/reports/templates/ReportTemplateEditorPage';
import { AppHttpError } from '../integrations/http/api-client';

const mocks = vi.hoisted(() => ({ enabled: true, catalog: vi.fn(), get: vi.fn(), create: vi.fn(), update: vi.fn(), preview: vi.fn(), courses: vi.fn() }));
vi.mock('../features/connections/useConnectionScope', () => ({ useMoodleConnections: () => ({ data: { data: [{ connectionId: 'identity-a', connectionRef: 'synthetic', alias: 'Conexão sintética' }] } }) }));
vi.mock('../features/courses/courses-gateway', () => ({ coursesGateway: { list: mocks.courses } }));
vi.mock('../features/auth/useSession', () => ({ useSession: () => ({ session: { isPending: false }, user: { id: 'synthetic-owner' }, can: () => true, reportTemplatesEnabled: mocks.enabled }) }));
vi.mock('../features/reports/templates/report-templates-gateway', () => ({ reportTemplatesGateway: mocks }));
function mount(path = '/relatorios/modelos/novo') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[path]}><Routes><Route path="/relatorios/modelos/novo" element={<ReportTemplateEditorPage />} /><Route path="/relatorios/modelos/:id/editar" element={<ReportTemplateEditorPage />} /><Route path="/relatorios/modelos" element={<div>Lista de modelos</div>} /><Route path="/relatorios" element={<div>Histórico disponível</div>} /></Routes></MemoryRouter></QueryClientProvider>);
}
beforeEach(() => {
  vi.clearAllMocks(); mocks.enabled = true;
  mocks.catalog.mockResolvedValue({ fields: [
    { fieldId: 'student.name', label: 'Nome do estudante', group: 'Aluno', type: 'Text', filterable: true, requirements: ['participants'], readPolicy: 'snapshot_preferred' },
    { fieldId: 'course.name', label: 'Nome do curso / UC', group: 'Curso / UC', type: 'Text', filterable: true, requirements: ['course'], readPolicy: 'snapshot_preferred' },
    { fieldId: 'access.course_at', label: 'Último acesso ao curso', group: 'Acesso', type: 'DateTime', filterable: true, requirements: ['participants'], readPolicy: 'live_required' },
    { fieldId: 'access.days_since_course', label: 'Dias sem acesso', group: 'Acesso', type: 'Number', filterable: true, requirements: ['participants'], readPolicy: 'live_required' },
    { fieldId: 'grade.value', label: 'Nota', group: 'Desempenho', type: 'Number', filterable: true, requirements: ['gradebook'], readPolicy: 'live_required' },
    { fieldId: 'tracking.situation', label: 'Situação', group: 'Desempenho', type: 'Enum', filterable: true, requirements: ['activities'], readPolicy: 'live_required' },
  ], limits: { maxColumns: 30, maxFilters: 20 } });
  mocks.create.mockResolvedValue({ id: 'created' });
  mocks.courses.mockResolvedValue({ data: [{ courseId: '1', fullName: 'Curso sintético' }], meta: { hasMore: true } });
  mocks.get.mockResolvedValue({ id: 'model', name: 'Modelo original', revision: 3, definition: { schemaVersion: 1, sourceId: 'student_course', columns: [{ fieldId: 'student.name' }] } });
});
afterEach(cleanup);
describe('editor de modelos', () => {
  it('loads a live sample only after explicit scope selection and aborts when closed without persisting the scope', async () => {
    mocks.preview.mockImplementation(() => new Promise(() => {}));
    const user = userEvent.setup(); mount();
    await screen.findByText('Colunas do relatório');
    expect(screen.getByText('As colunas do modelo estão configuradas.')).toBeInTheDocument();
    expect(screen.getByText('Escolha um curso de amostra para ver os dados nas linhas.')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'Configurações' }));
    await user.type(screen.getByLabelText('Nome do modelo'), 'Modelo sintético');
    await user.click(screen.getByRole('tab', { name: 'Campos e prévia' }));
    expect(mocks.courses).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Escolher curso de amostra' }));
    expect(mocks.preview).not.toHaveBeenCalled();
    await user.selectOptions(screen.getByLabelText('Conexão de amostra'), 'identity-a');
    await screen.findByRole('option', { name: 'Curso sintético' });
    expect(mocks.courses).toHaveBeenCalledWith('synthetic', 1, 20, expect.any(AbortSignal));
    await user.selectOptions(screen.getByLabelText('Curso / UC de amostra'), '1');
    await waitFor(() => expect(mocks.preview).toHaveBeenCalledTimes(1));
    expect(mocks.preview.mock.calls[0][0]).toEqual({ connectionRef: 'synthetic', courseIds: ['1'], definition: { schemaVersion: 1, sourceId: 'student_course', columns: ['student.name', 'course.name', 'access.course_at', 'access.days_since_course', 'grade.value', 'tracking.situation'].map(fieldId => ({ fieldId })), filters: [], parameters: { inactiveDaysThreshold: 7, minGradePercent: 60 } } });
    const signal = mocks.preview.mock.calls[0][1] as AbortSignal;
    await user.click(screen.getByRole('button', { name: 'Fechar amostra' }));
    await waitFor(() => expect(signal.aborted).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Salvar modelo' }));
    await screen.findByText('Lista de modelos');
    expect(mocks.create.mock.calls[0][0]).not.toHaveProperty('courseIds');
    expect(mocks.create.mock.calls[0][0]).not.toHaveProperty('connectionRef');
  });
  it('saves ordered labels with keyboard controls and never fetches sample data for the structure', async () => {
    const user = userEvent.setup(); mount();
    await screen.findByText('Colunas do relatório');
    await user.click(screen.getByRole('tab', { name: 'Configurações' }));
    await user.type(screen.getByLabelText('Nome do modelo'), 'Modelo sintético');
    await user.clear(screen.getByLabelText('Dias sem acesso para considerar atenção'));
    await user.type(screen.getByLabelText('Dias sem acesso para considerar atenção'), '14');
    await user.clear(screen.getByLabelText('Nota mínima (%)'));
    await user.type(screen.getByLabelText('Nota mínima (%)'), '72.5');
    await user.click(screen.getByRole('tab', { name: 'Campos e prévia' }));
    const reorder = screen.getByRole('button', { name: /Reordenar Nota/ });
    reorder.focus(); await user.keyboard('{ArrowLeft}');
    expect(screen.getByText('Nota movida para a posição 4 de 6.')).toBeInTheDocument();
    const titleInput = screen.getByLabelText('Título de Nota');
    await user.clear(titleInput);
    await user.type(titleInput, 'Nota Moodle');
    expect(screen.getAllByRole('textbox').filter(e => e.getAttribute('aria-label')?.startsWith('Título de'))
      .map(e => (e as HTMLInputElement).value)).toEqual([
      'Nome do estudante', 'Nome do curso / UC', 'Último acesso ao curso',
      'Nota Moodle', 'Dias sem acesso', 'Situação',
    ]);
    expect(mocks.preview).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Salvar modelo' }));
    await screen.findByText('Lista de modelos');
    expect(mocks.create).toHaveBeenCalledWith(expect.objectContaining({ definition: { schemaVersion: 1, sourceId: 'student_course', columns: [
      { fieldId: 'student.name' }, { fieldId: 'course.name' }, { fieldId: 'access.course_at' },
      { fieldId: 'grade.value', label: 'Nota Moodle' }, { fieldId: 'access.days_since_course' },
      { fieldId: 'tracking.situation' },
    ], filters: [], parameters: { inactiveDaysThreshold: 14, minGradePercent: 72.5 } } }));
  });
  it('hides filter editing and preserves filters already saved on a model', async () => {
    const savedFilter = { fieldId: 'access.days_since_course', operator: 'greater_than', value: '7' };
    mocks.get.mockResolvedValueOnce({ id: 'model', name: 'Modelo original', revision: 3, definition: {
      schemaVersion: 1, sourceId: 'student_course', columns: [{ fieldId: 'student.name' }], filters: [savedFilter],
      parameters: { inactiveDaysThreshold: 7, minGradePercent: 60 },
    } });
    const user = userEvent.setup(); mount('/relatorios/modelos/model/editar');
    await screen.findByText('Colunas do relatório');
    await user.click(screen.getByRole('tab', { name: 'Configurações' }));
    await screen.findByDisplayValue('Modelo original');
    expect(screen.queryByRole('tab', { name: /Filtros/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Salvar modelo' }));
    await screen.findByText('Lista de modelos');
    expect(mocks.update.mock.calls[0][1].definition.filters).toEqual([savedFilter]);
  });
  it('preserves the edited draft and captured revision after a 409', async () => {
    mocks.update.mockRejectedValue(new AppHttpError(409, 'Conflict'));
    const user = userEvent.setup(); mount('/relatorios/modelos/model/editar');
    await screen.findByText('Colunas do relatório');
    await user.click(screen.getByRole('tab', { name: 'Configurações' }));
    await screen.findByDisplayValue('Modelo original');
    await user.clear(screen.getByLabelText('Nome do modelo'));
    await user.type(screen.getByLabelText('Nome do modelo'), 'Meu rascunho');
    await user.click(screen.getByRole('button', { name: 'Salvar modelo' }));
    await screen.findByText(/Seu rascunho foi preservado/);
    expect(screen.getByLabelText('Nome do modelo')).toHaveValue('Meu rascunho');
    expect(mocks.update).toHaveBeenCalledWith('model', expect.objectContaining({ name: 'Meu rascunho' }), 3);
    expect(mocks.preview).not.toHaveBeenCalled();
  });
  it('guards a direct URL when the backend session flag is off without reading the catalog', async () => {
    mocks.enabled = false; mount();
    await screen.findByText('Histórico disponível');
    await waitFor(() => expect(mocks.catalog).not.toHaveBeenCalled());
  });
});
