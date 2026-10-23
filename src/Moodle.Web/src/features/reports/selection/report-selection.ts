import type { Course, ListResponse } from '../../courses/courses-gateway';

export const MAX_REPORT_COURSES = 500;
export type SelectionSnapshot = {
  scope: string; active: boolean; ids: ReadonlySet<string>; pending: ReadonlySet<string>;
  errors: ReadonlyMap<string, string>; notice?: string;
};
export type SelectionPageReader = (path: string, page: number, signal: AbortSignal) => Promise<ListResponse<Course>>;

export function normalizeReportCourseIds(values: Iterable<string>): string[] {
  const ids = [...new Set([...values].map((value) => value.trim()))];
  if (ids.some((id) => !/^[1-9]\d*$/.test(id))) throw new Error('A seleção contém um curso inválido.');
  if (ids.length > MAX_REPORT_COURSES) throw new Error('Selecione no máximo 500 cursos.');
  return ids.sort();
}

// Only concrete, confirmed IDs are shared between pages. Category paths are
// transient resolution keys; they never become a dynamic job scope.
export class ReportSelectionStore {
  private snapshot: SelectionSnapshot = { scope: '', active: false, ids: new Set(), pending: new Set(), errors: new Map() };
  private listeners = new Set<() => void>();
  private controllers = new Map<string, AbortController>();
  private categories = new Map<string, Set<string>>();
  private resolved = new Set<string>();
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  getSnapshot = () => this.snapshot;
  private publish(patch: Partial<SelectionSnapshot>) {
    this.snapshot = { ...this.snapshot, ...patch };
    this.listeners.forEach((listener) => listener());
  }
  setScope(scope: string) {
    if (scope === this.snapshot.scope) return;
    const changed = this.snapshot.active && this.snapshot.ids.size > 0;
    this.cancel();
    this.publish({ scope, active: false, notice: changed ? 'A conexão mudou. Selecione novamente os cursos.' : undefined });
  }
  start() { this.publish({ active: true, notice: undefined }); }
  showError(message: string) { this.publish({ notice: message }); }
  cancel() {
    this.controllers.forEach((controller) => controller.abort());
    this.controllers.clear(); this.categories.clear(); this.resolved.clear();
    this.publish({ active: false, ids: new Set(), pending: new Set(), errors: new Map(), notice: undefined });
  }
  toggle(values: Iterable<string>, checked: boolean) {
    const ids = new Set(this.snapshot.ids);
    normalizeReportCourseIds(values).forEach((id) => checked ? ids.add(id) : ids.delete(id));
    normalizeReportCourseIds(ids);
    this.publish({ ids, notice: undefined });
  }
  categoryState(key: string, visibleIds: Iterable<string> = []): boolean | 'indeterminate' {
    const known = this.categories.get(key) ?? new Set(visibleIds);
    const count = [...known].filter((id) => this.snapshot.ids.has(id)).length;
    if (this.snapshot.pending.has(key) || this.snapshot.errors.has(key)) return 'indeterminate';
    return count === 0 ? false : count === known.size ? true : 'indeterminate';
  }
  categoryResolved(key: string) { return this.resolved.has(key); }
  removeCategory(key: string) {
    this.controllers.get(key)?.abort(); this.controllers.delete(key);
    this.toggle(this.categories.get(key) ?? [], false);
    this.categories.delete(key);
    this.resolved.delete(key);
    const pending = new Set(this.snapshot.pending); pending.delete(key);
    const errors = new Map(this.snapshot.errors); errors.delete(key);
    this.publish({ pending, errors });
  }
  async resolveCategory(key: string, canonicalPaths: string[], read: SelectionPageReader) {
    if (this.controllers.has(key)) return;
    const controller = new AbortController(); this.controllers.set(key, controller);
    this.resolved.delete(key);
    const scope = this.snapshot.scope;
    const known = this.categories.get(key) ?? new Set<string>(); this.categories.set(key, known);
    const pending = new Set(this.snapshot.pending); pending.add(key);
    const errors = new Map(this.snapshot.errors); errors.delete(key);
    this.publish({ pending, errors });
    try {
      for (const path of [...new Set(canonicalPaths)]) {
        let snapshotAt: string | undefined;
        const seen = new Set<string>();
        for (let page = 1; ; page++) {
          controller.signal.throwIfAborted();
          const response = await read(path, page, controller.signal);
          controller.signal.throwIfAborted();
          if (scope !== this.snapshot.scope) return;
          if (page === 1) snapshotAt = response.meta.snapshotAt;
          else if (snapshotAt && response.meta.snapshotAt !== snapshotAt) throw new Error('O catálogo mudou durante a seleção. Tente novamente.');
          if (response.meta.page !== page) throw new Error('A paginação retornou uma página inesperada.');
          const ids = normalizeReportCourseIds(response.data.map((course) => course.courseId));
          const newIds = ids.filter((id) => !seen.has(id));
          ids.forEach((id) => seen.add(id));
          this.toggle(ids, true);
          ids.forEach((id) => known.add(id));
          if (!response.meta.hasMore) {
            if (response.meta.complete === false) throw new Error('O catálogo está incompleto. Tente novamente.');
            if (response.meta.total != null && seen.size !== response.meta.total) throw new Error('O total de cursos não corresponde às páginas recebidas. Tente novamente.');
            break;
          }
          if (newIds.length === 0 || page >= MAX_REPORT_COURSES) throw new Error('Não foi possível confirmar todas as páginas da categoria.');
        }
      }
      this.resolved.add(key);
    } catch (error) {
      if (!controller.signal.aborted && scope === this.snapshot.scope) {
        const failed = new Map(this.snapshot.errors);
        failed.set(key, error instanceof Error ? error.message : 'Não foi possível resolver a categoria.');
        this.publish({ errors: failed });
      }
    } finally {
      if (this.controllers.get(key) === controller) {
        this.controllers.delete(key);
        const remaining = new Set(this.snapshot.pending); remaining.delete(key);
        this.publish({ pending: remaining });
      }
    }
  }
}

export const reportSelectionStore = new ReportSelectionStore();
