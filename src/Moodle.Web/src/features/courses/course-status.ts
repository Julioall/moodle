import type { Course } from './courses-gateway';

export type CourseLifecycle = 'in_progress' | 'not_started' | 'finished';
export type CourseLifecycleFilter = CourseLifecycle | 'all';
export type CourseLifecycleSelection = CourseLifecycleFilter | readonly CourseLifecycle[];

function parseDate(value?: string) {
  if (!value) return undefined;
  const timestamp = Date.parse(value);
  return Number.isNaN(timestamp) ? undefined : timestamp;
}

function normalizeCategoryPath(value?: string | null) {
  return value?.split('>').map((part) => part.trim()).filter(Boolean).join(' > ').toLocaleLowerCase('pt-BR') ?? '';
}

export function getCourseLifecycle(course: Pick<Course, 'startDate' | 'endDate'>, now = Date.now()): CourseLifecycle {
  const start = course.startDate ? new Date(course.startDate).getTime() : undefined;
  const end = course.endDate ? new Date(course.endDate).getTime() : undefined;
  if (end && end < now) return 'finished';
  if (start && start > now) return 'not_started';
  return 'in_progress';
}

export function filterCoursesByLifecycle<T extends Pick<Course, 'startDate' | 'endDate'>>(courses: T[], filter: CourseLifecycleSelection): T[] {
  const selected = typeof filter === 'string'
    ? filter === 'all' ? [] : [filter]
    : filter;
  if (selected.length === 0) return courses;

  const selectedStatuses = new Set(selected);
  return courses.filter((course) => selectedStatuses.has(getCourseLifecycle(course)));
}

export function matchesCourseSearch(course: Pick<Course, 'courseId' | 'fullName' | 'shortName' | 'displayName' | 'categoryName'>, search: string): boolean {
  const term = search.trim().toLocaleLowerCase('pt-BR');
  if (!term) return true;
  return [course.courseId, course.fullName, course.shortName, course.displayName, course.categoryName]
    .filter(Boolean)
    .some((value) => value!.toLocaleLowerCase('pt-BR').includes(term));
}

export function normalizeCourseEndDatesBySequence<T extends Pick<Course, 'courseId' | 'categoryName' | 'startDate' | 'endDate'>>(courses: T[]): T[] {
  const groups = new Map<string, T[]>();
  courses.forEach((course) => {
    const category = normalizeCategoryPath(course.categoryName);
    if (!category) return;
    const group = groups.get(category) ?? [];
    group.push(course);
    groups.set(category, group);
  });

  const adjustedEndDates = new Map<T, string>();
  groups.forEach((group) => {
    if (group.length < 2) return;
    const endTimes = group.map((course) => parseDate(course.endDate));
    const definedEndTimes = endTimes.filter((time): time is number => time !== undefined);
    if (definedEndTimes.length < 2) return;

    // Moodle pode devolver mais de uma sequência dentro da mesma turma
    // (por exemplo, módulos configurados com finais diferentes). Cada
    // sequência deve ser inferida separadamente.
    const distinctEndTimes = [...new Set(definedEndTimes)];
    const sequences = distinctEndTimes.length === 1
      ? [group]
      : distinctEndTimes.map((endTime) => group.filter((course) => parseDate(course.endDate) === endTime));

    sequences.forEach((sequence) => {
      const startsByTime = new Map<number, string>();
      sequence.forEach((course) => {
        const startTime = parseDate(course.startDate);
        if (startTime !== undefined && !startsByTime.has(startTime)) startsByTime.set(startTime, course.startDate!);
      });
      const starts = [...startsByTime.keys()].sort((left, right) => left - right);
      if (starts.length < 2) return;

      sequence.forEach((course) => {
        const startTime = parseDate(course.startDate);
        if (startTime === undefined) return;
        const nextStart = starts.find((candidate) => candidate > startTime);
        if (nextStart !== undefined) adjustedEndDates.set(course, startsByTime.get(nextStart)!);
      });
    });
  });

  return courses.map((course) => {
    const endDate = adjustedEndDates.get(course);
    return endDate ? { ...course, endDate } : course;
  });
}
