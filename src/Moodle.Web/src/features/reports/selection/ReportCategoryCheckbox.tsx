import { Checkbox } from '@/components/ui/checkbox';
import { Button } from '@/components/ui/button';
import { coursesGateway } from '../../courses/courses-gateway';
import type { ReportSelection } from './useReportSelection';

export function ReportCategoryCheckbox({ selection, categoryPath, paths, visibleIds, count, incomplete }: { selection: ReportSelection; categoryPath: string; paths: string[]; visibleIds: string[]; count: number; incomplete: boolean }) {
  const store = selection.store;
  const pending = selection.snapshot.pending.has(categoryPath);
  const error = selection.snapshot.errors.get(categoryPath);
  let state = store.categoryState(categoryPath, visibleIds);
  if (state === true && !store.categoryResolved(categoryPath) && visibleIds.length < count) state = 'indeterminate';
  const resolve = () => void store.resolveCategory(categoryPath, paths, (path, page, signal) => coursesGateway.byCategory(path, selection.connectionRef, page, 100, signal));
  return <div className="flex shrink-0 items-center gap-2" onClick={(event) => { event.preventDefault(); event.stopPropagation(); }}>
    <Checkbox aria-label={`Selecionar ${categoryPath} para relatório`} checked={state} disabled={incomplete || pending} onCheckedChange={(checked) => {
      if (checked === true) resolve();
      else { store.removeCategory(categoryPath); store.toggle(visibleIds, false); }
    }} />
    {pending && <span className="text-xs" role="status">Carregando…</span>}
    {error && <span className="max-w-56 text-xs font-normal text-destructive" role="alert">{error}<Button size="sm" variant="link" onClick={resolve}>Tentar novamente</Button><Button size="sm" variant="link" onClick={() => store.removeCategory(categoryPath)}>Cancelar categoria</Button></span>}
  </div>;
}
