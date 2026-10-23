import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ClipboardCheck, FileText, MessageCircle, Phone, Plus, RefreshCw, UserRound } from 'lucide-react';
import { useState } from 'react';

import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { followupGateway, type FollowupInput } from './followup-gateway';

const kindLabels: Record<string, string> = { acompanhamento: 'Acompanhamento', contato: 'Contato realizado', orientacao: 'Orientação enviada', pendencia_conferida: 'Pendência conferida', ligacao: 'Ligação', resposta_aluno: 'Resposta do aluno' };

export type CourseContext = { connectionRef: string; courseId: string; courseName?: string };

function kindIcon(kind: string) {
  if (kind === 'ligacao') return Phone;
  if (kind === 'resposta_aluno' || kind === 'contato') return MessageCircle;
  if (kind === 'pendencia_conferida') return ClipboardCheck;
  return FileText;
}

export function FollowupPage({ courseContext, embedded = false }: { courseContext?: CourseContext; embedded?: boolean }) {
  const client = useQueryClient();
  const scopedCourseRef = courseContext ? `${courseContext.connectionRef}:${courseContext.courseId}` : undefined;
  const query = useQuery({ queryKey: ['app', 'followups', scopedCourseRef], queryFn: () => followupGateway.list(courseContext ? { connectionRef: courseContext.connectionRef, courseId: courseContext.courseId } : undefined), staleTime: 30_000 });
  const [studentRef, setStudentRef] = useState('');
  const [courseRef, setCourseRef] = useState(scopedCourseRef ?? '');
  const [notes, setNotes] = useState('');
  const [kind, setKind] = useState('acompanhamento');
  const create = useMutation({ mutationFn: (input: FollowupInput) => followupGateway.create(input), onSuccess: () => { setStudentRef(''); setCourseRef(scopedCourseRef ?? ''); setNotes(''); setKind('acompanhamento'); void client.invalidateQueries({ queryKey: ['app', 'followups'] }); } });
  const records = query.data?.data ?? [];
  const titleId = embedded ? 'course-followup-title' : 'followup-title';
  const Container = embedded ? 'section' : 'main';

  return <Container className={embedded ? 'space-y-4' : 'content-frame'} aria-labelledby={titleId}><header className="page-heading"><div><p className="eyebrow">OPERACIONAL</p>{embedded ? <h2 id={titleId}>Follow-up do curso</h2> : <h1 id={titleId}>Follow-up</h1>}<p>{embedded ? `Registre acompanhamentos vinculados a ${courseContext?.courseName ?? `este curso (${courseContext?.courseId})`}.` : 'Registre contatos e acompanhamentos humanos com rastreabilidade.'}</p></div><div className="flex items-center gap-3">{query.data && <span className="freshness">Atualizado em {new Date(query.data.meta.generatedAt).toLocaleString('pt-BR')}</span>}<Button type="button" variant="outline" onClick={() => void query.refetch()} disabled={query.isFetching}><RefreshCw className={query.isFetching ? 'animate-spin' : ''} />Atualizar</Button></div></header>
    <div className="grid gap-6 lg:grid-cols-[minmax(0,0.9fr)_minmax(0,1.1fr)]"><Card><CardHeader><CardTitle className="flex items-center gap-2 text-lg"><Plus className="h-5 w-5 text-primary" />Registrar acompanhamento</CardTitle><CardDescription>Documente uma ação humana vinculada ao aluno e ao curso atual.</CardDescription></CardHeader><CardContent><form className="grid gap-4" onSubmit={(event) => { event.preventDefault(); if (studentRef.trim() && notes.trim()) create.mutate({ studentRef: studentRef.trim(), courseRef: courseRef.trim() || undefined, notes: notes.trim(), kind }); }}><label className="grid gap-1.5 text-sm font-medium">Referência do aluno<Input value={studentRef} onChange={(event) => setStudentRef(event.target.value)} placeholder="connectionRef:studentId" required /></label>{courseContext ? <div className="rounded-md border bg-muted/30 px-3 py-2 text-sm"><p className="text-xs text-muted-foreground">Curso atual</p><p className="mt-1 font-medium">{courseContext.courseName ?? courseContext.courseId}</p></div> : <label className="grid gap-1.5 text-sm font-medium">Referência do curso <span className="font-normal text-muted-foreground">opcional</span><Input value={courseRef} onChange={(event) => setCourseRef(event.target.value)} placeholder="connectionRef:courseId" /></label>}<label className="grid gap-1.5 text-sm font-medium">Tipo de acompanhamento<select className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" value={kind} onChange={(event) => setKind(event.target.value)}>{Object.entries(kindLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label><label className="grid gap-1.5 text-sm font-medium">Notas do acompanhamento<Textarea className="min-h-36" value={notes} onChange={(event) => setNotes(event.target.value)} placeholder="Descreva o contato, a orientação ou o próximo passo." required /></label><Button type="submit" disabled={create.isPending || !studentRef.trim() || !notes.trim()}>{create.isPending ? 'Registrando…' : 'Registrar acompanhamento'}</Button></form>{create.isError && <p className="mt-4 text-sm text-destructive" role="alert">Não foi possível registrar o acompanhamento.</p>}{create.isSuccess && <p className="mt-4 text-sm text-status-success" role="status">Acompanhamento registrado com sucesso.</p>}</CardContent></Card>
      <Card><CardHeader><div className="flex items-center justify-between gap-3"><div><CardTitle className="text-lg">Histórico recente</CardTitle><CardDescription>{records.length} registro{records.length === 1 ? '' : 's'} carregado{records.length === 1 ? '' : 's'}.</CardDescription></div><Badge variant="outline">Somente leitura</Badge></div></CardHeader><CardContent>{query.isPending && <p className="text-sm text-muted-foreground">Carregando registros…</p>}{query.isError && <p className="text-sm text-destructive" role="alert">Não foi possível carregar os acompanhamentos.</p>}{query.isSuccess && records.length === 0 && <div className="flex flex-col items-center gap-3 rounded-lg border border-dashed p-10 text-center"><ClipboardCheck className="h-8 w-8 text-muted-foreground/40" /><p className="text-sm font-medium">Nenhum acompanhamento registrado</p><p className="text-xs text-muted-foreground">Os registros feitos no formulário aparecerão aqui.</p></div>}{query.isSuccess && records.length > 0 && <ol className="relative ml-2 space-y-6 border-l pl-6">{records.map((item) => { const Icon = kindIcon(item.kind); return <li key={item.id} className="relative"><span className="absolute -left-[1.82rem] mt-1 flex h-6 w-6 items-center justify-center rounded-full bg-primary/10 text-primary ring-4 ring-background"><Icon className="h-3 w-3" /></span><div className="rounded-lg border bg-card p-4"><div className="flex flex-wrap items-start justify-between gap-2"><div><p className="font-medium">{item.studentRef}</p><p className="mt-1 text-xs text-muted-foreground">{item.courseRef ? `Curso ${item.courseRef} · ` : ''}{new Date(item.occurredAt).toLocaleString('pt-BR')}</p></div><Badge variant="outline">{kindLabels[item.kind] ?? item.kind}</Badge></div><p className="mt-3 whitespace-pre-wrap text-sm text-muted-foreground">{item.notes}</p><div className="mt-3 flex items-center gap-1.5 border-t pt-3 text-xs text-muted-foreground"><UserRound className="h-3.5 w-3.5" />Registro operacional auditável</div></div></li>; })}</ol>}</CardContent></Card></div>
  </Container>;
}
