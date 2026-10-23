import { useEffect, useSyncExternalStore } from 'react';
import { useSession } from '../../auth/useSession';
import { useConnectionScope } from '../../connections/useConnectionScope';
import { reportSelectionStore } from './report-selection';

export function useReportSelection() {
  const { user, can, reportTemplatesEnabled } = useSession();
  const { connectionRef, selectedConnection, connections } = useConnectionScope();
  const effectiveConnection = selectedConnection ?? connections.data?.data.find((connection) => connection.connectionRef === connectionRef);
  const scope = user && effectiveConnection?.connectionId ? `${user.id}:${effectiveConnection.connectionId}` : '';
  const snapshot = useSyncExternalStore(reportSelectionStore.subscribe, reportSelectionStore.getSnapshot);
  useEffect(() => { reportSelectionStore.setScope(scope); }, [scope]);
  return {
    store: reportSelectionStore, snapshot, connectionRef, connectionAlias: effectiveConnection?.alias, scope,
    active: Boolean(scope && snapshot.scope === scope && snapshot.active),
    available: Boolean(scope && can('reports.view')),
    templatesEnabled: reportTemplatesEnabled,
  };
}
export type ReportSelection = ReturnType<typeof useReportSelection>;
