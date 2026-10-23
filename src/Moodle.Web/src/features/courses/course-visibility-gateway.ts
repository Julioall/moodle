import { createAppClient, type AppClient } from '../../integrations/http/api-client';

export type IgnoredCoursesResponse = {
  data: string[];
  meta: { generatedAt: string; connectionRef?: string };
};

export type UpdateIgnoredCoursesInput = {
  connectionRef: string;
  courseIds: string[];
  ignored: boolean;
};

export const createCourseVisibilityGateway = (client: AppClient = createAppClient()) => ({
  listIgnored: (connectionRef: string) => client.get<IgnoredCoursesResponse>(`/api/course-preferences/ignored?connectionRef=${encodeURIComponent(connectionRef)}`, { admissionFamily: 'course_preferences' }),
  updateIgnored: (input: UpdateIgnoredCoursesInput) => client.request('/api/course-preferences/ignored', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  }),
});

export const courseVisibilityGateway = createCourseVisibilityGateway();
