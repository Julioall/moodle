import { Navigate } from 'react-router-dom';
import { useSession } from '../auth/useSession';
import { ReportHistoryPage } from './ReportHistoryPage';

export function ReportsPage() {
  const { session, can } = useSession();
  if (session.isPending) return null;
  return can('reports.view') ? <ReportHistoryPage /> : <Navigate to="/meus-cursos" replace />;
}
