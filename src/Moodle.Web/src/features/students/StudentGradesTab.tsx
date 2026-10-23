import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import type { StudentCourse } from './students-gateway';

function gradeLabel(grade: { percentage?: number; grade?: number; maximum?: number }) {
  if (grade.percentage != null) return `${grade.percentage}%`;
  if (grade.grade != null && grade.maximum != null) return `${grade.grade} / ${grade.maximum}`;
  return grade.grade != null ? String(grade.grade) : 'Sem nota';
}

export function StudentGradesTab({ courses }: { courses: StudentCourse[] }) {
  return (
    <section aria-label="Notas" className="grid gap-4 lg:grid-cols-2">
      {courses.length === 0 && <Card><CardContent className="py-8"><p className="text-sm text-muted-foreground">Nenhuma nota encontrada.</p></CardContent></Card>}
      {courses.map((course) => (
        <Card key={`${course.connectionRef}:${course.courseId}`}>
          <CardHeader><div className="flex items-start justify-between gap-3"><div><CardTitle className="text-lg">{course.name}</CardTitle><CardDescription>Notas disponíveis no Moodle</CardDescription></div><Badge variant="outline">Somente leitura</Badge></div></CardHeader>
          <CardContent>{course.grades.length === 0 ? <p className="text-sm text-muted-foreground">Sem notas registradas.</p> : <div className="divide-y">{course.grades.map((grade) => <div className="flex items-start justify-between gap-4 py-3 first:pt-0 last:pb-0" key={grade.itemId}><div><p className="text-sm font-medium">{grade.name}</p>{grade.feedback && <p className="mt-1 text-xs text-muted-foreground">Feedback: {grade.feedback}</p>}</div><strong className="whitespace-nowrap text-sm">{gradeLabel(grade)}</strong></div>)}</div>}</CardContent>
        </Card>
      ))}
    </section>
  );
}
