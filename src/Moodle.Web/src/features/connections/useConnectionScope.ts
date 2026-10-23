import { useQuery } from '@tanstack/react-query';
import { useCallback, useEffect, useSyncExternalStore } from 'react';
import { useSearchParams } from 'react-router-dom';
import { connectionsGateway, type MoodleConnection } from './connections-gateway';

const SELECTED_CONNECTION_KEY = 'app:selected-connection';

function readStoredConnection(): string | undefined {
  if (typeof window === 'undefined') return undefined;
  return window.localStorage.getItem(SELECTED_CONNECTION_KEY) || undefined;
}

let globalConnection = readStoredConnection();
const globalConnectionListeners = new Set<() => void>();

function subscribeToGlobalConnection(listener: () => void) {
  globalConnectionListeners.add(listener);
  return () => globalConnectionListeners.delete(listener);
}

function getGlobalConnection() {
  return globalConnection;
}

function setGlobalConnection(next?: string) {
  globalConnection = next;
  if (typeof window !== 'undefined') {
    if (next) window.localStorage.setItem(SELECTED_CONNECTION_KEY, next);
    else window.localStorage.removeItem(SELECTED_CONNECTION_KEY);
  }
  globalConnectionListeners.forEach((listener) => listener());
}

export function useMoodleConnections() {
  return useQuery({
    queryKey: ['app', 'connections'],
    queryFn: connectionsGateway.list,
    staleTime: 60_000,
  });
}

/**
 * Keeps the selected Moodle in the URL when possible and in local storage
 * across screens. The absence of a value means the server's default Moodle;
 * there is intentionally no synthetic "all" scope.
 */
export function useConnectionScope() {
  const [params, setParams] = useSearchParams();
  const connections = useMoodleConnections();
  const urlConnection = params.get('connectionRef') || undefined;
  const storedConnection = useSyncExternalStore(
    subscribeToGlobalConnection,
    getGlobalConnection,
    () => undefined,
  );
  const defaultConnection = connections.data?.data.find((connection) => connection.isDefault)
    ?? connections.data?.data[0];
  const connectionRef = urlConnection ?? storedConnection;
  const selectedConnection = connections.data?.data.find((connection) => connection.connectionRef === connectionRef)
    ?? (!connectionRef ? defaultConnection : undefined);
  const effectiveConnectionRef = selectedConnection?.connectionRef
    ?? (connections.isSuccess ? defaultConnection?.connectionRef : connectionRef);

  // A URL parameter is accepted for deep links, but the selected Moodle is
  // application state. Once the connection list confirms the parameter,
  // promote it to the global selection so the next screen keeps the same
  // Moodle even when its route has no query string.
  useEffect(() => {
    if (urlConnection && selectedConnection?.connectionRef === urlConnection && storedConnection !== urlConnection) {
      setGlobalConnection(urlConnection);
    }
  }, [selectedConnection?.connectionRef, storedConnection, urlConnection]);

  const selectConnection = useCallback((nextConnectionRef: string) => {
    const next = nextConnectionRef || undefined;
    setGlobalConnection(next);
    setParams((current) => {
      const nextParams = new URLSearchParams(current);
      if (next) nextParams.set('connectionRef', next);
      else nextParams.delete('connectionRef');
      return nextParams;
    }, { replace: true });
  }, [setParams]);

  return {
    connections,
    connectionRef: effectiveConnectionRef,
    selectedConnection,
    selectConnection,
  };
}

export function connectionDisplayName(connection?: MoodleConnection) {
  return connection?.alias || connection?.connectionRef || 'Moodle padrão';
}
