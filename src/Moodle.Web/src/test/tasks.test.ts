import { afterEach, describe, expect, it, vi } from 'vitest';
import { createTasksGateway } from '../features/tasks/tasks-gateway';

describe('tasks gateway', () => {
  afterEach(() => vi.restoreAllMocks());

  it('envia a data de início ao criar uma tarefa', async () => {
    const get = vi.fn().mockResolvedValue({ token: 'csrf-token' });
    const request = vi.fn().mockResolvedValue({ data: {}, meta: {} });
    const gateway = createTasksGateway({ get, request } as never);
    const input = {
      title: 'Acompanhar aluno',
      startAt: '2026-08-15T15:00:00.000Z',
      dueAt: '2026-08-22T15:00:00.000Z',
    };

    await gateway.create(input);

    expect(request).toHaveBeenCalledWith('/api/tasks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(input),
    });
  });

  it('remove varias tarefas em uma unica requisicao', async () => {
    const get = vi.fn().mockResolvedValue({ token: 'csrf-token' });
    const request = vi.fn().mockResolvedValue({ data: { requested: 2, deleted: 2 }, meta: {} });
    const gateway = createTasksGateway({ get, request } as never);
    const ids = ['11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222'];

    await gateway.removeMany(ids);

    expect(request).toHaveBeenCalledWith('/api/tasks', {
      method: 'DELETE',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ids }),
    });
  });
});
