import { Navigate } from 'react-router-dom';
import type { ReactNode } from 'react';
import { useSession } from '../../auth/useSession';

export function ReportTemplatesGate({ children }: { children: ReactNode }) {
  const { session, can, reportTemplatesEnabled } = useSession();
  if (session.isPending) return null;
  if (!can('reports.view') || !reportTemplatesEnabled) return <Navigate to="/relatorios" replace />;
  return <>{children}</>;
}
