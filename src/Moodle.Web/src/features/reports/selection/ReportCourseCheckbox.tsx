import { Checkbox } from '@/components/ui/checkbox';
import type { ReportSelection } from './useReportSelection';

export function ReportCourseCheckbox({ selection, ids, label, disabled = false }: { selection: ReportSelection; ids: string[]; label: string; disabled?: boolean }) {
  const count = ids.filter((id) => selection.snapshot.ids.has(id)).length;
  const checked = count === 0 ? false : count === ids.length ? true : 'indeterminate';
  return <Checkbox aria-label={`Selecionar ${label} para relatório`} checked={checked} disabled={disabled} onCheckedChange={(value) => {
    try { selection.store.toggle(ids, value === true); }
    catch (error) { selection.store.showError(error instanceof Error ? error.message : 'Não foi possível selecionar.'); }
  }} />;
}
