import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { BookOpen, Building2, ChevronDown, EyeOff, Plus, Search } from 'lucide-react';

import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Button } from '@/components/ui/button';
import { useReportSelection, type ReportSelection } from '../reports/selection/useReportSelection';
import { ReportCourseCheckbox } from '../reports/selection/ReportCourseCheckbox';
import { ReportCategoryCheckbox } from '../reports/selection/ReportCategoryCheckbox';
import { ReportSelectionBar } from '../reports/selection/ReportSelectionBar';
import { useEditMode } from '@/components/layout/edit-mode-context';
import { useConnectionScope } from '../connections/useConnectionScope';
import { coursesGateway, type Course } from '../courses/courses-gateway';
import { CourseCatalogPageCache } from '../courses/course-catalog-page-cache';
import { useCourseCatalog } from '../courses/useCourseCatalog';
import { useSession } from '../auth/useSession';
import { retryAwarePollingInterval } from '../../integrations/http/api-client';
import { CourseCard } from '../courses/components/CourseCard';
import { courseOverviewFeatureQueryKey, getCourseOverviewFeatureEnabled } from '../courses/course-overview-gateway';
import { filterCoursesByLifecycle, getCourseLifecycle, matchesCourseSearch, normalizeCourseEndDatesBySequence, type CourseLifecycle, type CourseLifecycleFilter } from '../courses/course-status';
import { useIgnoredCourses } from '../courses/course-visibility';
import { useTrackedCourses } from '../courses/course-tracking';
import { buildSchoolsTree, courseCategoryPath, filterSchoolsHierarchyByCourses, groupCoursesByCategory, normalizeCategoryPath, type TreeNode } from './schools-tree';

const statusFilters: { value: CourseLifecycleFilter; label: string }[] = [
  { value: 'all', label: 'Todos' },
  { value: 'in_progress', label: 'Em andamento' },
  { value: 'not_started', label: 'Não iniciados' },
  { value: 'finished', label: 'Finalizados' },
];

