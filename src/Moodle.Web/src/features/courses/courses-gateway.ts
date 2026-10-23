import { createAppClient } from '../../integrations/http/api-client';

export type Course = { connectionRef: string; courseId: string; shortName?: string; fullName: string; displayName?: string; categoryId?: number | null; categoryName?: string | null; startDate?: string; endDate?: string; visible?: boolean; viewUrl?: string; courseImage?: string; progress?: number; lastAccessAt?: string };
export type Activity = { connectionRef: string; courseId: string; activityId: string; instanceId?: string; activityType: string; name: string; url?: string; visible?: boolean; userVisible?: boolean; hasDates: boolean; hasDeadline: boolean; dueAt?: string; openAt?: string; closeAt?: string; fileCount: number; pendingSubmissionCount?: number; awaitingGradingCount?: number };
export type ListResponse<T> = { data: T[]; meta: { page: number; pageSize: number; returned: number; total?: number | null; hasMore: boolean; generatedAt: string; connectionRef?: string; warnings?: string[]; source?: string; snapshotAt?: string; ageSeconds?: number; stale?: boolean; refreshQueued?: boolean; complete?: boolean } };
export type CourseResponse = { data: Course; meta: { generatedAt: string; connectionRef?: string; source?: string; snapshotAt?: string; ageSeconds?: number; stale?: boolean; refreshQueued?: boolean; complete?: boolean } };
export type CourseHierarchyNode = { path: string; name: string; level: number; courseCount: number };

export type CourseCatalogLoadOptions = {
  signal?: AbortSignal;
  onPage?: (response: ListResponse<Course>) => void;
  cachedPages?: ReadonlyMap<number, ListResponse<Course>>;
  pageConcurrency?: number;
  maxPages?: number;
};

const DEFAULT_PAGE_CONCURRENCY = 4;
const MAX_CATALOG_PAGES = 100;

function validTotal(response: ListResponse<Course>) {
  const total = response.meta.total;
  return Number.isSafeInteger(total) && Number(total) >= response.data.length &&
    !(Number(total) === response.data.length && response.meta.hasMore);
}

async function readAllPages(
  fetchPage: (page: number, signal?: AbortSignal) => Promise<ListResponse<Course>>,
  pageSize: number,
  options: CourseCatalogLoadOptions = {},
): Promise<ListResponse<Course>> {
  const maxPages = Math.max(1, Math.floor(options.maxPages ?? MAX_CATALOG_PAGES));
  const concurrency = Math.max(1, Math.min(8, Math.floor(options.pageConcurrency ?? DEFAULT_PAGE_CONCURRENCY)));
  const pages = new Map<number, ListResponse<Course>>();
  const firstPage = await fetchPage(1, options.signal);
  pages.set(1, firstPage);
  options.onPage?.(firstPage);

  const totalKnown = validTotal(firstPage);
  const total = totalKnown ? Number(firstPage.meta.total) : undefined;
  const expectedPageCount = totalKnown ? Math.ceil(total! / pageSize) : undefined;
  const pageLimit = expectedPageCount === undefined ? maxPages : Math.min(expectedPageCount, maxPages);

  const pageIsReusable = (pageNumber: number, response: ListResponse<Course>) =>
    response.meta.page === pageNumber &&
    response.meta.total === firstPage.meta.total &&
    Boolean(firstPage.meta.snapshotAt) && response.meta.snapshotAt === firstPage.meta.snapshotAt;

  const loadPage = async (pageNumber: number) => {
    options.signal?.throwIfAborted();
    const cached = options.cachedPages?.get(pageNumber);
    const response = cached && pageIsReusable(pageNumber, cached)
      ? cached
      : await fetchPage(pageNumber, options.signal);
    if (response.meta.snapshotAt !== firstPage.meta.snapshotAt) {
      throw new Error('O catálogo mudou de versão durante a paginação.');
    }
    pages.set(pageNumber, response);
    options.onPage?.(response);
  };

  if (expectedPageCount !== undefined) {
    let nextPage = 2;
    const workers = Array.from({ length: Math.min(concurrency, Math.max(0, pageLimit - 1)) }, async () => {
      while (nextPage <= pageLimit) {
        const pageNumber = nextPage++;
        await loadPage(pageNumber);
      }
    });
    const settled = await Promise.allSettled(workers);
    const failed = settled.find((result): result is PromiseRejectedResult => result.status === 'rejected');
    if (failed) throw failed.reason;
  } else {
    let currentPage = firstPage;
    let pageNumber = 2;
    while (pageNumber <= pageLimit && currentPage.data.length > 0 &&
      (currentPage.meta.hasMore || currentPage.data.length === pageSize)) {
      await loadPage(pageNumber);
      currentPage = pages.get(pageNumber)!;
      pageNumber += 1;
    }
  }

  const courses = [...pages.entries()]
    .sort(([left], [right]) => left - right)
    .flatMap(([, response]) => response.data);
  const reachedTotal = totalKnown && courses.length >= total!;
  const capped = expectedPageCount !== undefined
    ? expectedPageCount > maxPages
    : pages.size === maxPages && courses.length > 0 &&
      (pages.get(maxPages)?.meta.hasMore || pages.get(maxPages)?.data.length === pageSize);
  const complete = firstPage.meta.complete !== false && (totalKnown ? reachedTotal : !capped);

  return {
    data: courses,
    meta: {
      ...firstPage.meta,
      returned: courses.length,
      total: totalKnown ? total : firstPage.meta.total,
      hasMore: capped || (totalKnown ? courses.length < total! : false),
      complete,
    },
  };
}

