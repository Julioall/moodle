import { Link } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import type { ReportSelection } from './useReportSelection';

export function ReportSelectionBar({ selection, incomplete = false, onAdvance }: { selection: ReportSelection; incomplete?: boolean; onAdvance: () => void }) {
  if (!selection.active) return selection.snapshot.notice ? <p role="status">{selection.snapshot.notice}</p> : null;
  const { snapshot, store } = selection;
  const busy = snapshot.pending.size > 0;
  return <>
    <div className="h-28 sm:h-20" aria-hidden="true" />
    {snapshot.notice && <p role="alert" className="text-sm text-destructive">{snapshot.notice}</p>}
    <section aria-label="Seleção para relatório" className="fixed inset-x-0 bottom-0 z-40 flex flex-wrap items-center justify-between gap-3 border-t bg-background px-4 py-4 shadow-lg md:left-[var(--sidebar-width)] md:group-has-[[data-state=collapsed]]/sidebar-wrapper:left-[var(--sidebar-width-icon)] md:px-6">
      <div className="min-w-0"><p className="font-semibold" role="status" aria-live="polite">{snapshot.ids.size} cursos / UCs selecionados</p><Link className="text-sm text-primary underline" to="/escolas">Selecionar em Escolas</Link>{busy && <p className="text-sm" role="status">Confirmando todas as páginas…</p>}{incomplete && <p className="text-sm text-destructive">Aguarde o catálogo completo para avançar.</p>}</div>
      <div className="flex gap-2"><Button variant="outline" onClick={() => store.cancel()}>Cancelar</Button><Button data-report-advance disabled={busy || incomplete || snapshot.errors.size > 0 || snapshot.ids.size === 0} onClick={onAdvance}>Avançar</Button></div>
    </section>
  </>;
}
