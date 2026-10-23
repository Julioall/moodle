import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { retryAwarePollingInterval } from '../../integrations/http/api-client';
import { coursesGateway, type ListResponse, type Course } from './courses-gateway';
import { CourseCatalogPageCache } from './course-catalog-page-cache';

export const courseCatalogQueryKey = (ownerId?: string, connectionId?: string) =>
  ['app', 'course-catalog', 'all-pages', ownerId ?? 'anonymous', connectionId ?? 'unselected'] as const;

export function useCourseCatalog(
  ownerId: string | undefined,
  connectionId: string | undefined,
  connectionRef: string | undefined,
  enabled: boolean,
) {
  const queryClient = useQueryClient();
  const queryKey = courseCatalogQueryKey(ownerId, connectionId);
  const scope = ownerId && connectionId ? `${ownerId}:${connectionId}` : undefined;
  const cacheRef = useRef<{ scope?: string; cache: CourseCatalogPageCache }>({ cache: new CourseCatalogPageCache() });

  if (cacheRef.current?.scope !== scope) {
    const cache = new CourseCatalogPageCache();
    const existing = queryClient.getQueryData<ListResponse<Course>>(queryKey);
    if (existing) cache.seed(existing);
    cacheRef.current = { scope, cache };
  }
  const pageCache = cacheRef.current.cache;

  return useQuery({
    queryKey,
    queryFn: async ({ signal }) => {
      if (!connectionRef) throw new Error('A conexão do catálogo não está disponível.');
      const loadedPages = new Map<number, ListResponse<Course>>();
      let firstPageMeta: ListResponse<Course>['meta'] | undefined;
      return coursesGateway.listAll(connectionRef, 100, {
        signal,
        cachedPages: pageCache.pages,
        onPage: (page) => {
          if (page.meta.page === 1) firstPageMeta = page.meta;
          else firstPageMeta ??= page.meta;
          pageCache.remember(page);
          if (page.meta.snapshotAt !== firstPageMeta.snapshotAt) return;
          loadedPages.set(page.meta.page, page);
          const data = [...loadedPages.entries()]
            .sort(([left], [right]) => left - right)
            .flatMap(([, response]) => response.data);
          queryClient.setQueryData<ListResponse<Course>>(queryKey, {
            data,
            meta: {
              ...firstPageMeta,
              returned: data.length,
              hasMore: true,
              complete: false,
            },
          });
        },
      });
    },
    enabled: Boolean(enabled && ownerId && connectionId && connectionRef),
    staleTime: 60_000,
    refetchInterval: (query) => query.state.data?.meta.complete === false || query.state.data?.meta.refreshQueued
      ? retryAwarePollingInterval(query.state.error, 15_000)
      : false,
  });
}
