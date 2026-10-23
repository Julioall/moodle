import { describe, expect, it, vi } from 'vitest';
import { createStudentsGateway } from '../features/students/students-gateway';

describe('Students gateway', () => {
  it('lists students with pagination and a composed Moodle identity', async () => {
    const client = { get: vi.fn().mockResolvedValue({ data: [], meta: { page: 2, pageSize: 20, hasMore: false, generatedAt: '2026-08-10T00:00:00Z' } }), request: vi.fn() };
    const gateway = createStudentsGateway(client);

    await gateway.byCourse('senai-goias', 'course-1', 2);

    expect(client.get).toHaveBeenCalledWith('/api/courses/senai-goias/course-1/students?page=2&pageSize=20', { admissionFamily: 'course_details' });
  });

  it('loads a read-only student profile using connectionRef and studentId', async () => {
    const client = { get: vi.fn().mockResolvedValue({ data: { connectionRef: 'senai-goias', studentId: '42', courses: [], risk: 'normal' }, meta: { generatedAt: '2026-08-10T00:00:00Z' } }), request: vi.fn() };
    const gateway = createStudentsGateway(client);

    const response = await gateway.get('senai/goias', 'course-1', 'student/42');

    expect(client.get).toHaveBeenCalledWith('/api/courses/senai%2Fgoias/course-1/students/student%2F42', { admissionFamily: 'course_details' });
    expect(response.data).toMatchObject({ connectionRef: 'senai-goias', studentId: '42', risk: 'normal' });
  });
});
