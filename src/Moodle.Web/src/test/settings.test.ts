import { describe, expect, it, vi } from 'vitest';
import { accessGateway } from '../features/settings/access-gateway';

describe('access gateway', () => {
  it('delegates CSRF protection to the shared app client when updating a permission group', async () => {
    const client = {
      get: vi.fn().mockResolvedValue({ token: 'fresh-token' }),
      request: vi.fn().mockResolvedValue({ group: { id: 'group-1' } }),
    };

    await accessGateway(client as never).updateGroup('group/1', {
      name: 'Tutor',
      description: 'Acompanhamento acadêmico',
      permissions: ['courses.view'],
    });

    expect(client.get).not.toHaveBeenCalled();
    expect(client.request).toHaveBeenCalledWith('/api/permission-groups/group%2F1', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Tutor', description: 'Acompanhamento acadêmico', permissions: ['courses.view'] }),
    });
  });
});
