import { createAppClient, type AppClient } from '../../integrations/http/api-client';

export type Team = { id: string; name: string; isPersonal: boolean; role: string; scopes: string[] };
export type PermissionGroup = { id: string; name: string; description: string; permissions: string[] };

export const accessGateway = (client: AppClient = createAppClient()) => ({
  teams: () => client.get<{ teams: Team[] }>('/api/teams', { admissionFamily: 'teams' }),
  groups: () => client.get<{ groups: PermissionGroup[] }>('/api/permission-groups', { admissionFamily: 'admin_api' }),
  catalog: () => client.get<{ permissions: string[] }>('/api/permission-catalog', { admissionFamily: 'admin_api' }),
  createGroup: (input: { name: string; description?: string; permissions: string[] }) =>
    client.request<{ group: PermissionGroup }>('/api/permission-groups', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input)
    }),
  updateGroup: (groupId: string, input: { name: string; description?: string; permissions: string[] }) =>
    client.request<{ group: PermissionGroup }>(`/api/permission-groups/${encodeURIComponent(groupId)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input)
    }),
  invite: (teamId: string, input: { email: string; role: string }) =>
    client.request(`/api/teams/${encodeURIComponent(teamId)}/invitations`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input)
    })
});
