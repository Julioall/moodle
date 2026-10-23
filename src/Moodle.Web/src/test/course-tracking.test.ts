import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useTrackedCourses } from '../features/courses/course-tracking';
import type { UpdateTrackedCoursesInput } from '../features/courses/course-tracking-gateway';

describe('course tracking preferences', () => {
  it('loads, adds and removes explicitly tracked courses', async () => {
    const ids = new Set(['30585']);
    const gateway = {
      listTracked: vi.fn(async () => ({ data: [...ids], meta: { generatedAt: '2026-08-21T00:00:00Z', connectionRef: 'goias' } })),
      updateTracked: vi.fn(async (input: UpdateTrackedCoursesInput) => {
        input.courseIds.forEach((courseId) => input.tracked ? ids.add(courseId) : ids.delete(courseId));
      }),
    };
    const { result } = renderHook(() => useTrackedCourses('goias', gateway));

    await waitFor(() => expect(result.current.trackedCourseIds.has('30585')).toBe(true));
    act(() => result.current.trackCourse('30586'));
    expect(result.current.trackedCourseIds.has('30586')).toBe(true);
    act(() => result.current.untrackCourse('30585'));
    expect(result.current.trackedCourseIds.has('30585')).toBe(false);
  });
});
