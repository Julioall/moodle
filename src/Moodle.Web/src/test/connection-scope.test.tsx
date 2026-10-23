import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor, act } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useConnectionScope } from '../features/connections/useConnectionScope';
import { connectionsGateway } from '../features/connections/connections-gateway';

vi.mock('../features/connections/connections-gateway', () => ({
  connectionsGateway: { list: vi.fn() },
}));

const connections = [
  { connectionRef: 'goias', alias: 'Goiás', host: 'https://goias.example', status: 'active', isDefault: true, capabilities: ['read'] },
  { connectionRef: 'senai', alias: 'SENAI', host: 'https://senai.example', status: 'active', isDefault: false, capabilities: ['read'] },
];

function wrapperFor(entry: string) {
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MemoryRouter initialEntries={[entry]}>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

describe('useConnectionScope', () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.mocked(connectionsGateway.list).mockResolvedValue({
      data: connections,
      meta: { generatedAt: '2026-08-21T00:00:00Z' },
    });
  });

  it('promotes a deep-link selection to application scope across pages', async () => {
    const dashboard = renderHook(() => useConnectionScope(), {
      wrapper: wrapperFor('/?connectionRef=goias'),
    });

    await waitFor(() => expect(dashboard.result.current.connectionRef).toBe('goias'));

    const myCourses = renderHook(() => useConnectionScope(), {
      wrapper: wrapperFor('/meus-cursos'),
    });
    await waitFor(() => expect(myCourses.result.current.connectionRef).toBe('goias'));

    act(() => dashboard.result.current.selectConnection('senai'));
    await waitFor(() => expect(myCourses.result.current.connectionRef).toBe('senai'));
    expect(window.localStorage.getItem('app:selected-connection')).toBe('senai');
  });
});
