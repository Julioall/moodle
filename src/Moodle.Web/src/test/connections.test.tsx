import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { ConnectionsPage } from '../features/connections/ConnectionsPage';
import { connectionsGateway } from '../features/connections/connections-gateway';

vi.mock('../features/connections/connections-gateway', () => ({ connectionsGateway: { list: vi.fn() } }));

describe('ConnectionsPage', () => {
  it('renders safe multi-Moodle cards with connectionRef and freshness', async () => {
    vi.mocked(connectionsGateway.list).mockResolvedValue({ data: [{ connectionRef: 'fieg', alias: 'SENAI Goiás', host: 'https://goias.example', status: 'active', isDefault: true, capabilities: ['read', 'write'], lastValidatedAt: '2026-08-10T00:00:00Z' }], meta: { generatedAt: '2026-08-10T00:00:00Z' } });
    const client = new QueryClient();
    render(<QueryClientProvider client={client}><MemoryRouter><ConnectionsPage /></MemoryRouter></QueryClientProvider>);
    expect(await screen.findByText('SENAI Goiás')).toBeInTheDocument();
    expect(screen.getByText('fieg')).toBeInTheDocument();
    expect(screen.getByText(/Atualizado em/)).toBeInTheDocument();
  });
});
