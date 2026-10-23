import {
  BookOpen,
  Building2,
  FileBarChart2,
  LogOut,
  Settings,
  type LucideIcon,
} from 'lucide-react';
import { NavLink } from '@/components/NavLink';
import { Button } from '@/components/ui/button';
import { MoodleLogo } from '@/components/ui/moodle-logo';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  useSidebar,
} from '@/components/ui/sidebar';
import { useSession } from '@/features/auth/useSession';
import { useConnectionScope } from '@/features/connections/useConnectionScope';
import { APP_PERMISSIONS } from '@/lib/access-control';

type SidebarNavItem = {
  title: string;
  url: string;
  icon: LucideIcon;
  permission: string;
};

const mainNavItems: SidebarNavItem[] = [
  { title: 'Meus Cursos', url: '/meus-cursos', icon: BookOpen, permission: APP_PERMISSIONS.COURSES_CATALOG_VIEW },
  { title: 'Escolas', url: '/escolas', icon: Building2, permission: APP_PERMISSIONS.SCHOOLS_VIEW },
  { title: 'Relatórios', url: '/relatorios', icon: FileBarChart2, permission: 'reports.view' },
];

const settingsItems: SidebarNavItem[] = [
  { title: 'Configurações', url: '/configuracoes', icon: Settings, permission: APP_PERMISSIONS.SETTINGS_VIEW },
];

export function AppSidebar() {
  const { user, logout, can } = useSession();
  const { connectionRef } = useConnectionScope();
  const { state, isMobile } = useSidebar();
  const isCollapsed = state === 'collapsed';
  const scopedUrl = (url: string) => {
    if (!connectionRef || url === '/configuracoes') return url;
    return `${url}${url.includes('?') ? '&' : '?'}connectionRef=${encodeURIComponent(connectionRef)}`;
  };

  const renderGroup = (label: string | undefined, items: SidebarNavItem[]) => {
    const visibleItems = user ? items.filter((item) => can(item.permission)) : items.filter((item) => item.permission !== 'reports.view');
    if (visibleItems.length === 0) return null;

    return (
      <SidebarGroup>
        {label && <SidebarGroupLabel className="px-3 text-xs uppercase tracking-wider text-sidebar-foreground/50">
          {!isCollapsed && label}
        </SidebarGroupLabel>}
        <SidebarGroupContent>
          <SidebarMenu>
            {visibleItems.map((item) => (
              <SidebarMenuItem key={item.title}>
                <SidebarMenuButton asChild tooltip={item.title}>
                  <NavLink
                    to={scopedUrl(item.url)}
                    end={item.url === '/'}
                    className="flex items-center gap-3 rounded-md px-3 py-2 text-sm text-sidebar-foreground/80 transition-colors hover:bg-sidebar-accent hover:text-sidebar-foreground"
                    activeClassName="sidebar-nav-active text-sidebar-primary font-medium"
                  >
                    <item.icon className="h-4 w-4 shrink-0" />
                    {!isCollapsed && <span>{item.title}</span>}
                  </NavLink>
                </SidebarMenuButton>
              </SidebarMenuItem>
            ))}
          </SidebarMenu>
        </SidebarGroupContent>
      </SidebarGroup>
    );
  };

  return (
    <Sidebar collapsible="icon" className="border-r border-sidebar-border">
      <SidebarHeader className="h-14 gap-0 p-0">
        <div className="flex h-full w-full items-center justify-center">
          {isMobile && (
            <div className="relative h-14 w-[144px] overflow-hidden">
              <MoodleLogo className="absolute left-0 top-1/2 h-auto w-full -translate-y-1/2" />
            </div>
          )}
        </div>
      </SidebarHeader>

      <SidebarContent className="border-t border-border">
        {renderGroup('Menu Principal', mainNavItems)}
      </SidebarContent>

      <SidebarFooter className="gap-0 border-t border-border p-0">
        {renderGroup(undefined, settingsItems)}
        {user && (
          <div className="flex items-center gap-3 border-t border-sidebar-border p-4">
            <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-sidebar-accent text-sm font-medium uppercase text-sidebar-foreground">
              {user.name.charAt(0)}
            </div>
            {!isCollapsed && (
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm font-medium text-sidebar-foreground">{user.name}</p>
              </div>
            )}
            <Button
              variant="ghost"
              size="icon"
              onClick={logout}
              className="h-8 w-8 shrink-0 text-sidebar-foreground/60 hover:bg-sidebar-accent hover:text-sidebar-foreground"
              title="Sair"
            >
              <LogOut className="h-4 w-4" />
            </Button>
          </div>
        )}
      </SidebarFooter>
    </Sidebar>
  );
}
