import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useIgnoredCourses } from '../features/courses/course-visibility';
import type { UpdateIgnoredCoursesInput } from '../features/courses/course-visibility-gateway';

afterEach(() => window.localStorage.clear());

describe('course visibility preferences', () => {
  function createGateway(initialIds: string[] = []) {
    const ids = new Set(initialIds);
    return {
      listIgnored: vi.fn(async () => ({ data: [...ids], meta: { generatedAt: '2026-08-15T00:00:00Z', connectionRef: 'fieg' } })),
      updateIgnored: vi.fn(async (input: UpdateIgnoredCoursesInput) => {
        input.courseIds.forEach((courseId) => input.ignored ? ids.add(courseId) : ids.delete(courseId));
      }),
    };
  }

  it('persists ignored courses per Moodle connection and restores them', async () => {
    const gateway = createGateway();
    const { result } = renderHook(() => useIgnoredCourses('fieg', gateway));
    await waitFor(() => expect(gateway.listIgnored).toHaveBeenCalledTimes(1));

    act(() => result.current.ignoreCourse('30585'));
    expect(result.current.ignoredCourseIds.has('30585')).toBe(true);

    act(() => result.current.ignoreCourses(['30586', '30587']));
    expect([...result.current.ignoredCourseIds]).toEqual(['30585', '30586', '30587']);

    const secondHook = renderHook(() => useIgnoredCourses('fieg', gateway));
    await waitFor(() => expect(secondHook.result.current.ignoredCourseIds.has('30585')).toBe(true));
    expect(secondHook.result.current.ignoredCourseIds.has('30585')).toBe(true);

    act(() => secondHook.result.current.restoreCourse('30585'));
    expect(secondHook.result.current.ignoredCourseIds.has('30585')).toBe(false);
    const restoredHook = renderHook(() => useIgnoredCourses('fieg', gateway));
    await waitFor(() => expect(restoredHook.result.current.ignoredCourseIds.has('30585')).toBe(false));
    expect(restoredHook.result.current.ignoredCourseIds.has('30585')).toBe(false);
  });

  it('does not share ignored courses between connections', async () => {
    const fiegGateway = createGateway();
    const senaiGateway = createGateway();
    const { result: fieg } = renderHook(() => useIgnoredCourses('fieg', fiegGateway));
    const { result: senai } = renderHook(() => useIgnoredCourses('senai', senaiGateway));
    await waitFor(() => expect(fiegGateway.listIgnored).toHaveBeenCalledTimes(1));

    act(() => fieg.current.ignoreCourse('30585'));

    expect(fieg.current.ignoredCourseIds.has('30585')).toBe(true);
    expect(senai.current.ignoredCourseIds.has('30585')).toBe(false);
  });

  it('migrates an existing browser preference to the backend once', async () => {
    window.localStorage.setItem('app:ignored-courses', JSON.stringify({ fieg: ['30585'] }));
    const gateway = createGateway();
    const { result } = renderHook(() => useIgnoredCourses('fieg', gateway));

    await waitFor(() => expect(gateway.updateIgnored).toHaveBeenCalledWith({ connectionRef: 'fieg', courseIds: ['30585'], ignored: true }));
    expect(result.current.ignoredCourseIds.has('30585')).toBe(true);
    expect(window.localStorage.getItem('app:ignored-courses')).toBeNull();
  });
});
