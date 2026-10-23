import type { Course, ListResponse } from './courses-gateway';

export class CourseCatalogPageCache {
  readonly pages = new Map<number, ListResponse<Course>>();
  private firstPageMeta: ListResponse<Course>['meta'] | undefined;

  remember(response: ListResponse<Course>) {
    if (response.meta.page === 1) {
      const previous = this.pages.get(1);
      if (!response.meta.snapshotAt || previous?.meta.snapshotAt !== response.meta.snapshotAt || previous.meta.total !== response.meta.total) {
        this.pages.clear();
      }
      this.firstPageMeta = response.meta;
    }

    if (!this.firstPageMeta?.snapshotAt || response.meta.snapshotAt !== this.firstPageMeta.snapshotAt ||
        response.meta.total !== this.firstPageMeta.total) return;
    this.pages.set(response.meta.page, response);
  }

  seed(response: ListResponse<Course>) {
    const { total, pageSize, snapshotAt, complete } = response.meta;
    if (complete !== true || !snapshotAt || !Number.isSafeInteger(total) || total !== response.data.length || pageSize < 1) return;

    this.pages.clear();
    const pageCount = Math.ceil(response.data.length / pageSize);
    for (let index = 0; index < pageCount; index++) {
      const page = index + 1;
      const data = response.data.slice(index * pageSize, page * pageSize);
      this.pages.set(page, {
        data,
        meta: { ...response.meta, page, returned: data.length, hasMore: page < pageCount },
      });
    }
    this.firstPageMeta = this.pages.get(1)?.meta;
  }
}
