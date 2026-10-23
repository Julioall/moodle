import { createAppClient, type AppClient } from '../../integrations/http/api-client';
import type { ReportCoverage, ReportDefinition } from './templates/report-templates-gateway';

export type ReportType = 'grades' | 'weekly' | 'overview' | 'completion' | 'template';

export type ReportCourse = {
  name: string;
  categoryName?: string;
};

export type ReportJob = {
  id: string;
  reportType: ReportType;
  scopeType: 'category' | 'course' | 'courses';
  connectionRef: string;
  categoryPath?: string;
  courseId?: string;
  courseIds?: string[];
  status: 'queued' | 'running' | 'completed' | 'failed';
  progressPercent: number;
  totalCourses: number;
  processedCourses: number;
  fileName?: string;
  contentType?: string;
  fileSizeBytes: number;
  errorMessage?: string;
  requestedAt: string;
  startedAt?: string;
  completedAt?: string;
  updatedAt: string;
  downloadUrl?: string;
  courses?: ReportCourse[];
  templateId?: string | null;
  templateNameSnapshot?: string | null;
  templateRevisionSnapshot?: number | null;
  resultMetadata?: { coverage: ReportCoverage[]; warnings: string[]; partial: boolean; truncated: boolean; collectionStartedAt: string; collectionEndedAt: string; generatedAt: string } | null;
};

export type ReportJobDetails = ReportJob & {
  definitionSnapshot?: ReportDefinition | null;
  selectionSnapshot?: { schemaVersion: number; connectionId: string; connectionRef: string; courseIds: string[] } | null;
};
export type ReportHistoryFilters = { search?: string; status?: ReportJob['status'] | ''; connectionRef?: string };

export type ReportJobsResponse = {
  data: ReportJob[];
  meta: {
    page: number;
    pageSize: number;
    returned: number;
    total?: number | null;
    hasMore: boolean;
    generatedAt: string;
    storageUsedBytes: number;
    storageLimitBytes: number;
    storageAvailableBytes: number;
  };
};

export type CreateReportJobInput = {
  reportType: 'grades';
  scopeType: ReportJob['scopeType'];
  connectionRef: string;
  categoryPath?: string;
  courseId?: string;
  courseIds?: string[];
};

export type RefreshReportJobResponse = { id: string; status: string };

export const createReportsGateway = (client: AppClient = createAppClient()) => ({
  jobs: (page = 1, pageSize = 20, filters: ReportHistoryFilters = {}, signal?: AbortSignal) => client.get<ReportJobsResponse>(`/api/reports/jobs?${new URLSearchParams({ page: String(page), pageSize: String(pageSize), ...(filters.search ? { search: filters.search } : {}), ...(filters.status ? { status: filters.status } : {}), ...(filters.connectionRef ? { connectionRef: filters.connectionRef } : {}) })}`, { ...(signal ? { signal } : {}), admissionFamily: 'reports' }),
  getJob: (id: string, signal?: AbortSignal) => client.get<ReportJobDetails>(`/api/reports/jobs/${encodeURIComponent(id)}`, { ...(signal ? { signal } : {}), admissionFamily: 'reports' }),
  refreshJob: (id: string) => client.request<RefreshReportJobResponse>('/api/reports/jobs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshFromJobId: id }),
  }),
  deleteJob: (id: string) => client.request<void>(`/api/reports/jobs/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  createJob: async (input: CreateReportJobInput) => client.request<ReportJob>('/api/reports/jobs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  }),
});

export const reportsGateway = createReportsGateway();
