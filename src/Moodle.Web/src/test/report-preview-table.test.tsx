import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { ReportPreviewTable } from '../features/reports/ReportPreviewTable';
import type { ReportPreview } from '../features/reports/templates/report-templates-gateway';

const preview: ReportPreview = {
  columns: [{ fieldId: 'access.course_at', label: 'Último acesso ao curso', type: 'DateTime' }],
  rows: [{ courseId: '1', studentId: 'synthetic-student', cells: [{ value: '2026-10-05T16:30:00Z', state: 'Known' }] }],
  sampledCourseIds: ['1'], sampledCourses: [{ courseId: '1', name: 'Curso sintético' }],
  requestedCourseCount: 1, returnedRows: 1, truncated: false, partial: false,
  coverage: [{ courseId: '1', family: 'participants', fieldIds: ['access.course_at'], source: 'live', readAt: '2026-10-05T16:20:00Z', snapshotAt: null, complete: true }],
  warnings: [], generatedAt: '2026-10-05T16:30:00Z', referenceAt: '2026-10-05T16:25:00Z',
};

afterEach(cleanup);

describe('prévia de relatório', () => {
  it('shows date-time cells and read metadata in Brazilian format with the UTC zone', async () => {
    const user = userEvent.setup();
    render(<ReportPreviewTable preview={preview} />);

    expect(screen.getByRole('cell')).toHaveTextContent(/05\/10\/2026.*16:30 UTC/);
    expect(screen.getByRole('cell').textContent).not.toContain('2026-10-05T16:30:00Z');

    await user.click(screen.getByText('Origem e datas da leitura'));
    expect(screen.getByText(/Coleta:.*16:25 UTC.*Prévia:.*16:30 UTC/)).toBeInTheDocument();
    expect(screen.getByText(/participants: live.*16:20 UTC/)).toBeInTheDocument();
  });

  it('translates population warnings and shows only an aggregate uncertainty count', async () => {
    const user = userEvent.setup();
    render(<ReportPreviewTable preview={{ ...preview, partial: true,
      warnings: ['participant_classification_incomplete'],
      coverage: [{ courseId: '1', family: 'population', fieldIds: [], source: 'incomplete', complete: false,
        reason: 'participant_classification_incomplete', uncertainCount: 2 }] }} />);

    expect(screen.getByText('Há participantes sem classificação suficiente; a exportação completa foi bloqueada.')).toBeInTheDocument();
    await user.click(screen.getByText('Origem e datas da leitura'));
    expect(screen.getByText(/2 participante\(s\) sem classificação segura/)).toBeInTheDocument();
    expect(screen.queryByText('participant_classification_incomplete')).not.toBeInTheDocument();
  });
});
