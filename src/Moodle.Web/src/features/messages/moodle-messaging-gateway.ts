import { createAppClient, type AppClient } from '../../integrations/http/api-client';

export type MoodleConversation = {
  id: number;
  member: { id: number; fullName: string; profileImageUrl?: string | null };
  lastMessage?: { text: string; createdAtUnix: number } | null;
  unreadCount: number;
  studentId?: string | null;
};

export type MoodleMessage = {
  id: string;
  text: string;
  createdAtUnix: number;
  senderMoodleUserId: number;
  senderType: 'student' | 'tutor';
};

type ConversationsResponse = {
  data: {
    contractVersion: number;
    currentMoodleUserId: number;
    items: MoodleConversation[];
  };
};

type MessagesResponse = {
  data: {
    contractVersion: number;
    conversationId: number | null;
    currentMoodleUserId: number;
    items: MoodleMessage[];
  };
};

export type MessagePreview = {
  messageType: string;
  courseId: string;
  recipientCount: number;
  recipients: { studentId: string; fullName: string }[];
  messageText: string;
  selectionCriteria: string;
  confirmationText: string;
  expiresAt: string;
  risks: string[];
  pendingActionId?: string;
};

export type MessageResult = {
  status: string;
  pendingActionId: string;
  sentCount: number;
  failedCount: number;
  warnings: string[];
};

export const createMoodleMessagingGateway = (client: AppClient = createAppClient()) => ({
  conversations: (connectionRef?: string) => {
    const params = new URLSearchParams();
    if (connectionRef) params.set('connectionRef', connectionRef);
    return client.get<ConversationsResponse>(`/api/messages/conversations${params.size ? `?${params}` : ''}`, { admissionFamily: 'messages' });
  },
  messages: (moodleUserId: number, connectionRef?: string, limit = 50) => {
    const params = new URLSearchParams({ limit: String(limit) });
    if (connectionRef) params.set('connectionRef', connectionRef);
    return client.get<MessagesResponse>(`/api/messages/conversations/${encodeURIComponent(String(moodleUserId))}?${params}`, { admissionFamily: 'messages' });
  },
  prepareDirect: (moodleUserId: number, message: string, connectionRef?: string) => {
    const params = new URLSearchParams();
    if (connectionRef) params.set('connectionRef', connectionRef);
    return client.request<{ data: MessagePreview }>(`/api/messages/conversations/${encodeURIComponent(String(moodleUserId))}/prepare${params.size ? `?${params}` : ''}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message }),
    });
  },
  confirm: (pendingActionId: string, confirmationText: string) => client.request<{ data: MessageResult }>('/api/messages/confirm', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pendingActionId, confirmationText }),
  }),
});

export const moodleMessagingGateway = createMoodleMessagingGateway();
