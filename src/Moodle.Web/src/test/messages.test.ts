import { describe, expect, it, vi } from 'vitest';

import { createMoodleMessagingGateway } from '../features/messages/moodle-messaging-gateway';

describe('Moodle messaging gateway', () => {
  it('keeps the selected Moodle in conversation reads', async () => {
    const get = vi.fn().mockResolvedValue({ data: { contractVersion: 1, currentMoodleUserId: 10, items: [] } });
    const gateway = createMoodleMessagingGateway({ get, request: vi.fn() });

    await gateway.conversations('campus-a');

    expect(get).toHaveBeenCalledWith('/api/messages/conversations?connectionRef=campus-a', { admissionFamily: 'messages' });
  });

  it('prepares a direct message instead of sending outside the approval flow', async () => {
    const request = vi.fn().mockResolvedValue({ data: { messageType: 'MoodleDirect', confirmationText: 'CONFIRMAR ENVIO MENSAGEM MOODLE 1 DESTINATÁRIO', pendingActionId: 'pending-1' } });
    const gateway = createMoodleMessagingGateway({ get: vi.fn(), request });

    await gateway.prepareDirect(42, 'Olá Moodle', 'campus-a');

    expect(request).toHaveBeenCalledWith('/api/messages/conversations/42/prepare?connectionRef=campus-a', expect.objectContaining({ method: 'POST', body: JSON.stringify({ message: 'Olá Moodle' }) }));
  });

  it('confirms a prepared message through the existing pending-action endpoint', async () => {
    const request = vi.fn().mockResolvedValue({ data: { status: 'sent', pendingActionId: 'pending-1', sentCount: 1, failedCount: 0, warnings: [] } });
    const gateway = createMoodleMessagingGateway({ get: vi.fn(), request });

    await gateway.confirm('pending-1', 'CONFIRMAR ENVIO MENSAGEM MOODLE 1 DESTINATÁRIO');

    expect(request).toHaveBeenCalledWith('/api/messages/confirm', expect.objectContaining({ method: 'POST', body: JSON.stringify({ pendingActionId: 'pending-1', confirmationText: 'CONFIRMAR ENVIO MENSAGEM MOODLE 1 DESTINATÁRIO' }) }));
  });
});
