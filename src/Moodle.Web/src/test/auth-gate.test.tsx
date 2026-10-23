import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthGate } from '../features/auth/AuthGate';
import { sessionGateway } from '../integrations/auth/session-gateway';

vi.mock('../integrations/auth/session-gateway', () => ({
  sessionGateway: { getSession: vi.fn() },
}));

const getSession = vi.mocked(sessionGateway.getSession);
const renderWithQuery = (children: React.ReactNode) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    {children}
  </QueryClientProvider>,
);

describe('AuthGate', () => {
  beforeEach(() => { cleanup(); getSession.mockReset(); });

  it('keeps the operational shell behind the Moodle Conector auth page when anonymous', async () => {
    getSession.mockResolvedValue({ data: { authenticated: false }, meta: { generatedAt: new Date().toISOString() } });
    renderWithQuery(<AuthGate><h1>Resumo protegido</h1></AuthGate>);
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Entrar no Moodle Conector' })).toBeInTheDocument());
    expect(screen.queryByRole('heading', { name: 'Resumo protegido' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Entrar' })).toBeInTheDocument();
  });

  it('releases the shell only after an authenticated session', async () => {
    getSession.mockResolvedValue({ data: { authenticated: true, user: { id: 'u1', name: 'Tutor', roles: ['Tutor'], permissions: ['dashboard.view'] } }, meta: { generatedAt: new Date().toISOString() } });
    renderWithQuery(<AuthGate><h1>Resumo protegido</h1></AuthGate>);
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Resumo protegido' })).toBeInTheDocument());
  });
});
