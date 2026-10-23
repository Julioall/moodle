import { useState } from 'react';
import { KeyRound, Plug, UserRound } from 'lucide-react';
import { useSearchParams } from 'react-router-dom';

import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { useSession } from '../auth/useSession';
import { ThemeCard } from './ThemeCard';
import { APP_PERMISSIONS } from '../../lib/access-control';
import { ConnectionsPage } from '../connections/ConnectionsPage';
import { ChangePasswordCard } from './PasswordCards';

export function SettingsPage() {
  const { user, can } = useSession();
  const [searchParams] = useSearchParams();
  const requestedTab = searchParams.get('tab');
  const [activeTab, setActiveTab] = useState(['conexoes', 'senha'].includes(requestedTab ?? '') ? requestedTab! : 'geral');
  const initials = user?.name?.split(' ').filter(Boolean).slice(0, 2).map((part) => part[0]).join('').toUpperCase() || 'CL';
  const canManageConnections = can(APP_PERMISSIONS.SERVICES_VIEW);

  return (
    <main className="space-y-6 animate-fade-in" aria-labelledby="settings-title">
      <header className="page-heading"><div><p className="eyebrow">ADMINISTRAÇÃO</p><h1 id="settings-title">Configurações</h1><p>Gerencie sua conta e conexões Moodle.</p></div></header>

      <Tabs value={activeTab} onValueChange={setActiveTab} className="space-y-5">
        <TabsList className="h-auto w-full justify-start gap-1 overflow-x-auto">
          <TabsTrigger value="geral" className="gap-2"><UserRound className="h-4 w-4" />Geral</TabsTrigger>
          <TabsTrigger value="senha" className="gap-2"><KeyRound className="h-4 w-4" />Senha</TabsTrigger>
          {canManageConnections && <TabsTrigger value="conexoes" className="gap-2"><Plug className="h-4 w-4" />Conexões Moodle</TabsTrigger>}
        </TabsList>

        <TabsContent value="geral" className="mt-0 space-y-6">
          <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(300px,0.7fr)]">
            <Card><CardHeader><CardTitle className="flex items-center gap-2 text-lg"><UserRound className="h-5 w-5" />Perfil</CardTitle><CardDescription>Informações da sua conta Moodle Conector.</CardDescription></CardHeader><CardContent><div className="flex items-center gap-4"><div className="flex h-16 w-16 items-center justify-center rounded-full bg-primary/10 text-xl font-bold text-primary">{initials}</div><div><p className="text-lg font-medium">{user?.name ?? 'Usuário Moodle Conector'}</p><p className="text-sm text-muted-foreground">Conta autenticada pelo Moodle Conector</p></div></div></CardContent></Card>
            <Card><CardHeader><CardTitle className="text-lg">Função e acesso</CardTitle><CardDescription>Permissões efetivas no portal.</CardDescription></CardHeader><CardContent><div className="space-y-3">{user?.roles?.map((item) => <div key={item} className="flex items-center justify-between border-b pb-3 last:border-0"><span className="text-sm text-muted-foreground">Perfil</span><Badge variant="secondary">{item}</Badge></div>)}<div className="flex items-center justify-between"><span className="text-sm text-muted-foreground">Permissões</span><span className="font-semibold">{user?.permissions?.length ?? 0}</span></div></div></CardContent></Card>
          </div>
          <ThemeCard />
        </TabsContent>

        <TabsContent value="senha" className="mt-0"><ChangePasswordCard /></TabsContent>

        {canManageConnections && <TabsContent value="conexoes" className="mt-0"><ConnectionsPage embedded /></TabsContent>}

      </Tabs>
    </main>
  );
}
