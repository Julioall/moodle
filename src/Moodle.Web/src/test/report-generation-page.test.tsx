import { useState } from 'react';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { ReportGenerationPage } from '../features/reports/ReportGenerationPage';
import { reportSelectionStore } from '../features/reports/selection/report-selection';
import { useReportPreview } from '../features/reports/useReportPreview';
import type { ReportPreview } from '../features/reports/templates/report-templates-gateway';

const mocks = vi.hoisted(() => ({ preview: vi.fn(), generate: vi.fn(), list: vi.fn(), catalog: vi.fn() }));
vi.mock('../features/reports/templates/report-templates-gateway', () => ({ reportTemplatesGateway: mocks }));
vi.mock('../features/auth/useSession', () => ({ useSession: () => ({ user: { id: 'owner' }, can: () => true, reportTemplatesEnabled: true }) }));
vi.mock('../features/connections/useConnectionScope', () => ({ useConnectionScope: () => ({ connectionRef: 'synthetic', selectedConnection: { connectionId: 'identity', alias: 'Moodle sintético' }, connections: { isPending: false } }) }));

function sample(courseId = '1'): ReportPreview {
  return { columns: [{ fieldId: 'student.name', label: 'Nome', type: 'Text' }], rows: [{ courseId, studentId: '7001', cells: [{ state: 'Known', value: `Amostra ${courseId}` }] }], sampledCourseIds: [courseId], sampledCourses: [{ courseId, name: `Curso ${courseId}` }], requestedCourseCount: 2, returnedRows: 1, truncated: false, partial: false, coverage: [], warnings: [], referenceAt: '2026-10-02T00:00:00Z', generatedAt: '2026-10-02T00:00:00Z' };
}

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}><MemoryRouter initialEntries={[{ pathname: '/relatorios/gerar', state: { returnTo: '/meus-cursos?connectionRef=synthetic' } }]}><Routes><Route path="/relatorios/gerar" element={<ReportGenerationPage />} /><Route path="/meus-cursos" element={<p>Seleção de cursos</p>} /><Route path="/relatorios" element={<p>Histórico de execução</p>} /></Routes></MemoryRouter></QueryClientProvider>);
}

function selectCourses() {
  reportSelectionStore.setScope('owner:identity');
  reportSelectionStore.start();
  reportSelectionStore.toggle(['1', '2'], true);
}

beforeAll(() => {
  const element = HTMLElement.prototype as HTMLElement & { hasPointerCapture?: () => boolean; setPointerCapture?: () => void; releasePointerCapture?: () => void };
  element.hasPointerCapture ??= () => false;
  element.setPointerCapture ??= () => undefined;
  element.releasePointerCapture ??= () => undefined;
  HTMLElement.prototype.scrollIntoView ??= () => undefined;
});

async function chooseTemplate() {
  const model = await screen.findByRole('combobox', { name: 'Modelo do relatório' });
  fireEvent.keyDown(model, { key: 'ArrowDown', code: 'ArrowDown' });
  const option = await screen.findByRole('option', { name: 'Notas da turma · revisão 3' });
  fireEvent.click(option);
}

beforeEach(() => {
  vi.clearAllMocks();
  reportSelectionStore.cancel();
  selectCourses();
  mocks.list.mockResolvedValue({ items: [{ id: 'template-1', name: 'Notas da turma', revision: 3 }], page: 1, pageSize: 20, total: 1 });
  mocks.catalog.mockResolvedValue({ limits: { maxCourses: 20, maxRows: 10_000, maxAssignmentsPerCourse: 25, generationTimeoutSeconds: 600 } });
  mocks.preview.mockResolvedValue(sample());
});
afterEach(() => { cleanup(); reportSelectionStore.cancel(); });

