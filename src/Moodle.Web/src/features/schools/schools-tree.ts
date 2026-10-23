import type { Course, CourseHierarchyNode } from '../courses/courses-gateway';

export type TreeNode = { name: string; path: string; count: number; children: Map<string, TreeNode> };

function splitPath(path: string) {
  return path.split('>').map((part) => part.trim()).filter(Boolean);
}

export function redundantCategoryRoots(paths: string[]) {
  return new Set(
    paths
      .map(splitPath)
      .filter((parts) => parts.length > 1)
      .map((parts) => parts[0].toLocaleLowerCase('pt-BR')),
  );
}

export function categoryPartsWithoutRedundantRoot(path: string, roots: Set<string>) {
  const parts = splitPath(path);
  const shouldHideRoot = parts[0] !== undefined && roots.has(parts[0].toLocaleLowerCase('pt-BR'));
  return shouldHideRoot ? parts.slice(1) : parts;
}

export function normalizeCategoryPath(path: string) {
  return splitPath(path).join(' > ').toLocaleLowerCase('pt-BR');
}

export function courseCategoryPath(course: { categoryId?: number | null; categoryName?: string | null }) {
  if (course.categoryId === 0) return 'Sem categoria';
  const parts = splitPath(course.categoryName ?? '');
  return (parts.length > 0 ? parts : ['Categoria não carregada']).join(' > ');
}

export function groupCoursesByCategory<T extends { categoryId?: number | null; categoryName?: string | null }>(courses: T[]) {
  const groups = new Map<string, T[]>();
  courses.forEach((course) => {
    const key = normalizeCategoryPath(courseCategoryPath(course));
    const group = groups.get(key) ?? [];
    group.push(course);
    groups.set(key, group);
  });
  return groups;
}

export function countCoursesByCategory<T extends Pick<Course, 'categoryName'>>(courses: T[]) {
  const counts = new Map<string, number>();
  courses.forEach((course) => {
    const parts = splitPath(courseCategoryPath(course));
    for (let length = 1; length <= parts.length; length += 1) {
      const key = normalizeCategoryPath(parts.slice(0, length).join(' > '));
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  });
  return counts;
}

export function filterSchoolsHierarchyByCourses<T extends Pick<Course, 'categoryName'>>(items: CourseHierarchyNode[], courses: T[]) {
  const counts = countCoursesByCategory(courses);
  return items.flatMap((item) => {
    const courseCount = counts.get(normalizeCategoryPath(item.path)) ?? 0;
    return courseCount > 0 ? [{ ...item, courseCount }] : [];
  });
}

export function buildSchoolsTree(items: CourseHierarchyNode[]) {
  const root: TreeNode = { name: 'root', path: '', count: 0, children: new Map() };
  const redundantRoots = redundantCategoryRoots(items.map((item) => item.path));

  for (const item of items) {
    const rawParts = splitPath(item.path);
    const parts = categoryPartsWithoutRedundantRoot(item.path, redundantRoots);
    if (parts.length === 0) continue;
    const hiddenRootCount = rawParts.length - parts.length;

    let node = root;
    for (let index = 0; index < parts.length; index += 1) {
      const part = parts[index];
      const path = rawParts.slice(0, index + hiddenRootCount + 1).join(' > ');
      if (!node.children.has(part)) node.children.set(part, { name: part, path, count: 0, children: new Map() });
      node = node.children.get(part)!;
    }
    node.count = item.courseCount;
  }
  return root;
}
