import { Activity, ShieldAlert, UsersRound } from 'lucide-react';
import { useSearchParams } from 'react-router-dom';

import { Badge } from '../../components/ui/badge';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { useSession } from '../auth/useSession';
import { APP_PERMISSIONS } from '../../lib/access-control';
import { AdminMetricsCard, AdminPasswordResetCard } from './PasswordCards';

export function AdministrationPage() {
  const { can } = useSession();
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = searchParams.get('tab') === 'metricas' ? 'metricas' : 'usuarios';

  if (!can(APP_PERMISSIONS.ADMIN_VIEW)) return null;

  return (
    <main className="space-y-6 animate-fade-in" aria-labelledby="administration-title">
      <header className="page-heading"><div><p className="eyebrow">ADMINISTRAÇÃO</p><h1 id="administration-title">Administração</h1><p>Gerencie usuários e acompanhe a operação da plataforma.</p></div><Badge variant="outline" className="w-fit gap-1.5"><ShieldAlert className="h-3.5 w-3.5 text-status-success" />Administrador</Badge></header>
      <Tabs value={activeTab} onValueChange={(tab) => setSearchParams({ tab })} className="space-y-5">
        <TabsList className="h-auto w-full justify-start gap-1 overflow-x-auto">
          <TabsTrigger value="usuarios" className="gap-2"><UsersRound className="h-4 w-4" />Usuários</TabsTrigger>
          <TabsTrigger value="metricas" className="gap-2"><Activity className="h-4 w-4" />Métricas</TabsTrigger>
        </TabsList>
        <TabsContent value="usuarios" className="mt-0"><AdminPasswordResetCard /></TabsContent>
        <TabsContent value="metricas" className="mt-0"><AdminMetricsCard /></TabsContent>
      </Tabs>
    </main>
  );
}
