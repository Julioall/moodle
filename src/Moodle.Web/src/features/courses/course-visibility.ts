import { useEffect, useRef, useState } from 'react';
import { courseVisibilityGateway, type UpdateIgnoredCoursesInput } from './course-visibility-gateway';

const IGNORED_COURSES_KEY = 'app:ignored-courses';
type StoredIgnoredCourses = Record<string, string[]>;

function readStoredIgnoredCourses(): StoredIgnoredCourses {
  if (typeof window === 'undefined') return {};
  try {
    const value = JSON.parse(window.localStorage.getItem(IGNORED_COURSES_KEY) ?? '{}') as unknown;
    if (!value || typeof value !== 'object' || Array.isArray(value)) return {};
    return Object.fromEntries(Object.entries(value).filter(([, ids]) => Array.isArray(ids) && ids.every((id) => typeof id === 'string')));
  } catch {
    return {};
  }
}

function readLegacyIgnoredCourseIds(connectionRef?: string) {
  if (!connectionRef) return new Set<string>();
  return new Set(readStoredIgnoredCourses()[connectionRef] ?? []);
}

function clearLegacyIgnoredCourseIds(connectionRef: string) {
  if (typeof window === 'undefined') return;
  const stored = readStoredIgnoredCourses();
  delete stored[connectionRef];
  if (Object.keys(stored).length === 0) window.localStorage.removeItem(IGNORED_COURSES_KEY);
  else window.localStorage.setItem(IGNORED_COURSES_KEY, JSON.stringify(stored));
}

type CourseVisibilityGateway = typeof courseVisibilityGateway;

function applyPendingUpdates(ids: Set<string>, pendingUpdates: Map<string, boolean>) {
  pendingUpdates.forEach((ignored, courseId) => {
    if (ignored) ids.add(courseId);
    else ids.delete(courseId);
  });
  return ids;
}

export function useIgnoredCourses(connectionRef?: string, gateway: CourseVisibilityGateway = courseVisibilityGateway) {
  const [ignoredCourseIds, setIgnoredCourseIds] = useState(() => readLegacyIgnoredCourseIds(connectionRef));
  const [isLoaded, setIsLoaded] = useState(!connectionRef);
  const [pendingCount, setPendingCount] = useState(0);
  const pendingUpdates = useRef(new Map<string, boolean>());
  const revision = useRef(0);

  useEffect(() => {
    let cancelled = false;
    const loadRevision = revision.current;
    pendingUpdates.current.clear();
    setIsLoaded(!connectionRef);
    setIgnoredCourseIds(readLegacyIgnoredCourseIds(connectionRef));
    if (!connectionRef) return () => { cancelled = true; };

    const load = async () => {
      try {
        const response = await gateway.listIgnored(connectionRef);
        if (cancelled) return;
        const merged = new Set(response.data);
        const legacyIds = readLegacyIgnoredCourseIds(connectionRef);
        const legacyOnlyIds = [...legacyIds].filter((courseId) => !merged.has(courseId));
        if (legacyOnlyIds.length > 0) {
          await gateway.updateIgnored({ connectionRef, courseIds: legacyOnlyIds, ignored: true });
          legacyOnlyIds.forEach((courseId) => merged.add(courseId));
        }
        if (cancelled || revision.current !== loadRevision) return;
        clearLegacyIgnoredCourseIds(connectionRef);
        setIgnoredCourseIds(applyPendingUpdates(merged, pendingUpdates.current));
        setIsLoaded(true);
      } catch {
        // Keep the legacy value visible if the preference API is temporarily unavailable.
        // The next mount retries the synchronization without losing local settings.
        setIsLoaded(true);
      }
    };

    void load();
    return () => { cancelled = true; };
  }, [connectionRef, gateway]);

  async function updateIgnoredCourses(courseIds: string[], ignored: boolean, propagateError = false) {
    if (!connectionRef || courseIds.length === 0) return;
    const normalizedIds = [...new Set(courseIds.map((courseId) => courseId.trim()).filter(Boolean))];
    if (normalizedIds.length === 0) return;
    const updateRevision = ++revision.current;
    normalizedIds.forEach((courseId) => pendingUpdates.current.set(courseId, ignored));
    setIgnoredCourseIds((current) => {
      const next = new Set(current);
      normalizedIds.forEach((courseId) => {
        if (ignored) next.add(courseId);
        else next.delete(courseId);
      });
      return next;
    });
    const input: UpdateIgnoredCoursesInput = { connectionRef, courseIds: normalizedIds, ignored };
    setPendingCount((current) => current + normalizedIds.length);
    try {
      await gateway.updateIgnored(input);
      normalizedIds.forEach((courseId) => {
        if (pendingUpdates.current.get(courseId) === ignored) pendingUpdates.current.delete(courseId);
      });
    } catch (error) {
      normalizedIds.forEach((courseId) => {
        if (pendingUpdates.current.get(courseId) === ignored) pendingUpdates.current.delete(courseId);
      });
      try {
        const response = await gateway.listIgnored(connectionRef);
        if (revision.current === updateRevision) setIgnoredCourseIds(applyPendingUpdates(new Set(response.data), pendingUpdates.current));
      } catch { /* keep the optimistic state until the next refresh */ }
      if (propagateError) throw error;
    } finally {
      setPendingCount((current) => Math.max(0, current - normalizedIds.length));
    }
  }

  async function replaceIgnoredCourses(allCourseIds: string[], keptCourseIds: Iterable<string>) {
    const kept = new Set(keptCourseIds);
    await updateIgnoredCourses(allCourseIds.filter((courseId) => !kept.has(courseId)), true, true);
    await updateIgnoredCourses(allCourseIds.filter((courseId) => kept.has(courseId)), false, true);
  }

  return {
    ignoredCourseIds,
    isLoading: !isLoaded,
    isSaving: pendingCount > 0,
    ignoreCourse: (courseId: string) => { void updateIgnoredCourses([courseId], true); },
    ignoreCourses: (courseIds: string[]) => { void updateIgnoredCourses(courseIds, true); },
    restoreCourse: (courseId: string) => { void updateIgnoredCourses([courseId], false); },
    replaceIgnoredCourses,
  };
}
