import { describe, expect, it } from 'vitest';
import { buildSchoolsTree, countCoursesByCategory, courseCategoryPath, filterSchoolsHierarchyByCourses, groupCoursesByCategory } from '../features/schools/schools-tree';

function node(path: string, courseCount: number) {
  const parts = path.split(' > ');
  return { path, name: parts.at(-1)!, level: parts.length - 1, courseCount };
}

describe('schools tree', () => {
  it('hides a redundant root while preserving the original lookup path', () => {
    const tree = buildSchoolsTree([
      node('Portal', 3),
      node('Portal > Escola A', 2),
      node('Portal > Escola B', 1),
    ]);

    expect([...tree.children.keys()]).toEqual(['Escola A', 'Escola B']);
    expect(tree.children.get('Escola A')?.path).toBe('Portal > Escola A');
  });

  it('keeps an independent root when another root has child categories', () => {
    const tree = buildSchoolsTree([
      node('Portal > Escola A', 2),
      node('Outro portal', 1),
    ]);

    expect([...tree.children.keys()]).toEqual(['Escola A', 'Outro portal']);
  });

  it('groups and counts only the courses that remain after a filter', () => {
    const courses = [
      { categoryName: 'Portal > Escola A > Turma 1', courseId: '1' },
      { categoryName: 'Portal > Escola A > Turma 1', courseId: '2' },
      { categoryName: 'Portal > Escola B', courseId: '3' },
    ];

    expect(groupCoursesByCategory(courses).get('portal > escola a > turma 1')).toHaveLength(2);
    expect(countCoursesByCategory(courses).get('portal > escola a')).toBe(2);
    expect(countCoursesByCategory([courses[0]]).get('portal > escola a')).toBe(1);
  });

  it('keeps only categories with matching courses and updates their counts', () => {
    const hierarchy = [node('Escola A', 2), node('Escola B', 1)];
    const filtered = filterSchoolsHierarchyByCourses(hierarchy, [
      { categoryName: 'Escola A > Turma 1', courseId: '1' },
    ]);

    expect(filtered).toEqual([{ ...node('Escola A', 2), courseCount: 1 }]);
  });

  it('distinguishes pending category metadata from an explicitly uncategorized course', () => {
    expect(courseCategoryPath({ categoryId: null, categoryName: null })).toBe('Categoria não carregada');
    expect(courseCategoryPath({ categoryId: 0, categoryName: null })).toBe('Sem categoria');
    expect(courseCategoryPath({ categoryId: 0, categoryName: 'Unexpected label' })).toBe('Sem categoria');
    expect(courseCategoryPath({ categoryId: 42, categoryName: null })).toBe('Categoria não carregada');
  });
});
