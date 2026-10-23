import { describe, expect, it } from 'vitest';
import { filterCoursesByLifecycle, getCourseLifecycle, matchesCourseSearch, normalizeCourseEndDatesBySequence } from '../features/courses/course-status';

const now = new Date('2026-08-14T12:00:00Z').getTime();

describe('course lifecycle', () => {
  it('classifies courses by their start and end dates', () => {
    expect(getCourseLifecycle({ startDate: '2026-08-01T00:00:00Z', endDate: '2026-08-31T23:59:59Z' } as never, now)).toBe('in_progress');
    expect(getCourseLifecycle({ startDate: '2026-08-15T00:00:00Z', endDate: '2026-08-31T23:59:59Z' } as never, now)).toBe('not_started');
    expect(getCourseLifecycle({ startDate: '2026-07-01T00:00:00Z', endDate: '2026-08-13T23:59:59Z' } as never, now)).toBe('finished');
  });

  it('filters courses without changing the all-courses view', () => {
    const courses = [
      { id: 'active', startDate: '2020-08-01T00:00:00Z', endDate: '2099-08-31T23:59:59Z' },
      { id: 'future', startDate: '2099-08-15T00:00:00Z', endDate: '2099-08-31T23:59:59Z' },
      { id: 'finished', startDate: '2020-07-01T00:00:00Z', endDate: '2021-08-13T23:59:59Z' },
    ];

    expect(filterCoursesByLifecycle(courses, 'in_progress').map((course) => course.id)).toEqual(['active']);
    expect(filterCoursesByLifecycle(courses, 'all')).toBe(courses);
  });

  it('combines multiple lifecycle statuses', () => {
    const courses = [
      { id: 'active', startDate: '2020-08-01T00:00:00Z', endDate: '2099-08-31T23:59:59Z' },
      { id: 'future', startDate: '2099-08-15T00:00:00Z', endDate: '2099-08-31T23:59:59Z' },
      { id: 'finished', startDate: '2020-07-01T00:00:00Z', endDate: '2021-08-13T23:59:59Z' },
    ];

    expect(filterCoursesByLifecycle(courses, ['in_progress', 'finished']).map((course) => course.id)).toEqual(['active', 'finished']);
  });

  it('uses the next distinct opening as the end of sequential units with a common module end', () => {
    const courses = [
      { courseId: 'a', categoryName: 'Turma 1', startDate: '2026-01-01T00:00:00Z', endDate: '2026-12-31T23:59:59Z' },
      { courseId: 'b', categoryName: 'Turma 1', startDate: '2026-01-01T00:00:00Z', endDate: '2026-12-31T23:59:59Z' },
      { courseId: 'c', categoryName: 'Turma 1', startDate: '2026-03-01T00:00:00Z', endDate: '2026-12-31T23:59:59Z' },
    ];

    const normalized = normalizeCourseEndDatesBySequence(courses);

    expect(normalized.map((course) => course.endDate)).toEqual([
      '2026-03-01T00:00:00Z',
      '2026-03-01T00:00:00Z',
      '2026-12-31T23:59:59Z',
    ]);
  });

  it('keeps the common end when all units open together', () => {
    const courses = [
      { courseId: 'a', categoryName: 'Turma 2', startDate: '2026-01-01T00:00:00Z', endDate: '2026-12-31T23:59:59Z' },
      { courseId: 'b', categoryName: 'Turma 2', startDate: '2026-01-01T00:00:00Z', endDate: '2026-12-31T23:59:59Z' },
    ];

    expect(normalizeCourseEndDatesBySequence(courses)).toEqual(courses);
  });

  it('normalizes independent sequences when a turma has different common ends', () => {
    const courses = [
      { courseId: 'a', categoryName: 'Turma 2B', startDate: '2026-01-01T00:00:00Z', endDate: '2026-09-30T23:59:59Z' },
      { courseId: 'b', categoryName: 'Turma 2B', startDate: '2026-03-01T00:00:00Z', endDate: '2026-09-30T23:59:59Z' },
      { courseId: 'c', categoryName: 'Turma 2B', startDate: '2026-02-01T00:00:00Z', endDate: '2026-12-15T23:59:59Z' },
      { courseId: 'd', categoryName: 'Turma 2B', startDate: '2026-04-01T00:00:00Z', endDate: '2026-12-15T23:59:59Z' },
    ];

    expect(normalizeCourseEndDatesBySequence(courses).map((course) => course.endDate)).toEqual([
      '2026-03-01T00:00:00Z',
      '2026-09-30T23:59:59Z',
      '2026-04-01T00:00:00Z',
      '2026-12-15T23:59:59Z',
    ]);
  });

  it('uses equal defined ends even when the latest unit has no end yet', () => {
    const courses = [
      { courseId: 'a', categoryName: 'Turma 3', startDate: '2026-05-20T00:00:00Z', endDate: '2026-08-25T23:59:59Z' },
      { courseId: 'b', categoryName: 'Turma 3', startDate: '2026-07-31T00:00:00Z', endDate: '2026-08-25T23:59:59Z' },
      { courseId: 'c', categoryName: 'Turma 3', startDate: '2026-08-13T00:00:00Z' },
    ];

    expect(normalizeCourseEndDatesBySequence(courses).map((course) => course.endDate)).toEqual([
      '2026-07-31T00:00:00Z',
      '2026-08-13T00:00:00Z',
      undefined,
    ]);
  });

  it('groups equivalent category paths despite inconsistent Moodle spacing', () => {
    const courses = [
      { courseId: 'a', categoryName: 'Escola > Turma 4', startDate: '2026-05-20T00:00:00Z', endDate: '2026-08-25T23:59:59Z' },
      { courseId: 'b', categoryName: 'Escola>Turma 4', startDate: '2026-07-31T00:00:00Z', endDate: '2026-08-25T23:59:59Z' },
      { courseId: 'c', categoryName: 'Escola > Turma 4', startDate: '2026-08-13T00:00:00Z' },
    ];

    expect(normalizeCourseEndDatesBySequence(courses).map((course) => course.endDate)).toEqual([
      '2026-07-31T00:00:00Z',
      '2026-08-13T00:00:00Z',
      undefined,
    ]);
  });

  it('searches courses by id, name or category', () => {
    const course = { courseId: '42', fullName: 'Projeto Docente', categoryName: 'Escola A > Turma 1' };

    expect(matchesCourseSearch(course, 'docente')).toBe(true);
    expect(matchesCourseSearch(course, 'escola a')).toBe(true);
    expect(matchesCourseSearch(course, '99')).toBe(false);
  });
});
