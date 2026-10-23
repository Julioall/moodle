import {
  createAppClient,
  type AppClient,
} from "../../integrations/http/api-client";
export type TaskStatus =
  "todo" | "in_progress" | "blocked" | "done" | "cancelled";
export type TaskPriority = "low" | "medium" | "high" | "urgent";
export type PlannerReferenceType = "course" | "student" | "class" | "school";
export type PlannerReference = {
  id?: string;
  referenceType: PlannerReferenceType | string;
  referenceId: string;
  referenceName?: string;
  connectionRef?: string;
  relation?: string;
  parentReferenceType?: PlannerReferenceType;
  parentReferenceId?: string;
  parentReferenceName?: string;
};
export type TaskParticipant = {
  userId: string;
  role: "owner" | "collaborator" | "watcher";
  assignedAt: string;
};
export type TaskProgress = { done: number; total: number; percent: number };
export type TaskEventLink = {
  id: string;
  taskId: string;
  eventId: string;
  occurrenceStartAt?: string;
  relation: string;
  createdAt: string;
};
export type Task = {
  id: string;
  title: string;
  description?: string;
  status: TaskStatus;
  priority: TaskPriority;
  startAt?: string;
  dueAt?: string;
  completedAt?: string;
  parentTaskId?: string;
  createdAt: string;
  updatedAt: string;
  references?: PlannerReference[];
  tags?: string[];
  participants?: TaskParticipant[];
  owner?: TaskParticipant;
  subtasks?: Task[];
  subtaskProgress?: TaskProgress;
  dependsOn?: string[];
  blocks?: string[];
  events?: TaskEventLink[];
  version?: number;
  actionType?: string;
  scheduleHint?: string;
};
export type TaskInput = {
  title?: string;
  description?: string;
  status?: TaskStatus;
  priority?: TaskPriority;
  startAt?: string;
  dueAt?: string;
  parentTaskId?: string;
  participants?: { userId: string; role: TaskParticipant["role"] }[];
  references?: PlannerReference[];
  tags?: string[];
  actionType?: string;
  scheduleHint?: string;
  expectedVersion?: number;
  clearStartAt?: boolean;
  clearDueAt?: boolean;
  subtasks?: {
    title: string;
    description?: string;
    priority?: TaskPriority;
    dueAt?: string;
    ownerId?: string;
  }[];
  dependsOnTaskIds?: string[];
};
export type TaskList = {
  data: Task[];
  meta: {
    page: number;
    pageSize: number;
    returned: number;
    total?: number;
    hasMore: boolean;
    generatedAt: string;
  };
};
export type TaskResponse = { data: Task; meta: { generatedAt: string } };
export type TaskTimeline = {
  data: {
    comments: {
      id: string;
      authorId: string;
      content: string;
      createdAt: string;
      editedAt?: string;
    }[];
    activities: {
      id: string;
      actorId: string;
      eventType: string;
      data?: string;
      createdAt: string;
    }[];
    page: number;
    pageSize: number;
    hasMore: boolean;
  };
  meta: { generatedAt: string };
};
export const createTasksGateway = (client: AppClient = createAppClient()) => ({
  list: (
    page = 1,
    pageSize = 24,
    status?: TaskStatus,
    priority?: TaskPriority,
    search?: string,
    participantId?: string,
    tag?: string,
    referenceType?: string,
    referenceId?: string,
  ) => {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
    });
    if (status) params.set("status", status);
    if (priority) params.set("priority", priority);
    if (search) params.set("search", search);
    if (participantId) params.set("participantId", participantId);
    if (tag) params.set("tag", tag);
    if (referenceType) params.set("referenceType", referenceType);
    if (referenceId) params.set("referenceId", referenceId);
    return client.get<TaskList>(`/api/tasks?${params.toString()}`, { admissionFamily: 'tasks' });
  },
  detail: (id: string) =>
    client.get<TaskResponse>(`/api/tasks/${encodeURIComponent(id)}`, { admissionFamily: 'tasks' }),
  create: (input: TaskInput) =>
    client.request<TaskResponse>("/api/tasks", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    }),
  update: (id: string, input: TaskInput) =>
    client.request<TaskResponse>(`/api/tasks/${encodeURIComponent(id)}`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    }),
  complete: (id: string, expectedVersion?: number) =>
    client.request<TaskResponse>(
      `/api/tasks/${encodeURIComponent(id)}/complete${expectedVersion ? `?expectedVersion=${expectedVersion}` : ""}`,
      { method: "POST" },
    ),
  reopen: (id: string, expectedVersion?: number) =>
    client.request<TaskResponse>(
      `/api/tasks/${encodeURIComponent(id)}/reopen${expectedVersion ? `?expectedVersion=${expectedVersion}` : ""}`,
      { method: "POST" },
    ),
  activity: (id: string, page = 1, pageSize = 30) =>
    client.get<TaskTimeline>(
      `/api/tasks/${encodeURIComponent(id)}/activity?page=${page}&pageSize=${pageSize}`,
      { admissionFamily: 'tasks' },
    ),
  addComment: (id: string, content: string) =>
    client.request<void>(`/api/tasks/${encodeURIComponent(id)}/comments`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ content }),
    }),
  linkEvent: (taskId: string, eventId: string, occurrenceStartAt?: string) =>
    client.request<{ data: TaskEventLink }>(
      `/api/tasks/${encodeURIComponent(taskId)}/events`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          eventId,
          occurrenceStartAt,
          mode: "link",
          relation: "related",
        }),
      },
    ),
  subtasks: (id: string) =>
    client.get<{ data: Task[]; meta: { generatedAt: string } }>(
      `/api/tasks/${encodeURIComponent(id)}/subtasks`,
      { admissionFamily: 'tasks' },
    ),
  createSubtask: (id: string, input: TaskInput) =>
    client.request<TaskResponse>(
      `/api/tasks/${encodeURIComponent(id)}/subtasks`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      },
    ),
  addTag: (id: string, value: string) =>
    client.request<void>(`/api/tasks/${encodeURIComponent(id)}/tags`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ value }),
    }),
  removeTag: (id: string, value: string) =>
    client.request<void>(
      `/api/tasks/${encodeURIComponent(id)}/tags/${encodeURIComponent(value)}`,
      { method: "DELETE" },
    ),
  remove: (id: string) =>
    client.request<void>(`/api/tasks/${encodeURIComponent(id)}`, {
      method: "DELETE",
    }),
  removeMany: (ids: string[]) =>
    client.request<{
      data: { requested: number; deleted: number };
      meta: { generatedAt: string };
    }>("/api/tasks", {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids }),
    }),
});
export const tasksGateway = createTasksGateway();
