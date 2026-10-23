import { createAppClient } from '../../integrations/http/api-client';

export type AccountResponse = {
  ok?: boolean;
  error?: string;
  hasMoodleConnected?: boolean;
  redirectUrl?: string;
};

export type MoodleBootstrapInput = {
  moodleAlias: string;
  moodleBaseUrl: string;
  moodleUsername: string;
  moodlePassword: string;
  isDefault: boolean;
  canWrite: boolean;
};

async function accountRequest(path: string, body: Record<string, unknown>): Promise<AccountResponse> {
  // Authentication can change the cookie identity. A new client requests a
  // token bound to that identity instead of reusing an anonymous one.
  return createAppClient().request<AccountResponse>(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export const authGateway = {
  login: (email: string, password: string) => accountRequest('/api/account/login', { email, password }),
  register: (name: string, email: string, password: string) => accountRequest('/api/account/register', { name, email, password }),
  connectMoodle: (input: MoodleBootstrapInput) => accountRequest('/api/account/connect-moodle', input),
};