describe('página de geração e prévia temporária', () => {
  it('returns to course selection without losing IDs and aborts an unfinished preview', async () => {
    mocks.preview.mockImplementation(() => new Promise(() => {}));
    const user = userEvent.setup();
    mount();
    expect(await screen.findByText(/conexão Moodle sintético/)).toBeInTheDocument();
    await chooseTemplate();
    await waitFor(() => expect(mocks.preview).toHaveBeenCalledTimes(1));
    const signal = mocks.preview.mock.calls[0][1] as AbortSignal;
    await user.click(screen.getByRole('button', { name: 'Voltar à seleção' }));
    await screen.findByText('Seleção de cursos');
    await waitFor(() => expect(signal.aborted).toBe(true));
    expect([...reportSelectionStore.getSnapshot().ids]).toEqual(['1', '2']);
    expect(mocks.generate).not.toHaveBeenCalled();
  });

  it('previews the chosen model and submits only after confirmation', async () => {
    const user = userEvent.setup();
    mount();
    await chooseTemplate();
    await screen.findByText('Amostra 1');
    expect(screen.getByText('Cursos consultados na amostra: Curso 1')).toBeInTheDocument();
    expect(screen.getByText(/Por limite de leitura, 1 curso não aparece nesta amostra/)).toBeInTheDocument();
    expect(mocks.preview).toHaveBeenCalledWith({ templateId: 'template-1', expectedRevision: 3, connectionRef: 'synthetic', courseIds: ['1', '2'] }, expect.any(AbortSignal));
    expect(mocks.generate).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Gerar relatório' }));
    await screen.findByText('Histórico de execução');
    expect(mocks.generate).toHaveBeenCalledTimes(1);
    expect(mocks.generate).toHaveBeenCalledWith({ templateId: 'template-1', expectedRevision: 3 }, 'synthetic', ['1', '2']);
    expect(reportSelectionStore.getSnapshot().ids.size).toBe(0);
  });

  it('ignores late results from an older preview after the selected course changes', async () => {
    const replies = new Map<string, (value: ReportPreview) => void>();
    const signals = new Map<string, AbortSignal>();
    mocks.preview.mockImplementation((input, signal) => new Promise((resolve) => { replies.set(input.courseIds[0], resolve); signals.set(input.courseIds[0], signal); }));
    function PreviewHost() {
      const [course, setCourse] = useState('1');
      const preview = useReportPreview('owner:identity', { definition: { schemaVersion: 1, sourceId: 'student_course', columns: [{ fieldId: 'student.name' }] }, connectionRef: 'synthetic', courseIds: [course] });
      return <><button onClick={() => setCourse('2')}>Trocar curso</button><p>{preview.data?.rows[0].cells[0].value ?? 'Aguardando amostra'}</p></>;
    }
    const client = new QueryClient();
    const user = userEvent.setup();
    render(<QueryClientProvider client={client}><PreviewHost /></QueryClientProvider>);
    await waitFor(() => expect(replies.has('1')).toBe(true));
    await user.click(screen.getByRole('button', { name: 'Trocar curso' }));
    await waitFor(() => expect(replies.has('2')).toBe(true));
    expect(signals.get('1')?.aborted).toBe(true);
    replies.get('2')!(sample('2'));
    await screen.findByText('Amostra 2');
    replies.get('1')!(sample('1'));
    await waitFor(() => expect(screen.getByText('Amostra 2')).toBeInTheDocument());
    expect(screen.queryByText('Amostra 1')).not.toBeInTheDocument();
  });

  it('blocks generation when the selected course count exceeds the live catalog limit', async () => {
    mocks.catalog.mockResolvedValue({ limits: { maxCourses: 1, maxRows: 10_000, maxAssignmentsPerCourse: 25, generationTimeoutSeconds: 600 } });
    const user = userEvent.setup();
    mount();
    await chooseTemplate();
    expect(await screen.findByRole('alert')).toHaveTextContent('ultrapassa o limite de 1 curso');
    expect(screen.getByRole('button', { name: 'Gerar relatório' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Gerar relatório' }));
    expect(mocks.generate).not.toHaveBeenCalled();
  });
});
