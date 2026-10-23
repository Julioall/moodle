import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Bell, CheckCheck, Download, Pencil, RefreshCw, WifiOff } from 'lucide-react';

import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { ScrollArea } from '@/components/ui/scroll-area';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { SidebarTrigger, useSidebar } from '@/components/ui/sidebar';
import { Switch } from '@/components/ui/switch';
import { cn } from '@/lib/utils';
import { MoodleLogo } from '@/components/ui/moodle-logo';
import { dashboardGateway } from '@/features/dashboard/dashboard-gateway';
import { reportsGateway } from '@/features/reports/reports-gateway';
import { useConnectionScope } from '@/features/connections/useConnectionScope';
import { GlobalActivityLine } from './GlobalActivityLine';
import { useEditMode } from './edit-mode-context';
import { useSession } from '@/features/auth/useSession';
import { retryAwarePollingInterval } from '@/integrations/http/api-client';

const LAST_SEEN_KEY = 'app:notifications-last-seen';

function formatLastValidation(value?: string) {
  if (!value) return 'Nunca';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'Nunca' : date.toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' });
}

function formatActivityDate(value?: string) {
  if (!value) return 'Agora';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Agora';
  const minutes = Math.round((date.getTime() - Date.now()) / 60_000);
  if (Math.abs(minutes) < 1) return 'Agora';
  if (Math.abs(minutes) < 60) return minutes < 0 ? `há ${Math.abs(minutes)} min` : `em ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return hours < 0 ? `há ${Math.abs(hours)} h` : `em ${hours} h`;
  const days = Math.round(hours / 24);
  return days < 0 ? `há ${Math.abs(days)} dia${Math.abs(days) === 1 ? '' : 's'}` : `em ${days} dia${days === 1 ? '' : 's'}`;
}

function reportTitle(type: string) {
  return type === 'grades'
    ? 'Notas do curso'
    : type === 'weekly'
      ? 'Desempenho semanal'
      : type === 'overview'
        ? 'Visão de acompanhamento'
        : 'Conclusão provável';
}

function readLastSeen() {
  if (typeof window === 'undefined') return undefined;
  return window.localStorage.getItem(LAST_SEEN_KEY) ?? undefined;
}

export function TopBar() {
  const queryClient = useQueryClient();
  const { connections, selectedConnection, connectionRef, selectConnection } = useConnectionScope();
  const { can, user } = useSession();
  const { editMode, setEditMode } = useEditMode();
  const { isMobile: sidebarIsMobile, state: sidebarState } = useSidebar();
  const [notificationOpen, setNotificationOpen] = useState(false);
  const [lastSeenAt, setLastSeenAt] = useState(readLastSeen);
  const status = selectedConnection?.status ?? 'unknown';
  const isOffline = status === 'offline' || status === 'inactive';
  const activityQuery = useQuery({
    queryKey: ['app', 'topbar-activity', connectionRef],
    queryFn: () => dashboardGateway.get(connectionRef, undefined, true),
    enabled: notificationOpen && Boolean(connectionRef),
    staleTime: 30_000,
  });
  const reportJobsQuery = useQuery({
    queryKey: ['app', 'report-jobs-notifications', user?.id],
    queryFn: () => reportsGateway.jobs(1, 10),
    enabled: can('reports.view'),
    staleTime: 10_000,
    refetchInterval: (query) => retryAwarePollingInterval(query.state.error, 30_000),
  });
  const activities = useMemo(() => activityQuery.data?.data.recentActivity ?? [], [activityQuery.data?.data.recentActivity]);
  const notifications = useMemo(() => {
    const reportNotifications = (reportJobsQuery.data?.data ?? [])
      .filter((job) => job.status === 'completed' || job.status === 'failed')
      .map((job) => ({
        key: `report:${job.id}:${job.status}`,
        title: job.status === 'completed' ? 'Relatório disponível' : 'Falha ao gerar relatório',
        detail: `${reportTitle(job.reportType)} · ${job.scopeType === 'category' ? job.categoryPath : job.scopeType === 'courses' ? `${job.totalCourses} cursos selecionados` : `Curso ${job.courseId}`}`,
        occurredAt: job.updatedAt,
        downloadUrl: job.downloadUrl,
      }));
    return [...activities.map((activity) => ({ ...activity, downloadUrl: undefined })), ...reportNotifications].sort((left, right) => new Date(right.occurredAt ?? 0).getTime() - new Date(left.occurredAt ?? 0).getTime());
  }, [activities, reportJobsQuery.data?.data]);
  const unreadCount = useMemo(() => {
    if (notifications.length === 0) return 0;
    if (!lastSeenAt) return notifications.length;
    const lastSeen = new Date(lastSeenAt).getTime();
    if (Number.isNaN(lastSeen)) return notifications.length;
    return notifications.filter((item) => item.occurredAt && new Date(item.occurredAt).getTime() > lastSeen).length;
  }, [notifications, lastSeenAt]);
  const sidebarWidth = sidebarState === 'collapsed' ? 'var(--sidebar-width-icon)' : 'var(--sidebar-width)';
  const headerStyle = sidebarIsMobile
    ? undefined
    : {
      marginLeft: `calc(0px - ${sidebarWidth})`,
      width: `calc(100% + ${sidebarWidth})`,
    };

  const markNotificationsAsSeen = () => {
    const now = new Date().toISOString();
    setLastSeenAt(now);
    if (typeof window !== 'undefined') window.localStorage.setItem(LAST_SEEN_KEY, now);
  };

  const handleNotificationOpenChange = (open: boolean) => {
    setNotificationOpen(open);
    if (open) {
      void activityQuery.refetch();
      void reportJobsQuery.refetch();
      if (notifications.length > 0) markNotificationsAsSeen();
    }
  };

  return (
    <header style={headerStyle} className="sticky top-0 relative z-30 flex h-14 items-center border-b bg-card/95 backdrop-blur supports-[backdrop-filter]:bg-card/80">
      <div
        className={`hidden h-full shrink-0 items-center border-r border-border md:flex ${sidebarState === 'collapsed' ? 'justify-center px-2' : 'justify-start px-4'}`}
        style={{ width: sidebarWidth }}
      >
        {sidebarState === 'collapsed' ? <>
          <img src="/brand/moodle-icon.png" alt="Moodle Conector" className="h-7 w-7 dark:hidden" />
          <img src="/brand/moodle-icon-white.png" alt="Moodle Conector" className="hidden h-7 w-7 dark:block" />
        </> : (
          <div className="relative h-14 w-[144px] overflow-hidden">
            <MoodleLogo className="absolute left-0 top-1/2 h-auto w-full -translate-y-1/2" />
          </div>
        )}
      </div>

      <div className="flex min-w-0 flex-1 items-center justify-between gap-4 px-4 md:px-6">
        <div className="flex min-w-0 items-center gap-3">
          <SidebarTrigger className="md:hidden" />
          <img src="/brand/moodle-icon.png" alt="Moodle Conector" className="h-7 w-7 dark:hidden md:hidden" />
          <img src="/brand/moodle-icon-white.png" alt="Moodle Conector" className="hidden h-7 w-7 dark:block md:hidden" />
          <div className="hidden items-center gap-2 whitespace-nowrap text-xs text-muted-foreground md:flex">
            <span>Última sincronização:</span>
            <span className="font-medium">{formatLastValidation(selectedConnection?.lastValidatedAt)}</span>
          </div>
        </div>

        <div className="flex shrink-0 items-center gap-2">
        {isOffline && (
          <div className="hidden items-center gap-1.5 rounded-md border border-amber-200 bg-amber-50 px-2.5 py-1.5 text-xs text-amber-700 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-300 sm:flex">
            <WifiOff className="h-3.5 w-3.5" />
            <span>Modo Offline</span>
          </div>
        )}

        <label className="sr-only" htmlFor="global-moodle-selector">Moodle atual</label>
        <Select value={connectionRef ?? ''} onValueChange={selectConnection} disabled={connections.isPending || connections.data?.data.length === 0}>
          <SelectTrigger id="global-moodle-selector" aria-label="Selecionar Moodle" className="h-8 w-[136px] gap-1 border-transparent bg-transparent px-2 text-xs shadow-none hover:bg-muted/50 focus:ring-1 focus:ring-ring/50 focus:ring-offset-0 sm:w-[160px]">
            <SelectValue placeholder={connections.isPending ? 'CARREGANDO MOODLES…' : 'SELECIONAR MOODLE'} />
          </SelectTrigger>
          <SelectContent>
            {connections.data?.data.map((connection) => (
              <SelectItem key={connection.connectionRef} value={connection.connectionRef}>
                {connection.alias.toLocaleUpperCase('pt-BR')}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Button variant="ghost" size="icon" className="h-9 w-9" title="Atualizar conexões" aria-label="Atualizar conexões" onClick={() => { void queryClient.invalidateQueries({ queryKey: ['app', 'connections'] }); void queryClient.invalidateQueries({ queryKey: ['app', 'dashboard'] }); }}>
          <RefreshCw className={`h-4 w-4 ${connections.isFetching ? 'animate-spin' : ''}`} />
        </Button>

        <Popover open={notificationOpen} onOpenChange={handleNotificationOpenChange}>
          <PopoverTrigger asChild>
            <Button variant="ghost" size="icon" className="relative h-9 w-9" aria-label="Notificações">
              <Bell className="h-4 w-4" />
              {unreadCount > 0 && <span className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-destructive px-1 text-[10px] font-medium text-destructive-foreground">{unreadCount > 99 ? '99+' : unreadCount}</span>}
            </Button>
          </PopoverTrigger>
          <PopoverContent align="end" className="w-[340px] p-0">
            <div className="flex items-center justify-between border-b px-3 py-2">
              <div>
                <p className="text-sm font-medium">Notificações</p>
                <p className="text-xs text-muted-foreground">{notifications.length} itens recentes</p>
              </div>
              <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={markNotificationsAsSeen} disabled={notifications.length === 0}>
                <CheckCheck className="mr-1 h-3.5 w-3.5" />Marcar lidas
              </Button>
            </div>
            <ScrollArea className="max-h-[360px]">
              {activityQuery.isFetching || reportJobsQuery.isFetching ? <div className="px-3 py-4 text-xs text-muted-foreground">Carregando notificações…</div> : notifications.length === 0 ? <div className="px-3 py-4 text-xs text-muted-foreground">Nenhuma notificação no momento.</div> : <div className="divide-y">{notifications.map((item) => <div key={item.key} className="px-3 py-2.5"><p className="text-sm font-medium leading-snug">{item.title}</p><p className="mt-1 text-xs leading-relaxed text-muted-foreground">{item.detail}</p><p className="mt-1 text-[11px] text-muted-foreground">{formatActivityDate(item.occurredAt)}</p>{item.downloadUrl && <a href={item.downloadUrl} download className="mt-2 inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline"><Download className="h-3.5 w-3.5" />{item.detail.includes('cursos selecionados') ? 'Baixar arquivo' : 'Baixar Excel'}</a>}</div>)}</div>}
            </ScrollArea>
          </PopoverContent>
        </Popover>

        <div className={cn('flex items-center gap-2 border-l border-border pl-2', editMode && 'text-primary')}>
          <Switch id="edit-mode" checked={editMode} onCheckedChange={setEditMode} />
          <Label htmlFor="edit-mode" className={cn('hidden cursor-pointer text-xs sm:inline', editMode ? 'font-medium text-primary' : 'text-muted-foreground')}>
            <Pencil className="mr-1 inline h-3 w-3" />Editar
          </Label>
        </div>
        </div>
      </div>
      <GlobalActivityLine />
    </header>
  );
}
