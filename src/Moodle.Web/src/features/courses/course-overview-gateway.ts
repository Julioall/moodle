import { createAppClient } from '../../integrations/http/api-client';

export const courseOverviewFeatureQueryKey = ['app', 'course-overview-feature'] as const;

export async function getCourseOverviewFeatureEnabled(signal?: AbortSignal): Promise<boolean> {
  const response = await fetch('/api/info', { credentials: 'same-origin', cache: 'no-store', signal });
  if (!response.ok) throw new Error('Não foi possível ler a configuração da tela.');
  const info = await response.json() as { courseDetailsOverviewV1Enabled?: boolean };
  return info.courseDetailsOverviewV1Enabled === true;
}

export type CourseOverviewStudent = {
  studentId: string;
  fullName: string;
  profilePath: string;
  lastCourseAccessAt?: string | null;
  lastAccessState: 'known' | 'unknown';
  grade?: { value?: number | null; max?: number | null; percentage?: number | null; state: string; reason?: string | null } | null;
};

export type CourseOverviewResponse = {
  schemaVersion: number;
  course: { courseId: string; connectionRef: string; name: string; categoryName?: string | null; startAt?: string | null; endAt?: string | null };
  summary: { observedStudentCount: number | null; totalStudentCount?: number | null; studentCountState: string; knownGradeCount?: number | null; averagePercentage?: number | null; averageGradeValue?: number | null; averageDenominator?: number | null; averageState: string };
  students: CourseOverviewStudent[];
  pagination: { page: number; pageSize: number; filteredTotal: number; hasMore: boolean; populationComplete: boolean };
  coverage: Record<string, { state: string; reasons: string[] }>;
  freshness: Record<string, { state: string; updatedAt?: string | null; staleUntil?: string | null }>;
  refresh: Record<string, { state: string; reason: string; retryAfterSeconds?: number | null }>;
  skewSeconds?: number | null;
};

const client = createAppClient();

export const createCourseOverviewGateway = (appClient = client) => ({
  get: (connectionRef: string, courseId: string, options: { query: string; page: number; pageSize: number; sort: 'name_asc' | 'name_desc'; signal?: AbortSignal }) => {
    const search = new URLSearchParams({ page: String(options.page), pageSize: String(options.pageSize), sort: options.sort });
    if (options.query) search.set('q', options.query);
    return appClient.get<CourseOverviewResponse>(
      `/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/overview?${search}`,
      { ...(options.signal ? { signal: options.signal } : {}), admissionFamily: 'course_details' },
    );
  },
});

export const courseOverviewGateway = createCourseOverviewGateway();
