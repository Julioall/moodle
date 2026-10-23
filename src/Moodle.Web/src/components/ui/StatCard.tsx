import { cn } from '@/lib/utils';
import { LucideIcon, RefreshCw } from 'lucide-react';
import { Button } from './button';

interface StatCardProps {
  title: string;
  value: number | string;
  subtitle?: string;
  icon?: LucideIcon;
  trend?: {
    value: number;
    label: string;
    positive?: boolean;
  };
  variant?: 'default' | 'warning' | 'danger' | 'success' | 'pending' | 'risk';
  className?: string;
  onRefresh?: () => void;
  refreshing?: boolean;
}

export function StatCard({
  title,
  value,
  subtitle,
  icon: Icon,
  trend,
  variant = 'default',
  className,
  onRefresh,
  refreshing = false,
}: StatCardProps) {
  const variantStyles = {
    default: 'bg-card',
    warning: 'bg-card border-l-2 border-l-status-warning',
    danger: 'bg-card border-l-2 border-l-risk-critico',
    success: 'bg-card border-l-2 border-l-status-success',
    pending: 'bg-card border-l-2 border-l-status-pending',
    risk: 'bg-card border-l-2 border-l-risk-risco',
  };

  return (
    <div className={cn(
      'rounded-lg border p-4 shadow-sm',
      variantStyles[variant],
      className
    )}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0 flex-1 space-y-1">
          <p className="text-sm font-medium text-muted-foreground">{title}</p>
          <p className="text-2xl font-bold tracking-tight">{value}</p>
          {subtitle && (
            <p className="text-xs text-muted-foreground">{subtitle}</p>
          )}
          {trend && (
            <p className={cn(
              'text-xs font-medium',
              trend.positive ? 'text-status-success' : 'text-risk-risco'
            )}>
              {trend.positive ? '+' : ''}{trend.value} {trend.label}
            </p>
          )}
        </div>
        <div className="flex shrink-0 items-start gap-1">
          {onRefresh && <Button type="button" variant="ghost" size="icon" className="h-7 w-7" aria-label={`Atualizar ${title}`} title={`Atualizar ${title}`} onClick={onRefresh} disabled={refreshing}><RefreshCw className={cn('h-3.5 w-3.5', refreshing && 'animate-spin')} /></Button>}
          {Icon && <div className={cn('rounded-lg p-2')}>
            <Icon className={cn(
              'h-5 w-5',
              variant === 'default' && 'text-muted-foreground',
              variant === 'warning' && 'text-status-warning',
              variant === 'danger' && 'text-risk-critico',
              variant === 'success' && 'text-status-success',
              variant === 'pending' && 'text-status-pending',
              variant === 'risk' && 'text-risk-risco',
            )} />
          </div>}
        </div>
      </div>
    </div>
  );
}
