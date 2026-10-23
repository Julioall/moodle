import { cn } from '@/lib/utils';
import { Badge } from '../../../components/ui/badge';

const labels: Record<string, string> = {
  normal: 'Normal',
  atencao: 'Atenção',
  attention: 'Atenção',
  risco: 'Risco',
  risk: 'Risco',
  critico: 'Crítico',
  critical: 'Crítico',
};

const classes: Record<string, string> = {
  normal: 'risk-normal',
  atencao: 'risk-atencao',
  attention: 'risk-atencao',
  risco: 'risk-risco',
  risk: 'risk-risco',
  critico: 'risk-critico',
  critical: 'risk-critico',
};

export function RiskBadge({ level, showDot = true }: { level: string; showDot?: boolean }) {
  const label = labels[level] ?? level;
  const style = classes[level] ?? 'risk-inativo';

  return (
    <Badge className={cn(style, 'gap-1.5')} aria-label={`Risco: ${label}`}>
      {showDot && <span className="h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />}
      {label}
    </Badge>
  );
}
