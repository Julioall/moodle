import { createAppClient, type AppClient } from '../../../integrations/http/api-client';

export type ReportFilter = { fieldId: string; operator: string; value?: string | null };
export type ReportDefinition = {
  schemaVersion: 1;
  sourceId: 'student_course';
  columns: { fieldId: string; label?: string }[];
  filters?: ReportFilter[];
  parameters?: { inactiveDaysThreshold: number; minGradePercent: number };
};
export type ReportValueType = 'Text' | 'Number' | 'DateTime' | 'Enum';
export type ReportField = { fieldId: string; label: string; group: string; type: ReportValueType; readPolicy: 'snapshot_preferred' | 'live_required'; requirements: string[]; filterable: boolean };
export type ReportCatalog = { schemaVersion: number; sourceId: string; fields: ReportField[]; limits: { maxColumns: number; maxFilters: number; maxTemplatesPerOwner: number; maxCourses: number; maxRows: number; maxAssignmentsPerCourse: number; generationTimeoutSeconds: number } };
export type ReportTemplateInput = { name: string; description?: string | null; definition: ReportDefinition };
export type ReportTemplate = ReportTemplateInput & { id: string; revision: number; createdAt: string; updatedAt: string };
export type ReportTemplatePage = { items: ReportTemplate[]; page: number; pageSize: number; total: number };
export type ReportCell = { value: string | number | null; state: 'Known' | 'Missing' | 'Unavailable'; reason?: string | null };
export type ReportCoverage = { courseId: string; family: string; fieldIds: string[]; source: string; readAt?: string | null; snapshotAt?: string | null; complete: boolean; reason?: string | null; stale?: boolean | null; frozen?: boolean | null; uncertainCount?: number | null };
export type ReportPreview = { columns: { fieldId: string; label: string; type: ReportValueType }[]; rows: { courseId: string; studentId: string; cells: ReportCell[] }[]; sampledCourseIds: string[]; sampledCourses?: { courseId: string; name?: string | null }[]; requestedCourseCount: number; returnedRows: number; truncated: boolean; partial: boolean; coverage: ReportCoverage[]; warnings: string[]; generatedAt: string; referenceAt: string };
export type ReportOrigin = { definition: ReportDefinition; templateId?: never; expectedRevision?: never }
  | { templateId: string; expectedRevision: number; definition?: never };
export type ReportPreviewInput = ReportOrigin & { connectionRef: string; courseIds: string[] };
export type ReportJobOrigin = Extract<ReportOrigin, { templateId: string }>;

export function createReportTemplatesGateway(client: AppClient = createAppClient()) {
  const path = (id: string) => `/api/reports/templates/${encodeURIComponent(id)}`;
  const jsonHeaders = { 'Content-Type': 'application/json' };
  return {
    catalog: (signal?: AbortSignal) => client.get<ReportCatalog>('/api/reports/catalog', { ...(signal ? { signal } : {}), admissionFamily: 'reports' }),
    list: (page = 1, signal?: AbortSignal) => client.get<ReportTemplatePage>(`/api/reports/templates?page=${page}&pageSize=20`, { ...(signal ? { signal } : {}), admissionFamily: 'reports' }),
    get: (id: string, signal?: AbortSignal) => client.get<ReportTemplate>(path(id), { ...(signal ? { signal } : {}), admissionFamily: 'reports' }),
    create: (input: ReportTemplateInput) => client.request<ReportTemplate>('/api/reports/templates', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(input) }),
    update: (id: string, input: ReportTemplateInput, expectedRevision: number) => client.request<ReportTemplate>(path(id), { method: 'PUT', headers: jsonHeaders, body: JSON.stringify({ ...input, expectedRevision }) }),
    delete: (id: string, expectedRevision: number) => client.request<void>(`${path(id)}?expectedRevision=${expectedRevision}`, { method: 'DELETE' }),
    preview: (input: ReportPreviewInput, signal: AbortSignal) => client.request<ReportPreview>('/api/reports/preview', { method: 'POST', headers: jsonHeaders, body: JSON.stringify(input), signal }),
    generate: (origin: ReportJobOrigin, connectionRef: string, courseIds: string[], reportType: 'template' | 'grades' = 'template') => client.request<{ id: string; status: string }>('/api/reports/jobs', { method: 'POST', headers: jsonHeaders, body: JSON.stringify({ ...origin, reportType, scopeType: 'courses', connectionRef, courseIds }) }),
  };
}
export const reportTemplatesGateway = createReportTemplatesGateway();
