import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { reportTemplatesGateway, type ReportPreviewInput } from './templates/report-templates-gateway';

export function useReportPreview(ownerConnectionScope: string, input?: ReportPreviewInput) {
  const client = useQueryClient();
  const serialized = input ? JSON.stringify(input) : '';
  const [debounced, setDebounced] = useState('');
  useEffect(() => {
    if (!serialized) return;
    const timer = window.setTimeout(() => setDebounced(serialized), 400);
    return () => window.clearTimeout(timer);
  }, [serialized, ownerConnectionScope]);
  useEffect(() => () => { void client.cancelQueries({ queryKey: ['report-preview', ownerConnectionScope] }); client.removeQueries({ queryKey: ['report-preview', ownerConnectionScope] }); }, [client, ownerConnectionScope]);
  return useQuery({
    queryKey: ['report-preview', ownerConnectionScope, serialized],
    queryFn: ({ signal }) => reportTemplatesGateway.preview(JSON.parse(serialized) as ReportPreviewInput, signal),
    enabled: Boolean(ownerConnectionScope && serialized && debounced === serialized),
    retry: false, gcTime: 0, staleTime: 0, refetchOnWindowFocus: false,
  });
}