export const createCoursesGateway = (client = createAppClient()) => {
  const getPage = (path: string, signal?: AbortSignal) => client.get<ListResponse<Course>>(path, {
    ...(signal ? { signal } : {}), admissionFamily: 'course_catalog'
  });
  const list = (connectionRef?: string, page = 1, pageSize = 20, signal?: AbortSignal) => getPage(`/api/courses?${new URLSearchParams({ ...(connectionRef ? { connectionRef } : {}), page: String(page), pageSize: String(pageSize) })}`, signal);
  const byCategory = (categoryPath: string, connectionRef?: string, page = 1, pageSize = 50, signal?: AbortSignal) => getPage(`/api/schools/courses?${new URLSearchParams({ categoryPath, ...(connectionRef ? { connectionRef } : {}), page: String(page), pageSize: String(pageSize) })}`, signal);
  const search = (query: string, connectionRef?: string, limit = 100, signal?: AbortSignal) => client.get<ListResponse<Course>>(`/api/courses/search?${new URLSearchParams({ query, ...(connectionRef ? { connectionRef } : {}), limit: String(limit) })}`, { ...(signal ? { signal } : {}), admissionFamily: 'course_catalog' });

  const listAll = (connectionRef?: string, pageSize = 100, options: CourseCatalogLoadOptions = {}) =>
    readAllPages((page, signal) => list(connectionRef, page, pageSize, signal), pageSize, options);

  const listAllByCategory = (categoryPath: string, connectionRef?: string, pageSize = 100, options: CourseCatalogLoadOptions = {}) =>
    readAllPages((page, signal) => byCategory(categoryPath, connectionRef, page, pageSize, signal), pageSize, options);

  return {
  hierarchy: (connectionRef?: string, signal?: AbortSignal) => client.get<{ data: CourseHierarchyNode[]; meta: { generatedAt: string; connectionRef?: string; source?: string; warnings?: string[]; refreshQueued?: boolean; complete?: boolean } }>(`/api/schools?${new URLSearchParams(connectionRef ? { connectionRef } : {})}`, { ...(signal ? { signal } : {}), admissionFamily: 'course_catalog' }),
  byCategory,
  search,
  listAllByCategory,
  list,
  listAll,
  get: (connectionRef: string, courseId: string, refresh = false) => client.get<CourseResponse>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}${refresh ? '?refresh=true' : ''}`, { admissionFamily: 'course_details' }),
  activities: (connectionRef: string, courseId: string, page = 1, pageSize = 20, includeActionSummary = false, refresh = false) => client.get<ListResponse<Activity>>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/activities?page=${page}&pageSize=${pageSize}${includeActionSummary ? '&includeActionSummary=true' : ''}${refresh ? '&refresh=true' : ''}`, { admissionFamily: 'course_details' }),
  };
};
export const coursesGateway = createCoursesGateway();