function TreeBranch({ node, ownerId, connectionIdentity, connectionRef, courseGroups, onCoursesLoaded, editMode, ignoredCourseIds, trackedCourseIds, onRestore, onTrack, onUntrack, courseOverviewEnabled, selection, categoryPaths, catalogIncomplete, catalogFiltered, level = 0 }: { node: TreeNode; ownerId?: string; connectionIdentity?: string; connectionRef?: string; courseGroups: Map<string, Course[]>; onCoursesLoaded: (courses: Course[]) => void; editMode: boolean; ignoredCourseIds: Set<string>; trackedCourseIds: Set<string>; onRestore: (courseId: string) => void; onTrack: (courseId: string) => void; onUntrack: (courseId: string) => void; courseOverviewEnabled: boolean; selection: ReportSelection; categoryPaths: string[]; catalogIncomplete: boolean; catalogFiltered: boolean; level?: number }) {
  const [open, setOpen] = useState(false);
  const cacheScope = `${ownerId ?? 'anonymous'}:${connectionIdentity ?? 'unselected'}:${node.path}`;
  const pageCacheRef = useRef<{ scope: string; cache: CourseCatalogPageCache } | undefined>(undefined);
  if (pageCacheRef.current?.scope !== cacheScope) pageCacheRef.current = { scope: cacheScope, cache: new CourseCatalogPageCache() };
  const pageCache = pageCacheRef.current.cache;
  const categoryQueryKey = ['app', 'school-courses', ownerId, connectionIdentity, node.path] as const;
  const hasChildren = node.children.size > 0;
  const categoryKey = normalizeCategoryPath(node.path);
  const coursesQuery = useQuery({
    queryKey: categoryQueryKey,
    queryFn: ({ signal }) => coursesGateway.listAllByCategory(node.path, connectionRef, 100, {
      signal,
      cachedPages: pageCache.pages,
      onPage: (page) => pageCache.remember(page),
    }),
    enabled: Boolean(connectionRef && open && !hasChildren && !catalogFiltered),
    staleTime: 60_000,
    refetchInterval: (currentQuery) => currentQuery.state.data?.meta.complete === false || currentQuery.state.data?.meta.refreshQueued
      ? retryAwarePollingInterval(currentQuery.state.error, 15_000)
      : false,
  });
  useEffect(() => {
    if (coursesQuery.data?.data) onCoursesLoaded(coursesQuery.data.data);
  }, [coursesQuery.data?.data, onCoursesLoaded]);

  const categoryCourses = courseGroups.get(categoryKey) ?? [];
  const visibleIds = selection.active ? [...courseGroups.entries()].filter(([path]) => path === categoryKey || path.startsWith(`${categoryKey} > `)).flatMap(([, courses]) => courses.map((course) => course.courseId)) : [];
  const unitLabel = level === 0 ? 'curso' : level === 1 ? 'turma' : 'disciplina';
  const courseCount = !hasChildren && coursesQuery.data ? categoryCourses.length : node.count;

  return (
    <details className={`${level === 0 ? 'rounded-lg border bg-card' : 'border-l pl-4'} group`} open={open} onToggle={(event) => setOpen(event.currentTarget.open)}>
      <summary className="flex cursor-pointer list-none items-center gap-3 px-4 py-3 font-medium marker:hidden hover:bg-muted/50"><div className="flex items-center gap-2 px-4">{selection.active && (catalogFiltered ? <div onClick={(event) => { event.preventDefault(); event.stopPropagation(); }}><ReportCourseCheckbox selection={selection} ids={visibleIds} label={node.path} disabled={catalogIncomplete} /></div> : <ReportCategoryCheckbox selection={selection} categoryPath={node.path} paths={categoryPaths.filter((path) => normalizeCategoryPath(path) === categoryKey || normalizeCategoryPath(path).startsWith(`${categoryKey} > `))} visibleIds={visibleIds} count={node.count} incomplete={catalogIncomplete} />)}</div><span className="flex h-8 w-8 items-center justify-center rounded-full bg-primary/10 text-primary">{level === 0 ? <Building2 className="h-4 w-4" /> : <BookOpen className="h-4 w-4" />}</span><span className="min-w-0 flex-1"><span className="block truncate">{node.name}</span><span className="mt-0.5 block text-xs font-normal text-muted-foreground">{courseCount} {unitLabel}{courseCount === 1 ? '' : 's'}</span></span><ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-open:rotate-180" /></summary>
      <div className="space-y-3 px-4 pb-4">
        {open && <>
          {[...node.children.values()].sort((left, right) => left.name.localeCompare(right.name, 'pt-BR')).map((child) => <TreeBranch key={child.path} node={child} ownerId={ownerId} connectionIdentity={connectionIdentity} connectionRef={connectionRef} courseGroups={courseGroups} onCoursesLoaded={onCoursesLoaded} editMode={editMode} ignoredCourseIds={ignoredCourseIds} trackedCourseIds={trackedCourseIds} onRestore={onRestore} onTrack={onTrack} onUntrack={onUntrack} courseOverviewEnabled={courseOverviewEnabled} selection={selection} categoryPaths={categoryPaths} catalogIncomplete={catalogIncomplete} catalogFiltered={catalogFiltered} level={level + 1} />)}
          {!hasChildren && !catalogFiltered && coursesQuery.isPending && <Skeleton className="h-40 rounded-lg" />}
          {!hasChildren && !catalogFiltered && coursesQuery.isError && <p className="p-3 text-sm text-destructive">Não foi possível carregar as unidades curriculares.</p>}
          {!hasChildren && (catalogFiltered || (!coursesQuery.isPending && !coursesQuery.isError)) && categoryCourses.length === 0 && <p className="p-3 text-sm text-muted-foreground">Nenhum curso corresponde ao filtro selecionado.</p>}
          {!hasChildren && categoryCourses.length > 0 && <div className="grid gap-4 pt-1 md:grid-cols-2 xl:grid-cols-3">{categoryCourses.map((course) => {
          const ignored = ignoredCourseIds.has(course.courseId);
          const tracked = trackedCourseIds.has(course.courseId);
          const active = getCourseLifecycle(course) === 'in_progress';
          const canAdd = ignored || (!active && !tracked);
          const action = editMode && canAdd
            ? { label: 'Adicionar aos Meus Cursos', ariaLabel: `Adicionar ${course.displayName ?? course.fullName} aos Meus Cursos`, icon: <Plus className="h-4 w-4" />, onClick: () => { if (ignored) onRestore(course.courseId); if (!active) onTrack(course.courseId); } }
            : editMode && tracked && !active
              ? { label: 'Remover dos Meus Cursos', ariaLabel: `Remover ${course.displayName ?? course.fullName} dos Meus Cursos`, icon: <EyeOff className="h-4 w-4" />, onClick: () => onUntrack(course.courseId) }
              : undefined;
          return <div key={`${course.connectionRef}:${course.courseId}`} className={selection.snapshot.ids.has(course.courseId) ? 'rounded-lg bg-primary/5 p-2' : ''}>{selection.active && <div className="mb-2"><ReportCourseCheckbox selection={selection} ids={[course.courseId]} label={course.displayName ?? course.fullName} /></div>}<CourseCard course={course} detailsHref={courseOverviewEnabled ? `/cursos/${encodeURIComponent(course.connectionRef)}/${encodeURIComponent(course.courseId)}` : undefined} action={action} /></div>;
          })}</div>}
        </>}
      </div>
    </details>
  );
}

