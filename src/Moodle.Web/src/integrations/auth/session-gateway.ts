import { createAppClient, setAdmissionIdentityScope } from '../http/api-client';

export type AppSession = { data: { authenticated: boolean; reportTemplatesEnabled?: boolean; user?: { id: string; name: string; roles: string[]; permissions: string[] } }; meta: { generatedAt: string; connectionRef?: string } };
export const sessionGateway = {
  getSession: async () => {
    const session = await createAppClient().get<AppSession>('/api/session', { admissionFamily: 'session_control' });
    setAdmissionIdentityScope(session.data.authenticated ? session.data.user?.id : undefined);
    return session;
  },
};
