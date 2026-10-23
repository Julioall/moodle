import { afterEach, describe, expect, it, vi } from 'vitest';
import { createCoursesGateway } from '../features/courses/courses-gateway';

describe('courses gateway', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('uses composite connectionRef in course and activities URLs', async () => {
    const get = vi.fn().mockResolvedValue({ data: [], meta: { page: 1, pageSize: 20, returned: 0, hasMore: false, generatedAt: '2026-08-10T00:00:00Z' }});
    await createCoursesGateway({ get } as never).activities('senai-go', '42');
    expect(get).toHaveBeenCalledWith('/api/courses/senai-go/42/activities?page=1&pageSize=20', { admissionFamily: 'course_details' });
  });

  it('searches the catalogue without loading every course page', async () => {
    const get = vi.fn().mockResolvedValue({ data: [], meta: { page: 1, pageSize: 100, returned: 0, hasMore: false, generatedAt: '2026-08-10T00:00:00Z' }});
    await createCoursesGateway({ get } as never).search('Técnico em Segurança', 'monitora', 100);
    expect(get).toHaveBeenCalledWith('/api/courses/search?query=T%C3%A9cnico+em+Seguran%C3%A7a&connectionRef=monitora&limit=100', { admissionFamily: 'course_catalog' });
  });

  it('adds refresh to activity requests when requested', async () => {
    const get = vi.fn().mockResolvedValue({ data: [], meta: { page: 1, pageSize: 20, returned: 0, hasMore: false, generatedAt: '2026-08-10T00:00:00Z' }});
    await createCoursesGateway({ get } as never).activities('senai-go', '42', 1, 20, false, true);
    expect(get).toHaveBeenCalledWith('/api/courses/senai-go/42/activities?page=1&pageSize=20&refresh=true', { admissionFamily: 'course_details' });
  });

  it('loads all course pages for the catalog', async () => {
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: '1' }, { courseId: '2' }], meta: { page: 1, pageSize: 2, returned: 2, total: 3, hasMore: true, generatedAt: '2026-08-10T00:00:00Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '3' }], meta: { page: 2, pageSize: 2, returned: 1, total: 3, hasMore: false, generatedAt: '2026-08-10T00:00:01Z' } });

    const response = await createCoursesGateway({ get } as never).listAll('fieg', 2);

    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3']);
    expect(response.meta.returned).toBe(3);
    expect(get).toHaveBeenNthCalledWith(1, '/api/courses?connectionRef=fieg&page=1&pageSize=2', { admissionFamily: 'course_catalog' });
    expect(get).toHaveBeenNthCalledWith(2, '/api/courses?connectionRef=fieg&page=2&pageSize=2', { admissionFamily: 'course_catalog' });
  });

  it.each([
    { total: 2_500, pageCount: 25 },
    { total: 10_000, pageCount: 100 },
  ])('loads a synthetic $total-course catalog across $pageCount pages within bounded concurrency', async ({ total, pageCount }) => {
    let inFlight = 0;
    let maxInFlight = 0;
    const get = vi.fn(async (url: string) => {
      const page = Number(new URLSearchParams(url.split('?')[1]).get('page'));
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
      await new Promise((resolve) => setTimeout(resolve, 1));
      inFlight -= 1;

      const firstCourse = (page - 1) * 100;
      const returned = Math.min(100, total - firstCourse);
      return {
        data: Array.from({ length: returned }, (_, index) => ({ courseId: String(firstCourse + index + 1) })),
        meta: {
          page,
          pageSize: 100,
          returned,
          total,
          hasMore: page < pageCount,
          snapshotAt: 'synthetic-snapshot',
          generatedAt: '2026-10-07T00:00:00Z',
        },
      };
    });

    const response = await createCoursesGateway({ get } as never).listAll('synthetic', 100, { pageConcurrency: 4 });

    expect(get).toHaveBeenCalledTimes(pageCount);
    expect(response.data).toHaveLength(total);
    expect(response.data[0].courseId).toBe('1');
    expect(response.data.at(-1)?.courseId).toBe(String(total));
    expect(response.meta.complete).toBe(true);
    expect(maxInFlight).toBeGreaterThan(1);
    expect(maxInFlight).toBeLessThanOrEqual(4);
  });

  it('stops at the reported total when the last page is full', async () => {
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: '1' }, { courseId: '2' }], meta: { page: 1, pageSize: 2, returned: 2, total: 4, hasMore: true, generatedAt: '2026-08-10T00:00:00Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '3' }, { courseId: '4' }], meta: { page: 2, pageSize: 2, returned: 2, total: 4, hasMore: false, generatedAt: '2026-08-10T00:00:01Z' } })
      .mockRejectedValueOnce(new Error('unexpected empty page request'));

    const response = await createCoursesGateway({ get } as never).listAll('fieg', 2);

    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3', '4']);
    expect(get).toHaveBeenCalledTimes(2);
  });

  it('fetches known pages concurrently within the configured limit and reports each page', async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    const get = vi.fn(async (url: string) => {
      const page = Number(new URLSearchParams(url.split('?')[1]).get('page'));
      inFlight += 1;
      maxInFlight = Math.max(maxInFlight, inFlight);
      await new Promise((resolve) => setTimeout(resolve, page === 1 ? 0 : 5));
      inFlight -= 1;
      return {
        data: [{ courseId: String(page) }],
        meta: { page, pageSize: 1, returned: 1, total: 6, hasMore: page < 6, generatedAt: '2026-08-10T00:00:00Z' },
      };
    });
    const onPage = vi.fn();

    const response = await createCoursesGateway({ get } as never).listAll('fieg', 1, { pageConcurrency: 2, onPage });

    expect(maxInFlight).toBe(2);
    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3', '4', '5', '6']);
    expect(onPage).toHaveBeenCalledTimes(6);
  });

  it('retries only missing pages when a partial catalog is loaded again', async () => {
    const cachedPages = new Map();
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: '1' }], meta: { page: 1, pageSize: 1, returned: 1, total: 3, hasMore: true, snapshotAt: 'v1', generatedAt: '2026-08-10T00:00:00Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '2' }], meta: { page: 2, pageSize: 1, returned: 1, total: 3, hasMore: true, snapshotAt: 'v1', generatedAt: '2026-08-10T00:00:00Z' } })
      .mockRejectedValueOnce(new Error('temporary page error'))
      .mockResolvedValueOnce({ data: [{ courseId: '1' }], meta: { page: 1, pageSize: 1, returned: 1, total: 3, hasMore: true, snapshotAt: 'v1', generatedAt: '2026-08-10T00:00:01Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '3' }], meta: { page: 3, pageSize: 1, returned: 1, total: 3, hasMore: false, snapshotAt: 'v1', generatedAt: '2026-08-10T00:00:01Z' } });
    const gateway = createCoursesGateway({ get } as never);
    const onPage = (response: { meta: { page: number } }) => cachedPages.set(response.meta.page, response);

    await expect(gateway.listAll('fieg', 1, { cachedPages, onPage })).rejects.toThrow('temporary page error');
    const response = await gateway.listAll('fieg', 1, { cachedPages, onPage });

    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3']);
    expect(get).toHaveBeenCalledTimes(5);
  });

  it('continues when a full page has no hasMore flag', async () => {
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: '1' }, { courseId: '2' }], meta: { page: 1, pageSize: 2, returned: 2, total: 3, hasMore: false, generatedAt: '2026-08-10T00:00:00Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '3' }], meta: { page: 2, pageSize: 2, returned: 1, total: 3, hasMore: false, generatedAt: '2026-08-10T00:00:01Z' } });

    const response = await createCoursesGateway({ get } as never).listAll('fieg', 2);

    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3']);
    expect(get).toHaveBeenNthCalledWith(2, '/api/courses?connectionRef=fieg&page=2&pageSize=2', { admissionFamily: 'course_catalog' });
  });

  it('loads all courses for a school category', async () => {
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: '1' }, { courseId: '2' }], meta: { page: 1, pageSize: 2, returned: 2, total: 3, hasMore: true, generatedAt: '2026-08-10T00:00:00Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: '3' }], meta: { page: 2, pageSize: 2, returned: 1, total: 3, hasMore: false, generatedAt: '2026-08-10T00:00:01Z' } });

    const response = await createCoursesGateway({ get } as never).listAllByCategory('Escola A', 'fieg', 2);

    expect(response.data.map((course) => course.courseId)).toEqual(['1', '2', '3']);
    expect(get).toHaveBeenNthCalledWith(1, '/api/schools/courses?categoryPath=Escola+A&connectionRef=fieg&page=1&pageSize=2', { admissionFamily: 'course_catalog' });
    expect(get).toHaveBeenNthCalledWith(2, '/api/schools/courses?categoryPath=Escola+A&connectionRef=fieg&page=2&pageSize=2', { admissionFamily: 'course_catalog' });
  });

  it('does not reuse cached pages when the snapshot version changes', async () => {
    const cachedPages = new Map();
    cachedPages.set(2, {
      data: [{ courseId: 'old-page' }],
      meta: { page: 2, pageSize: 1, returned: 1, total: 2, hasMore: false, snapshotAt: 'v1', generatedAt: '2026-08-10T00:00:00Z' },
    });
    const get = vi.fn()
      .mockResolvedValueOnce({ data: [{ courseId: 'new-first-page' }], meta: { page: 1, pageSize: 1, returned: 1, total: 2, hasMore: true, snapshotAt: 'v2', generatedAt: '2026-08-10T00:00:01Z' } })
      .mockResolvedValueOnce({ data: [{ courseId: 'new-second-page' }], meta: { page: 2, pageSize: 1, returned: 1, total: 2, hasMore: false, snapshotAt: 'v2', generatedAt: '2026-08-10T00:00:01Z' } });

    const response = await createCoursesGateway({ get } as never).listAll('fieg', 1, { cachedPages });

    expect(response.data.map((course) => course.courseId)).toEqual(['new-first-page', 'new-second-page']);
    expect(get).toHaveBeenCalledTimes(2);
  });
});
