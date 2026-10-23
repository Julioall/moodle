import { Badge } from '../../../components/ui/badge';
import { cn } from '@/lib/utils';

export function EnrollmentBadge({ status }: { status: string }) {
  const normalized = status.toLowerCase();
  const label = ({ ativo: 'Ativo', active: 'Ativo', suspenso: 'Suspenso', suspended: 'Suspenso', concluido: 'Concluído', completed: 'Concluído' } as Record<string, string>)[normalized] ?? status;
  const className = normalized === 'suspenso' || normalized === 'suspended'
    ? 'border-destructive/30 bg-destructive/10 text-destructive'
    : normalized === 'concluido' || normalized === 'completed'
      ? 'border-primary/30 bg-primary/10 text-primary'
      : 'border-[hsl(var(--risk-normal)/0.3)] bg-[hsl(var(--risk-normal-bg))] text-[hsl(var(--risk-normal))]';

  return <Badge className={cn(className)}>{label}</Badge>;
}
