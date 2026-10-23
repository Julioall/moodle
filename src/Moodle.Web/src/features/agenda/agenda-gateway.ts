import {
  createAppClient,
  type AppClient,
} from "../../integrations/http/api-client";
import type { PlannerReference } from "../tasks/tasks-gateway";
export type AgendaEvent = {
  id: string;
  title: string;
  description?: string;
  startAt: string;
  endAt?: string;
  occurrenceStartAt?: string;
  type: string;
  timeZoneId?: string;
  location?: string;
  availabilityStatus?: "free" | "busy" | "tentative";
  isAllDay?: boolean;
  source?: string;
  externalUid?: string;
  rRule?: string;
  tags?: string[];
  references?: PlannerReference[];
  version?: number;
  createdAt: string;
  updatedAt: string;
};
export type AgendaInput = {
  title: string;
  description?: string;
  startAt: string;
  endAt?: string;
  type?: string;
  timeZoneId?: string;
  location?: string;
  availabilityStatus?: "free" | "busy" | "tentative";
  isAllDay?: boolean;
  tags?: string[];
  references?: PlannerReference[];
  recurrence?: { rRule?: string; exDates?: string[]; rDates?: string[] };
  expectedVersion?: number;
  clearEndAt?: boolean;
};
export type AgendaResponse = {
  data: AgendaEvent[];
  meta: { generatedAt: string };
};
export type PlannerImportResult = {
  imported: number;
  updated: number;
  skipped: number;
  warnings: string[];
};
export type AgendaOccurrence = AgendaEvent & {
  occurrenceStartAt: string;
  occurrenceEndAt?: string;
  isCancelled: boolean;
};
export type AgendaLink = {
  id: string;
  taskId: string;
  eventId: string;
  occurrenceStartAt?: string;
  relation: string;
  createdAt: string;
};
export const createAgendaGateway = (client: AppClient = createAppClient()) => ({
  list: (
    from?: string,
    to?: string,
    tag?: string,
    taskId?: string,
    referenceType?: string,
    referenceId?: string,
  ) => {
    const params = new URLSearchParams();
    if (from) params.set("from", from);
    if (to) params.set("to", to);
    if (tag) params.set("tag", tag);
    if (taskId) params.set("taskId", taskId);
    if (referenceType) params.set("referenceType", referenceType);
    if (referenceId) params.set("referenceId", referenceId);
    return client.get<AgendaResponse>(
      `/api/agenda${params.size ? `?${params.toString()}` : ""}`,
      { admissionFamily: 'agenda' },
    );
  },
  detail: (id: string) =>
    client.get<{ data: AgendaEvent; meta: { generatedAt: string } }>(
      `/api/agenda/${encodeURIComponent(id)}`,
      { admissionFamily: 'agenda' },
    ),
  occurrences: (
    from: string,
    to: string,
    tag?: string,
    taskId?: string,
    page = 1,
    pageSize = 100,
  ) =>
    client.get<{ data: AgendaOccurrence[]; meta: { generatedAt: string } }>(
      `/api/agenda/occurrences?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&page=${page}&pageSize=${pageSize}${tag ? `&tag=${encodeURIComponent(tag)}` : ""}${taskId ? `&taskId=${encodeURIComponent(taskId)}` : ""}`,
      { admissionFamily: 'agenda' },
    ),
  relatedTasks: (id: string) =>
    client.get<{ data: AgendaLink[]; meta: { generatedAt: string } }>(
      `/api/agenda/${encodeURIComponent(id)}/tasks`,
      { admissionFamily: 'agenda' },
    ),
  createTask: (id: string, input: { dueAt?: string; relation?: string }) =>
    client.request<{ data: import("../tasks/tasks-gateway").Task }>(
      `/api/agenda/${encodeURIComponent(id)}/tasks`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      },
    ),
  linkTask: (eventId: string, taskId: string, occurrenceStartAt?: string) =>
    client.request<{ data: AgendaLink }>(
      `/api/agenda/${encodeURIComponent(eventId)}/tasks`,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          mode: "link",
          taskId,
          occurrenceStartAt,
          relation: "related",
        }),
      },
    ),
  create: (input: AgendaInput) =>
    client.request<{ data: AgendaEvent }>("/api/agenda/professional", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(input),
    }),
  update: (id: string, input: AgendaInput) =>
    client.request<{ data: AgendaEvent }>(
      `/api/agenda/${encodeURIComponent(id)}/professional`,
      {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      },
    ),
  remove: (id: string) =>
    client.request<void>(`/api/agenda/${encodeURIComponent(id)}`, {
      method: "DELETE",
    }),
  updateOccurrence: (
    id: string,
    originalStartAt: string,
    input: {
      title?: string;
      description?: string;
      startAt?: string;
      endAt?: string;
    },
  ) =>
    client.request<void>(
      `/api/agenda/${encodeURIComponent(id)}/occurrences/${encodeURIComponent(originalStartAt)}`,
      {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(input),
      },
    ),
  importIcs: (file: File) => {
    const form = new FormData();
    form.append("file", file);
    return client.request<{ data: PlannerImportResult }>("/api/agenda/import", {
      method: "POST",
      body: form,
    });
  },
});
export const agendaGateway = createAgendaGateway();
