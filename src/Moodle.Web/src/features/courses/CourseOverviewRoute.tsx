import { useQuery } from '@tanstack/react-query';
import { Navigate, useParams } from 'react-router-dom';

import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { CoursePanelPage } from './CoursePanelPage';
import { courseOverviewFeatureQueryKey, getCourseOverviewFeatureEnabled } from './course-overview-gateway';

export function CourseOverviewRoute() {
  const { connectionRef } = useParams();
  const feature = useQuery({
    queryKey: courseOverviewFeatureQueryKey,
    queryFn: ({ signal }) => getCourseOverviewFeatureEnabled(signal),
    staleTime: 60_000,
    refetchOnMount: 'always',
    retry: false,
  });

  if (feature.isPending || feature.isFetching) return <main className="content-frame space-y-6" aria-busy="true"><p role="status" className="sr-only">Carregando dados da turma...</p><Skeleton className="h-10 w-2/3" /><div className="grid gap-4 md:grid-cols-2"><Skeleton className="h-24" /><Skeleton className="h-24" /></div><Skeleton className="h-80" /></main>;
  if (feature.isError) return <main className="content-frame"><p role="alert">Não foi possível validar o acesso à tela.</p><Button variant="outline" className="mt-3" onClick={() => window.location.reload()}>Tentar novamente</Button></main>;
  if (!feature.data) return <Navigate to={`/meus-cursos${connectionRef ? `?connectionRef=${encodeURIComponent(connectionRef)}` : ''}`} replace />;
  return <CoursePanelPage />;
}
