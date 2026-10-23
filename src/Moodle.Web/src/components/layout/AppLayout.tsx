import { Outlet } from 'react-router-dom';
import { AppSidebar } from './AppSidebar';
import { TopBar } from './TopBar';
import { SidebarProvider } from '@/components/ui/sidebar';
import { AppFooter } from '@/components/layout/AppFooter';
import { EditModeProvider } from './edit-mode';

export function AppLayout() {
  return (
    <EditModeProvider>
      <SidebarProvider>
        <div className="min-h-screen flex w-full bg-background">
          <AppSidebar />
          <div className="flex min-h-0 flex-1 flex-col min-w-0">
            <TopBar />
            <main className="min-h-0 flex flex-1 flex-col overflow-auto">
              <div className="container min-h-0 min-w-0 flex flex-1 flex-col max-w-7xl px-4 py-6 md:px-6 lg:px-8">
                <Outlet />
              </div>
            </main>
            <AppFooter />
          </div>
        </div>
      </SidebarProvider>
    </EditModeProvider>
  );
}
