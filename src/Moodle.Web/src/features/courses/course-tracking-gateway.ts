import { createAppClient, type AppClient } from '../../integrations/http/api-client';

export type TrackedCoursesResponse = {
  data: string[];
  meta: { generatedAt: string; connectionRef?: string };
};

export type UpdateTrackedCoursesInput = {
  connectionRef: string;
  courseIds: string[];
  tracked: boolean;
};

export const createCourseTrackingGateway = (client: AppClient = createAppClient()) => ({
  listTracked: (connectionRef: string) => client.get<TrackedCoursesResponse>(`/api/course-preferences/tracked?connectionRef=${encodeURIComponent(connectionRef)}`, { admissionFamily: 'course_preferences' }),
  updateTracked: (input: UpdateTrackedCoursesInput) => client.request('/api/course-preferences/tracked', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  }),
});

export const courseTrackingGateway = createCourseTrackingGateway();
