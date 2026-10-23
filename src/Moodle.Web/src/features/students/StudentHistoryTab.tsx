import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import type { Student } from './students-gateway';

function formatDate(value?: string) {
  return value ? new Date(value).toLocaleString('pt-BR') : 'Não informado';
}

export function StudentHistoryTab({ student }: { student: Student }) {
  return (
    <Card>
      <CardHeader><CardTitle className="text-lg">Histórico de acessos</CardTitle></CardHeader>
      <CardContent><ol className="relative ml-2 space-y-6 border-l pl-6 text-sm"><li><span className="absolute -left-1.5 mt-1 h-3 w-3 rounded-full bg-primary ring-4 ring-background" /><p className="font-medium">Primeiro acesso à conta</p><p className="text-muted-foreground">{formatDate(student.firstAccessAt)}</p></li><li><span className="absolute -left-1.5 mt-1 h-3 w-3 rounded-full bg-primary ring-4 ring-background" /><p className="font-medium">Último acesso à conta</p><p className="text-muted-foreground">{formatDate(student.lastAccessAt)}</p></li>{student.courses.map((course) => <li key={`${course.connectionRef}:${course.courseId}`}><span className="absolute -left-1.5 mt-1 h-3 w-3 rounded-full bg-muted-foreground ring-4 ring-background" /><p className="font-medium">Acesso ao curso: {course.name}</p><p className="text-muted-foreground">{formatDate(course.lastCourseAccessAt)}</p></li>)}</ol></CardContent>
    </Card>
  );
}
