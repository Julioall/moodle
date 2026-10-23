import { createAppClient } from '../../integrations/http/api-client';

export type StudentGrade = { itemId: string; name: string; grade?: number; maximum?: number; percentage?: number; feedback?: string; readOnly: boolean };
export type StudentCourse = { connectionRef: string; courseId: string; name: string; url?: string; enrollmentStatus: string; progress?: number; lastCourseAccessAt?: string; grades: StudentGrade[] };
export type Student = { studentRef: { connectionRef: string; studentId: string }; connectionRef: string; studentId: string; name: string; email?: string; suspended?: boolean; firstAccessAt?: string; lastAccessAt?: string; lastCourseAccessAt?: string; risk: string; riskFactors: string[]; courses: StudentCourse[]; moodleUrl?: string; pendingCount?: number };
export type StudentList = { data: Student[]; meta: { page: number; pageSize: number; returned: number; total?: number; hasMore: boolean; generatedAt: string; connectionRef?: string; warnings?: string[]; source?: string; snapshotAt?: string; ageSeconds?: number; stale?: boolean; refreshQueued?: boolean; complete?: boolean } };
export type StudentResponse = { data: Student; meta: { generatedAt: string; connectionRef?: string } };

export const createStudentsGateway = (client = createAppClient()) => ({
  byCourse: (connectionRef: string, courseId: string, page = 1, pageSize = 20, includePending = false, refresh = false) => client.get<StudentList>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/students?page=${page}&pageSize=${pageSize}${includePending ? '&includePending=true' : ''}${refresh ? '&refresh=true' : ''}`, { admissionFamily: 'course_details' }),
  get: (connectionRef: string, courseId: string, studentId: string) => client.get<StudentResponse>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/students/${encodeURIComponent(studentId)}`, { admissionFamily: 'course_details' }),
});
export const studentsGateway = createStudentsGateway();