export function SchoolsPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const { connectionRef, connections, selectedConnection } = useConnectionScope();
  const { user } = useSession();
  const connectionIdentity = selectedConnection?.connectionId ?? connectionRef;
  const courseOverviewFeature = useQuery({ queryKey: courseOverviewFeatureQueryKey, queryFn: ({ signal }) => getCourseOverviewFeatureEnabled(signal), staleTime: 60_000, retry: false });
  const { editMode: trackingEditMode, setEditMode } = useEditMode();
  const selection = useReportSelection();
  const editMode = trackingEditMode && !selection.active;
  useEffect(() => { if (trackingEditMode) selection.store.cancel(); }, [trackingEditMode, selection.store]);
  const { ignoredCourseIds, restoreCourse } = useIgnoredCourses(connectionRef);
  const { trackedCourseIds, trackCourse, untrackCourse } = useTrackedCourses(connectionRef);
  const [search, setSearch] = useState('');
  const [selectedStatuses, setSelectedStatuses] = useState<CourseLifecycle[]>([]);
  const [loadedCourses, setLoadedCourses] = useState<Course[]>([]);
  const query = useQuery({
    queryKey: ['app', 'schools', 'hierarchy', user?.id, connectionIdentity],
    queryFn: ({ signal }) => coursesGateway.hierarchy(connectionRef, signal),
    enabled: Boolean(connectionRef),
    staleTime: 60_000,
    refetchInterval: (currentQuery) => currentQuery.state.data?.meta.complete === false || currentQuery.state.data?.meta.refreshQueued
      ? retryAwarePollingInterval(currentQuery.state.error, 15_000)
      : false,
  });
  const lifecycleCatalogQuery = useCourseCatalog(user?.id, connectionIdentity, connectionRef, selectedStatuses.length > 0);
  const normalizedSearch = search.trim();
  const searchQuery = useQuery({
    queryKey: ['app', 'school-course-search', user?.id, connectionIdentity, normalizedSearch],
    queryFn: ({ signal }) => coursesGateway.search(normalizedSearch, connectionRef, 100, signal),
    enabled: Boolean(connectionRef && normalizedSearch && selectedStatuses.length === 0),
    staleTime: 60_000,
    refetchInterval: (currentQuery) => currentQuery.state.data?.meta.complete === false || currentQuery.state.data?.meta.refreshQueued
      ? retryAwarePollingInterval(currentQuery.state.error, 15_000)
      : false,
  });
  useEffect(() => {
    setLoadedCourses([]);
  }, [connectionRef]);
  const onCoursesLoaded = useCallback((courses: Course[]) => {
    setLoadedCourses((current) => {
      const byId = new Map(current.map((course) => [course.courseId, course]));
      courses.forEach((course) => byId.set(course.courseId, course));
      return [...byId.values()];
    });
  }, []);
  useEffect(() => {
    if (searchQuery.data?.data) onCoursesLoaded(searchQuery.data.data);
  }, [onCoursesLoaded, searchQuery.data?.data]);
  const visibleCourses = useMemo(() => {
    const catalogCourses = selectedStatuses.length > 0
      ? lifecycleCatalogQuery.data?.data ?? []
      : normalizedSearch ? searchQuery.data?.data ?? [] : loadedCourses;
    const normalizedCourses = normalizeCourseEndDatesBySequence(catalogCourses);
    const searchedCourses = normalizedSearch && selectedStatuses.length > 0
      ? normalizedCourses.filter((course) => matchesCourseSearch(course, normalizedSearch))
      : normalizedCourses;
    return filterCoursesByLifecycle(searchedCourses, selectedStatuses);
  }, [lifecycleCatalogQuery.data?.data, loadedCourses, normalizedSearch, searchQuery.data?.data, selectedStatuses]);
  const courseGroups = useMemo(() => groupCoursesByCategory(visibleCourses), [visibleCourses]);
  const lifecycleCatalogActive = selectedStatuses.length > 0;
  const lifecycleCatalogError = lifecycleCatalogActive && lifecycleCatalogQuery.isError;
  const catalogRefreshing = query.data?.meta.complete === false || query.data?.meta.refreshQueued === true || Boolean(!lifecycleCatalogActive && normalizedSearch && (searchQuery.isPending || searchQuery.data?.meta.complete === false || searchQuery.data?.meta.refreshQueued === true)) || Boolean(lifecycleCatalogActive && (lifecycleCatalogQuery.isPending || lifecycleCatalogQuery.data?.meta.complete === false || lifecycleCatalogQuery.data?.meta.refreshQueued === true));
  const catalogIncomplete = query.isError || query.data?.meta.complete === false || query.isFetching || (lifecycleCatalogActive && (lifecycleCatalogQuery.isPending || lifecycleCatalogQuery.isFetching || lifecycleCatalogError || lifecycleCatalogQuery.data?.meta.complete === false || lifecycleCatalogQuery.data?.meta.hasMore === true));
  const tree = useMemo(() => {
    const items = query.data?.data ?? [];
    if (selectedStatuses.length > 0) return buildSchoolsTree(filterSchoolsHierarchyByCourses(items, visibleCourses));
    if (!normalizedSearch) return buildSchoolsTree(items);
    const visibleCategoryPaths = new Set(visibleCourses.map((course) => normalizeCategoryPath(courseCategoryPath(course))));
    return buildSchoolsTree(items.filter((item) => {
      const itemPath = normalizeCategoryPath(item.path);
      return [...visibleCategoryPaths].some((categoryPath) => categoryPath === itemPath || categoryPath.startsWith(`${itemPath} > `));
    }));
  }, [normalizedSearch, query.data?.data, selectedStatuses, visibleCourses]);
  const toggleStatus = (status: CourseLifecycle) => setSelectedStatuses((current) => current.includes(status)
    ? current.filter((item) => item !== status)
    : [...current, status]);

  return (
    <main className="space-y-6 animate-fade-in" aria-labelledby="schools-title">
      <div className="flex justify-end">{selection.available && selection.templatesEnabled && !selection.active && <Button onClick={() => { setEditMode(false); selection.store.start(); }}>Gerar relatório</Button>}</div><header className="page-heading"><div><p className="eyebrow">CATÁLOGO</p><h1 id="schools-title">Escolas</h1><p>Cursos em andamento já aparecem em Meus Cursos. Use o modo de edição para adicionar outras turmas ao acompanhamento.</p></div></header>
      <div className="flex flex-col gap-3 rounded-lg border bg-card p-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex flex-wrap gap-2" role="group" aria-label="Filtros de status dos cursos nas escolas">
          <button type="button" aria-pressed={selectedStatuses.length === 0} onClick={() => setSelectedStatuses([])} className={`rounded-full border px-3 py-1.5 text-sm transition-colors ${selectedStatuses.length === 0 ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-card text-muted-foreground hover:border-primary/40 hover:text-foreground'}`}>Todos</button>
          {statusFilters.slice(1).map((filter) => <button key={filter.value} type="button" aria-pressed={selectedStatuses.includes(filter.value as CourseLifecycle)} onClick={() => toggleStatus(filter.value as CourseLifecycle)} className={`rounded-full border px-3 py-1.5 text-sm transition-colors ${selectedStatuses.includes(filter.value as CourseLifecycle) ? 'border-primary bg-primary text-primary-foreground' : 'border-border bg-card text-muted-foreground hover:border-primary/40 hover:text-foreground'}`}>{filter.label}</button>)}
        </div>
        <div className="relative w-full sm:w-72"><Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" /><Input type="search" className="pl-9" placeholder="Buscar escola, curso ou turma" aria-label="Buscar escolas" value={search} onChange={(event) => setSearch(event.target.value)} /></div>
      </div>
      {(connections.isPending || query.isPending) && <div className="space-y-3"><Skeleton className="h-16 rounded-lg" /><Skeleton className="h-16 rounded-lg" /></div>}
      {query.isError && <Card><CardContent className="p-6"><p role="alert">Não foi possível carregar as categorias.</p></CardContent></Card>}
      {catalogRefreshing && <p className="text-sm text-muted-foreground" role="status">Preparando cursos e categorias do Moodle…</p>}
      {lifecycleCatalogError && <p className="text-sm text-destructive" role="alert">Não foi possível carregar os cursos para aplicar o filtro de status. Tente novamente.</p>}
      {query.data?.meta.warnings?.map((warning) => <p key={warning} className="text-sm text-muted-foreground" role="status">{warning}</p>)}
      {query.isSuccess && !catalogRefreshing && !lifecycleCatalogError && tree.children.size === 0 && <Card><CardContent className="flex flex-col items-center gap-2 p-12 text-center"><Building2 className="h-10 w-10 text-muted-foreground/50" /><h2 className="font-medium">{lifecycleCatalogActive || normalizedSearch ? 'Nenhum curso corresponde ao filtro selecionado' : 'Nenhuma categoria encontrada'}</h2></CardContent></Card>}
      {query.isSuccess && tree.children.size > 0 && <div className="space-y-3">{[...tree.children.values()].map((node) => <TreeBranch key={node.path} node={node} ownerId={user?.id} connectionIdentity={connectionIdentity} connectionRef={connectionRef} courseGroups={courseGroups} onCoursesLoaded={onCoursesLoaded} editMode={editMode} ignoredCourseIds={ignoredCourseIds} trackedCourseIds={trackedCourseIds} onRestore={restoreCourse} onTrack={trackCourse} onUntrack={untrackCourse} courseOverviewEnabled={courseOverviewFeature.data === true} selection={selection} categoryPaths={query.data.data.map((item) => item.path)} catalogIncomplete={catalogIncomplete} catalogFiltered={lifecycleCatalogActive} />)}</div>}
      <ReportSelectionBar selection={selection} incomplete={catalogIncomplete} onAdvance={() => navigate('/relatorios/gerar', { state: { returnTo: `${location.pathname}${location.search}` } })} />
    </main>
  );
}
