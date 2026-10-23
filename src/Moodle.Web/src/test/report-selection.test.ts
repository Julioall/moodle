import { describe, expect, it } from 'vitest';
import { normalizeReportCourseIds, ReportSelectionStore } from '../features/reports/selection/report-selection';
import type { Course, ListResponse } from '../features/courses/courses-gateway';

function page(number: number, ids: string[], hasMore = false, complete = true): ListResponse<Course> {
  return { data: ids.map((courseId) => ({ courseId, connectionRef: 'synthetic', fullName: `Curso ${courseId}` })), meta: { page: number, pageSize: 100, returned: ids.length, hasMore, complete, generatedAt: '2026-10-02T00:00:00Z' } };
}

describe('report selection', () => {
  it('normalizes concrete IDs, deduplicates and enforces the limit', () => {
    expect(normalizeReportCourseIds([' 12 ', '2', '12'])).toEqual(['12', '2']);
    expect(() => normalizeReportCourseIds(['category:1'])).toThrow();
    expect(() => normalizeReportCourseIds(Array.from({ length: 501 }, (_, i) => String(i + 1)))).toThrow('500');
  });
  it('resolves every page of canonical categories before marking the parent checked', async () => {
    const store = new ReportSelectionStore(); store.setScope('owner:connection-id'); store.start();
    const paths: string[] = [];
    await store.resolveCategory('parent', ['Root > Arbitrary', 'Root > Other'], async (path, number) => {
      paths.push(`${path}:${number}`);
      return path.endsWith('Other') ? page(number, ['3']) : page(number, number === 1 ? ['1'] : ['1', '2'], number === 1);
    });
    expect(paths).toEqual(['Root > Arbitrary:1', 'Root > Arbitrary:2', 'Root > Other:1']);
    expect([...store.getSnapshot().ids]).toEqual(['1', '2', '3']);
    expect(store.categoryState('parent')).toBe(true);
    store.toggle(['2'], false);
    expect(store.categoryState('parent')).toBe('indeterminate');
  });
  it('preserves confirmed IDs on failure and blocks completion until retry', async () => {
    const store = new ReportSelectionStore(); store.start();
    await store.resolveCategory('parent', ['Arbitrary'], async (_, number) => {
      if (number === 2) throw new Error('Página indisponível');
      return page(1, ['1'], true);
    });
    expect([...store.getSnapshot().ids]).toEqual(['1']);
    expect(store.getSnapshot().errors.has('parent')).toBe(true);
    expect(store.categoryState('parent')).toBe('indeterminate');
    await store.resolveCategory('parent', ['Arbitrary'], async (_, number) => page(number, number === 1 ? ['1'] : ['2'], number === 1));
    expect(store.getSnapshot().errors.size).toBe(0);
    expect(store.categoryState('parent')).toBe(true);
  });
  it('blocks incomplete coverage and rejects a 501st course without losing 500 confirmed IDs', async () => {
    const store = new ReportSelectionStore(); store.start();
    store.toggle(Array.from({ length: 500 }, (_, i) => String(i + 1)), true);
    await store.resolveCategory('too-many', ['Arbitrary'], async () => page(1, ['501']));
    expect(store.getSnapshot().ids.size).toBe(500);
    expect(store.getSnapshot().errors.get('too-many')).toContain('500');
    await store.resolveCategory('partial', ['Other'], async () => page(1, ['1'], false, false));
    expect(store.getSnapshot().errors.has('partial')).toBe(true);
  });
  it('cancels a pending category and ignores late data after connection changes', async () => {
    const store = new ReportSelectionStore(); store.setScope('owner:old'); store.start(); store.toggle(['1'], true);
    let finish!: (value: ListResponse<Course>) => void;
    const resolution = store.resolveCategory('parent', ['Arbitrary'], () => new Promise((resolve) => { finish = resolve; }));
    expect(store.getSnapshot().pending.size).toBe(1);
    store.setScope('owner:new'); finish(page(1, ['2'])); await resolution;
    expect(store.getSnapshot().ids.size).toBe(0);
    expect(store.getSnapshot().pending.size).toBe(0);
    expect(store.getSnapshot().notice).toContain('conexão mudou');
  });
});
