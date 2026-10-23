import { createAppClient } from '../../integrations/http/api-client';

export type MoodleConnection = {
  connectionId?: string;
  connectionRef: string;
  alias: string;
  host: string;
  status: 'active' | 'inactive' | 'unknown' | string;
  isDefault: boolean;
  capabilities: string[];
  lastValidatedAt?: string;
};

export type ConnectionsResponse = {
  data: MoodleConnection[];
  meta: {
    generatedAt: string;
  };
};

export const connectionsGateway = {
  list: (): Promise<ConnectionsResponse> =>
    createAppClient().get<ConnectionsResponse>('/api/connections', { admissionFamily: 'connection_session' }),
  connect: async (input: { moodleAlias: string; moodleBaseUrl: string; moodleUsername: string; moodlePassword: string; isDefault: boolean; canWrite: boolean }) => {
    return createAppClient().request<{ error?: string }>('/api/connections', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  },
  validate: async (connectionId: string) =>
    createAppClient().request<{ status: string; lastValidatedAt?: string }>(`/api/connections/${encodeURIComponent(connectionId)}/validate`, { method: 'POST' }),
  update: async (connectionId: string, input: { moodleAlias: string; moodleBaseUrl: string; moodleUsername?: string; moodlePassword?: string; isDefault: boolean; canWrite: boolean }) =>
    createAppClient().request<MoodleConnection>(`/api/connections/${encodeURIComponent(connectionId)}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }),
  dataSummary: async (connectionId: string) => createAppClient().get<{ memories: number; documents: number; moodleUserLinks: number; auditLogsRetained: number }>(`/api/connections/${encodeURIComponent(connectionId)}/data-summary`, { admissionFamily: 'connection_status' }),
  remove: async (connectionId: string, deleteLinkedData: boolean, confirmationText?: string) => createAppClient().request<{ ok: boolean }>(`/api/connections/${encodeURIComponent(connectionId)}`, { method: 'DELETE', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ deleteLinkedData, confirmationText }) }),
};
