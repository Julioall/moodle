import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, ExternalLink, Mail, UserRound } from 'lucide-react';
import { Avatar, AvatarFallback } from '../../components/ui/avatar';
import { Button } from '../../components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Progress } from '../../components/ui/progress';
import { Skeleton } from '../../components/ui/skeleton';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '../../components/ui/tabs';
import { EnrollmentBadge } from './components/EnrollmentBadge';
import { RiskBadge } from './components/RiskBadge';
import { StudentGradesTab } from './StudentGradesTab';
import { StudentHistoryTab } from './StudentHistoryTab';
import { studentsGateway } from './students-gateway';

function initials(name: string) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((part) => part[0]).join('').toUpperCase() || '?';
}

function formatDate(value?: string) {
  return value ? new Date(value).toLocaleString('pt-BR') : 'Não informado';
}

export function StudentProfilePage() {
  const { connectionRef = '', courseId = '', studentId = '' } = useParams();
  const query = useQuery({
    queryKey: ['app', 'student', connectionRef, courseId, studentId],
    queryFn: () => studentsGateway.get(connectionRef, courseId, studentId),
    enabled: Boolean(connectionRef && courseId && studentId),
    staleTime: 30_000,
  });

  const student = query.data?.data;
  const currentCourse = student?.courses.find((course) => course.courseId === courseId && course.connectionRef === connectionRef);

  return (
    <main className="space-y-6 animate-fade-in">
      <Button variant="ghost" size="sm" asChild><Link to={`/cursos/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}?tab=students`}><ArrowLeft className="h-4 w-4" /> Voltar para alunos do curso</Link></Button>

      {query.isPending && <Card><CardContent className="space-y-4 py-8"><Skeleton className="h-12 w-1/2" /><Skeleton className="h-24 w-full" /></CardContent></Card>}
      {query.isError && <Card><CardContent className="py-8"><p role="alert" className="text-destructive">Não foi possível carregar o perfil do aluno.</p></CardContent></Card>}
      {!query.isPending && !query.isError && !student && <Card><CardContent className="py-8"><p>Aluno não encontrado neste curso.</p></CardContent></Card>}

      {student && (
        <>
          <header className="flex flex-col gap-4 rounded-lg border bg-card p-6 shadow-sm md:flex-row md:items-center md:justify-between">
            <div className="flex items-center gap-4">
              <Avatar className="h-16 w-16"><AvatarFallback className="bg-primary/10 text-lg text-primary">{initials(student.name)}</AvatarFallback></Avatar>
              <div><p className="eyebrow">ALUNO · {connectionRef} · {courseId}</p><h1>{student.name}</h1><p className="mt-1 flex items-center gap-1 text-sm text-muted-foreground">{student.email ? <><Mail className="h-3.5 w-3.5" />{student.email}</> : 'Email não informado'}</p></div>
            </div>
            <div className="flex items-center gap-2"><RiskBadge level={student.risk} /><EnrollmentBadge status={student.suspended ? 'suspenso' : currentCourse?.enrollmentStatus ?? 'ativo'} /></div>
          </header>

          <div className="grid gap-4 sm:grid-cols-3">
            <Card><CardHeader className="pb-2"><CardDescription>Último acesso</CardDescription><CardTitle className="text-base">{formatDate(student.lastAccessAt)}</CardTitle></CardHeader><CardContent className="text-xs text-muted-foreground">Conta no Moodle</CardContent></Card>
            <Card><CardHeader className="pb-2"><CardDescription>Acesso ao curso</CardDescription><CardTitle className="text-base">{formatDate(student.lastCourseAccessAt)}</CardTitle></CardHeader><CardContent className="text-xs text-muted-foreground">Contexto consultado</CardContent></Card>
            <Card><CardHeader className="pb-2"><CardDescription>Matrículas visíveis</CardDescription><CardTitle className="text-base">{student.courses.length}</CardTitle></CardHeader><CardContent className="text-xs text-muted-foreground">Somente leitura</CardContent></Card>
          </div>

          <Tabs defaultValue="resumo" className="space-y-4">
            <TabsList><TabsTrigger value="resumo">Resumo</TabsTrigger><TabsTrigger value="notas">Notas</TabsTrigger><TabsTrigger value="historico">Histórico</TabsTrigger></TabsList>
            <TabsContent value="resumo" className="space-y-4">
              <div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]">
                <Card><CardHeader><CardTitle className="text-lg">Contexto da matrícula</CardTitle><CardDescription>Dados recebidos do Moodle para o curso selecionado.</CardDescription></CardHeader><CardContent className="space-y-4">
                  {currentCourse ? <div className="rounded-md border p-4"><div className="flex items-start justify-between gap-3"><div><p className="font-medium">{currentCourse.name}</p><p className="text-xs text-muted-foreground">{currentCourse.connectionRef}:{currentCourse.courseId}</p></div><EnrollmentBadge status={currentCourse.enrollmentStatus} /></div>{currentCourse.progress != null && <div className="mt-4 space-y-1.5"><div className="flex justify-between text-xs text-muted-foreground"><span>Progresso</span><span>{currentCourse.progress}%</span></div><Progress value={currentCourse.progress} className="h-2" /></div>}</div> : <p className="text-sm text-muted-foreground">A matrícula do curso consultado não está disponível no retorno.</p>}
                  <div className="grid gap-3 text-sm sm:grid-cols-2"><div><span className="text-muted-foreground">Primeiro acesso</span><p>{formatDate(student.firstAccessAt)}</p></div><div><span className="text-muted-foreground">Último acesso no curso</span><p>{formatDate(currentCourse?.lastCourseAccessAt)}</p></div></div>
                </CardContent></Card>
                <Card><CardHeader><CardTitle className="text-lg">Sinais de atenção</CardTitle><CardDescription>Indicadores determinísticos, sem inferência automática.</CardDescription></CardHeader><CardContent>{student.riskFactors.length > 0 ? <ul className="space-y-2 text-sm">{student.riskFactors.map((factor) => <li key={factor} className="rounded-md bg-muted px-3 py-2">{factor}</li>)}</ul> : <p className="text-sm text-muted-foreground">Nenhum sinal registrado para este aluno.</p>}</CardContent></Card>
              </div>
              <Card><CardContent className="flex flex-wrap items-center justify-between gap-3 py-4"><div className="flex items-center gap-2 text-sm text-muted-foreground"><UserRound className="h-4 w-4" /> Identidade composta: <span className="font-mono text-xs text-foreground">{connectionRef}:{studentId}</span></div>{student.moodleUrl && <Button variant="outline" size="sm" asChild><a href={student.moodleUrl} target="_blank" rel="noreferrer">Abrir no Moodle <ExternalLink className="h-4 w-4" /></a></Button>}</CardContent></Card>
            </TabsContent>
            <TabsContent value="notas"><StudentGradesTab courses={student.courses} /></TabsContent>
            <TabsContent value="historico"><StudentHistoryTab student={student} /></TabsContent>
          </Tabs>
        </>
      )}
    </main>
  );
}
