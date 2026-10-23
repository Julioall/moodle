import { useEffect, useState } from 'react';
import { courseTrackingGateway, type UpdateTrackedCoursesInput } from './course-tracking-gateway';

type CourseTrackingGateway = typeof courseTrackingGateway;

export function useTrackedCourses(connectionRef?: string, gateway: CourseTrackingGateway = courseTrackingGateway) {
  const [trackedCourseIds, setTrackedCourseIds] = useState<Set<string>>(new Set());
  const [isLoading, setIsLoading] = useState(Boolean(connectionRef));
  const [pendingCount, setPendingCount] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setIsLoading(Boolean(connectionRef));
    setTrackedCourseIds(new Set());
    if (!connectionRef) return () => { cancelled = true; };

    void gateway.listTracked(connectionRef).then((response) => {
      if (!cancelled) setTrackedCourseIds(new Set(response.data));
    }).catch(() => {
      // Keep the empty set and allow the next mount/connection change to retry.
    }).finally(() => {
      if (!cancelled) setIsLoading(false);
    });

    return () => { cancelled = true; };
  }, [connectionRef, gateway]);

  async function updateTrackedCourses(courseIds: string[], tracked: boolean) {
    if (!connectionRef || courseIds.length === 0) return;
    const normalizedIds = [...new Set(courseIds.map((courseId) => courseId.trim()).filter(Boolean))];
    if (normalizedIds.length === 0) return;

    setTrackedCourseIds((current) => {
      const next = new Set(current);
      normalizedIds.forEach((courseId) => tracked ? next.add(courseId) : next.delete(courseId));
      return next;
    });
    setPendingCount((current) => current + normalizedIds.length);
    const input: UpdateTrackedCoursesInput = { connectionRef, courseIds: normalizedIds, tracked };
    try {
      await gateway.updateTracked(input);
    } catch {
      try {
        const response = await gateway.listTracked(connectionRef);
        setTrackedCourseIds(new Set(response.data));
      } catch {
        // Keep the optimistic state until the next refresh if recovery also fails.
      }
    } finally {
      setPendingCount((current) => Math.max(0, current - normalizedIds.length));
    }
  }

  return {
    trackedCourseIds,
    isLoading,
    isSaving: pendingCount > 0,
    trackCourse: (courseId: string) => { void updateTrackedCourses([courseId], true); },
    untrackCourse: (courseId: string) => { void updateTrackedCourses([courseId], false); },
  };
}
