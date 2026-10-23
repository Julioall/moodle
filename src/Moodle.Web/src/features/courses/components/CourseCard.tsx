import { Calendar } from 'lucide-react';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import type { Course } from '../courses-gateway';
import { getCourseLifecycle } from '../course-status';

function formatDate(value?: string) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString('pt-BR');
}

function lifecycle(course: Course) {
  const status = getCourseLifecycle(course);
  if (status === 'finished') return { label: 'Finalizada', className: 'border-slate-300 bg-slate-100 text-slate-700' };
  if (status === 'not_started') return { label: 'Não iniciada', className: 'border-amber-200 bg-amber-50 text-amber-700' };
  return { label: 'Em andamento', className: 'border-emerald-200 bg-emerald-50 text-emerald-700' };
}

export type CourseCardAction = { label: string; ariaLabel?: string; icon: ReactNode; onClick: () => void };
export type CourseCardSelection = { checked: boolean; ariaLabel: string; onChange: () => void };

export function CourseCard({ course, action, selection, detailsHref }: { course: Course; action?: CourseCardAction; selection?: CourseCardSelection; detailsHref?: string }) {
  const status = lifecycle(course);
  const title = course.displayName ?? course.fullName;
  return <Card className="relative h-full">
    <CardContent className={`p-5 ${selection ? 'pl-12' : ''}`}>
      <div className="space-y-4"><div className="flex items-start justify-between gap-2"><div className="min-w-0 space-y-2 pr-8"><h2 className="line-clamp-2 font-semibold leading-tight">{detailsHref ? <Link to={detailsHref} className="rounded-sm hover:underline underline-offset-2 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" aria-label={`Abrir detalhes de ${title}`}>{title}</Link> : title}</h2><p className="truncate text-xs text-muted-foreground">{course.shortName ?? course.categoryName ?? 'Curso Moodle'}</p><Badge variant="outline" className={`text-[10px] ${status.className}`}>{status.label}</Badge></div></div><div className="space-y-1 border-y py-3 text-xs text-muted-foreground"><div className="flex items-center gap-2"><Calendar className="h-3 w-3" />Início: {formatDate(course.startDate)}</div><div className="flex items-center gap-2"><Calendar className="h-3 w-3" />Fim: {formatDate(course.endDate)}</div></div></div>
    </CardContent>
    {selection && <input type="checkbox" checked={selection.checked} aria-label={selection.ariaLabel} className="absolute left-4 top-4 z-10 h-4 w-4 accent-primary" onChange={selection.onChange} onClick={(event) => event.stopPropagation()} />}
    {action && <Button type="button" variant="ghost" size="icon" className="absolute right-3 top-3 z-10 h-8 w-8 text-muted-foreground hover:text-foreground" aria-label={action.ariaLabel ?? action.label} title={action.label} onClick={action.onClick}>{action.icon}</Button>}
  </Card>;
}
