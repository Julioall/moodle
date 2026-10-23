import { useEffect, useMemo, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { BookOpen, Building2, ChevronDown, EyeOff, GraduationCap, Search, UsersRound } from 'lucide-react';

import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { useEditMode } from '@/components/layout/edit-mode-context';
import { useConnectionScope } from '../connections/useConnectionScope';
import { useSession } from '../auth/useSession';
import { CourseCard } from './components/CourseCard';
import { courseOverviewFeatureQueryKey, getCourseOverviewFeatureEnabled } from './course-overview-gateway';
import { type Course } from './courses-gateway';
import { useCourseCatalog } from './useCourseCatalog';
import { getCourseLifecycle, matchesCourseSearch, normalizeCourseEndDatesBySequence } from './course-status';
import { useIgnoredCourses } from './course-visibility';
import { useTrackedCourses } from './course-tracking';
import { categoryPartsWithoutRedundantRoot, courseCategoryPath, redundantCategoryRoots } from '../schools/schools-tree';
import { useReportSelection, type ReportSelection } from '../reports/selection/useReportSelection';
import { ReportCourseCheckbox } from '../reports/selection/ReportCourseCheckbox';
import { ReportSelectionBar } from '../reports/selection/ReportSelectionBar';

type CategoryNode = { name: string; path: string; children: Map<string, CategoryNode>; courses: Course[] };

function buildCategoryTree(courses: Course[]) {
  const root: CategoryNode = { name: 'root', path: '', children: new Map(), courses: [] };
  const redundantRoots = redundantCategoryRoots(courses.map((course) => course.categoryName ?? ''));
  courses.forEach((course) => {
    const categoryParts = categoryPartsWithoutRedundantRoot(courseCategoryPath(course), redundantRoots);
    const visibleCategoryParts = categoryParts.length > 0 ? categoryParts : [courseCategoryPath(course)];
    let node = root;
    visibleCategoryParts.forEach((part) => {
      const path = node.path ? `${node.path} > ${part}` : part;
      if (!node.children.has(part)) node.children.set(part, { name: part, path, children: new Map(), courses: [] });
      node = node.children.get(part)!;
    });
    node.courses.push(course);
  });
  return root;
}

function countCourses(node: CategoryNode): number {
  return node.courses.length + [...node.children.values()].reduce((total, child) => total + countCourses(child), 0);
}

function collectCourseIds(node: CategoryNode): string[] {
  return [...node.courses.map((course) => course.courseId), ...[...node.children.values()].flatMap(collectCourseIds)];
}

function CategoryBranch({ node, editMode, onIgnore, onIgnoreCategory, courseOverviewEnabled, selection, categorySelection, catalogIncomplete, level = 0 }: { node: CategoryNode; editMode: boolean; onIgnore: (courseId: string) => void; onIgnoreCategory: (courseIds: string[]) => void; courseOverviewEnabled: boolean; selection: ReportSelection; categorySelection: Map<string, string[]>; catalogIncomplete: boolean; level?: number }) {
  const [open, setOpen] = useState(false);
  const children = [...node.children.values()].sort((left, right) => left.name.localeCompare(right.name, 'pt-BR'));
  const courseCount = countCourses(node);
  const categoryCourseIds = collectCourseIds(node);
  const hasSelectedCourse = selection.active && categoryCourseIds.some(courseId => selection.snapshot.ids.has(courseId));
  const Icon = level === 0 ? Building2 : level === 1 ? GraduationCap : UsersRound;
  const hasContent = children.length > 0 || node.courses.length > 0;

  return (
    <section className={level === 0 ? 'overflow-hidden rounded-lg border bg-card' : 'overflow-hidden rounded-lg border bg-muted/30'}>
      <div className={`flex w-full items-center gap-2 px-4 py-3 text-left transition-colors ${hasSelectedCourse ? 'bg-primary/5 hover:bg-primary/10' : 'hover:bg-muted/50'}`}>
        {selection.active && <ReportCourseCheckbox selection={selection} ids={categorySelection.get(node.path) ?? categoryCourseIds} label={node.name} disabled={catalogIncomplete} />}
        <button type="button" className="flex min-w-0 flex-1 items-center gap-3 text-left" onClick={() => setOpen((value) => !value)} aria-expanded={open}>
          <Icon className={level === 0 ? 'h-5 w-5 text-primary' : 'h-4 w-4 text-primary/80'} />
          <span className="min-w-0 flex-1"><span className="block truncate font-semibold">{node.name}</span><span className="mt-0.5 block text-xs font-normal text-muted-foreground">{courseCount} curso{courseCount === 1 ? '' : 's'}</span></span>
          {hasContent && <ChevronDown className={`h-4 w-4 shrink-0 text-muted-foreground transition-transform ${open ? 'rotate-180' : ''}`} />}
        </button>
        {editMode && categoryCourseIds.length > 0 && <Button type="button" variant="ghost" size="icon" className="h-8 w-8 shrink-0 text-muted-foreground hover:text-foreground" aria-label={`Ignorar todos os cursos de ${node.name}`} title={`Ignorar todos os cursos de ${node.name}`} onClick={() => onIgnoreCategory(categoryCourseIds)}><EyeOff className="h-4 w-4" /></Button>}
      </div>
      {open && <div className="space-y-3 border-t p-4">{children.map((child) => <CategoryBranch key={child.path} node={child} editMode={editMode} onIgnore={onIgnore} onIgnoreCategory={onIgnoreCategory} courseOverviewEnabled={courseOverviewEnabled} selection={selection} categorySelection={categorySelection} catalogIncomplete={catalogIncomplete} level={level + 1} />)}{node.courses.length > 0 && <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">{node.courses.map((course) => <div key={`${course.connectionRef}:${course.courseId}`} className={selection.snapshot.ids.has(course.courseId) ? 'rounded-lg bg-primary/5 p-2' : ''}>{selection.active && <div className="mb-2"><ReportCourseCheckbox selection={selection} ids={[course.courseId]} label={course.displayName ?? course.fullName} /></div>}<CourseCard course={course} detailsHref={courseOverviewEnabled ? `/cursos/${encodeURIComponent(course.connectionRef)}/${encodeURIComponent(course.courseId)}` : undefined} action={editMode ? { label: 'Ignorar curso', ariaLabel: `Ignorar ${course.displayName ?? course.fullName}`, icon: <EyeOff className="h-4 w-4" />, onClick: () => onIgnore(course.courseId) } : undefined} /></div>)}</div>}</div>}
    </section>
  );
}

export function MyCoursesPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const { connectionRef, connections, selectedConnection } = useConnectionScope();
  const { user } = useSession();
  const { editMode: trackingEditMode, setEditMode } = useEditMode();
  const selection = useReportSelection();
  const editMode = trackingEditMode && !selection.active;
  useEffect(() => { if (trackingEditMode) selection.store.cancel(); }, [trackingEditMode, selection.store]);
  const courseOverviewFeature = useQuery({
    queryKey: courseOverviewFeatureQueryKey,
    queryFn: ({ signal }) => getCourseOverviewFeatureEnabled(signal),
    staleTime: 60_000,
    retry: false,
  });
  const { ignoredCourseIds, ignoreCourse, ignoreCourses } = useIgnoredCourses(connectionRef);
  const { trackedCourseIds, isLoading: trackedCoursesLoading } = useTrackedCourses(connectionRef);
  const [search, setSearch] = useState('');
  const query = useCourseCatalog(
    user?.id,
    selectedConnection?.connectionId ?? connectionRef,
    connectionRef,
    Boolean(connectionRef),
  );
  const eligibleCourses = useMemo(() => {
    const allCourses = normalizeCourseEndDatesBySequence(query.data?.data ?? []);
    return allCourses
      .filter((course) => !ignoredCourseIds.has(course.courseId))
      .filter((course) => getCourseLifecycle(course) === 'in_progress' || trackedCourseIds.has(course.courseId))
;
  }, [ignoredCourseIds, query.data?.data, trackedCourseIds]);
  const visibleCourses = useMemo(() => eligibleCourses.filter((course) => matchesCourseSearch(course, search)), [eligibleCourses, search]);
  const categorySelection = useMemo(() => {
    const map = new Map<string, string[]>();
    const visit = (node: CategoryNode) => { map.set(node.path, collectCourseIds(node)); node.children.forEach(visit); };
    visit(buildCategoryTree(eligibleCourses)); return map;
  }, [eligibleCourses]);
  const tree = useMemo(() => buildCategoryTree(visibleCourses), [visibleCourses]);
  const catalogRefreshing = query.data?.meta.complete === false || query.data?.meta.refreshQueued === true;
  const scopeLoading = connections.isPending || Boolean(connectionRef && (query.isPending || trackedCoursesLoading));

  return (
    <main className="space-y-6 animate-fade-in" aria-labelledby="courses-title">
      <div className="flex justify-end">{selection.available && selection.templatesEnabled && !selection.active && <Button onClick={() => { setEditMode(false); selection.store.start(); }}>Gerar relatório</Button>}</div><header className="page-heading"><div><p className="eyebrow">OPERACIONAL</p><h1 id="courses-title">Meus Cursos</h1><p>{scopeLoading ? 'Carregando cursos…' : `${visibleCourses.length} ${visibleCourses.length === 1 ? 'curso' : 'cursos'} em acompanhamento`}</p></div><div className="relative w-full sm:w-72"><Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" /><Input type="search" placeholder="Buscar curso..." value={search} onChange={(event) => setSearch(event.target.value)} className="pl-9" /></div></header>
      {scopeLoading && <div className="space-y-3"><Skeleton className="h-16 rounded-lg" /><Skeleton className="h-16 rounded-lg" /></div>}
      {catalogRefreshing && !scopeLoading && <p className="text-sm text-muted-foreground" role="status">Atualizando categorias e cursos do Moodle…</p>}
      {query.isError && query.data && <p className="text-sm text-destructive" role="alert">O catálogo está parcial. As páginas ausentes serão tentadas novamente automaticamente.</p>}
      {query.isError && !query.data && <Card><CardContent className="p-6 text-sm text-destructive" role="alert">Não foi possível carregar os cursos.</CardContent></Card>}
      {!scopeLoading && query.data && !catalogRefreshing && tree.children.size === 0 && <Card className="border-dashed"><CardContent className="flex flex-col items-center gap-3 p-12 text-center"><BookOpen className="h-10 w-10 text-muted-foreground/40" /><h2 className="font-medium">Nenhum curso encontrado</h2><p className="text-sm text-muted-foreground">Não há cursos em acompanhamento no momento.</p></CardContent></Card>}
      {!scopeLoading && query.data && tree.children.size > 0 && <section className="space-y-4" aria-label="Cursos agrupados por categoria">{[...tree.children.values()].sort((left, right) => left.name.localeCompare(right.name, 'pt-BR')).map((node) => <CategoryBranch key={node.path} node={node} editMode={editMode} onIgnore={ignoreCourse} onIgnoreCategory={ignoreCourses} courseOverviewEnabled={courseOverviewFeature.data === true} selection={selection} categorySelection={categorySelection} catalogIncomplete={catalogRefreshing || query.isFetching || query.isError} />)}</section>}
      <ReportSelectionBar selection={selection} incomplete={catalogRefreshing || query.isFetching || query.isError} onAdvance={() => navigate('/relatorios/gerar', { state: { returnTo: `${location.pathname}${location.search}` } })} />
    </main>
  );
}
