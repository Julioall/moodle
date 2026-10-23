import { Badge } from '@/components/ui/badge';
import type { PlannerReference } from './tasks-gateway';

const labels: Record<string, string> = { course: 'Curso', student: 'Aluno', class: 'Turma', school: 'Escola', curricular_unit: 'UC', tutor: 'Tutor', monitor: 'Monitor', category: 'Categoria', connection: 'Conexão', custom: 'Contexto' };

export function PlannerReferenceTags({ references, compact = false }: { references?: PlannerReference[]; compact?: boolean }) {
  if (!references?.length) return null;
  return <div className="flex flex-wrap gap-1.5" aria-label="Vínculos da atividade">{references.map((reference) => <Badge key={`${reference.id ?? ''}-${reference.referenceType}-${reference.referenceId}-${reference.parentReferenceId ?? ''}`} variant="outline" className={compact ? 'px-1.5 py-0 text-[10px]' : 'px-2 py-0.5 text-[11px]'}>{labels[reference.referenceType] ?? reference.referenceType} · {reference.referenceName ?? reference.referenceId}</Badge>)}</div>;
}
