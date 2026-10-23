import { render, screen } from '@testing-library/react'; import { MemoryRouter } from 'react-router-dom'; import { describe, expect, it } from 'vitest'; import { AppSidebar } from '../components/layout/AppSidebar';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

import { SidebarProvider } from '../components/ui/sidebar';

const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });

const renderWithProviders = (ui: React.ReactElement) => render(
  <QueryClientProvider client={queryClient}>
    <SidebarProvider>
      <MemoryRouter>{ui}</MemoryRouter>
    </SidebarProvider>
  </QueryClientProvider>
);

describe('Moodle Conector shell', () => {
  it('renders the foundation navigation', () => {
    renderWithProviders(<AppSidebar />);
    expect(screen.getByText('Meus Cursos')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Alunos' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Conexões Moodle' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Configurações' })).toBeInTheDocument();
  });

  it('hides suspended planner and administration surfaces', () => {
    renderWithProviders(<AppSidebar />);
    expect(screen.queryByRole('link', { name: 'Tarefas' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Agenda' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Administração' })).not.toBeInTheDocument();
    expect(screen.queryAllByRole('link', { name: 'Relatórios' })).toHaveLength(0);
  });
});
