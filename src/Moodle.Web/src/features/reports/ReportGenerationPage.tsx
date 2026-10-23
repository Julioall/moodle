import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { AppHttpError } from '../../integrations/http/api-client';
import { reportTemplatesGateway, type ReportJobOrigin, type ReportTemplate } from './templates/report-templates-gateway';
import { useReportSelection } from './selection/useReportSelection';
import { useReportPreview } from './useReportPreview';
import { ReportPreviewTable } from './ReportPreviewTable';

function selectionReturnPath(state: unknown) {
  if (!state || typeof state !== 'object' || !('returnTo' in state)) return '/meus-cursos';
  const value = state.returnTo;
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') ? value : '/meus-cursos';
}

export function ReportGenerationPage() {
  const selection = useReportSelection();
  const location = useLocation();
  const navigate = useNavigate();
  const client = useQueryClient();
  const [chosen, setChosen] = useState<ReportTemplate>();
  const [conflict, setConflict] = useState(false);
  const ids = [...selection.snapshot.ids].sort();
  const models = useQuery({
    queryKey: ['report-model-options', selection.scope],
    queryFn: async ({ signal }) => {
      const items: ReportTemplate[] = [];
      for (let page = 1; page <= 5; page++) {
        const result = await reportTemplatesGateway.list(page, signal);
        items.push(...result.items);
        if (items.length >= result.total) return items;
      }
      throw new Error('Não foi possível carregar todos os modelos.');
    },
    enabled: selection.active && selection.available && selection.templatesEnabled,
    staleTime: 0,
    retry: false,
  });
  const limits = useQuery({
    queryKey: ['report-catalog'],
    queryFn: ({ signal }) => reportTemplatesGateway.catalog(signal),
    enabled: selection.active && selection.available && selection.templatesEnabled,
    staleTime: 60_000,
    retry: false,
  });
  const jobOrigin: ReportJobOrigin | undefined = chosen ? { templateId: chosen.id, expectedRevision: chosen.revision } : undefined;
  const preview = useReportPreview(selection.scope, selection.active && selection.available && jobOrigin && selection.connectionRef
    ? { ...jobOrigin, connectionRef: selection.connectionRef, courseIds: ids }
    : undefined);
  const generate = useMutation({
    mutationFn: () => {
      if (!jobOrigin || !selection.connectionRef || !selection.active || selection.snapshot.pending.size || selection.snapshot.errors.size) {
        throw new Error('Confirme uma seleção completa.');
      }
      return reportTemplatesGateway.generate(jobOrigin, selection.connectionRef, ids);
    },
    retry: false,
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['app', 'report-history'] });
      void client.invalidateQueries({ queryKey: ['app', 'report-jobs-notifications'] });
      selection.store.cancel();
      const historyPath = selection.connectionRef ? `/relatorios?connectionRef=${encodeURIComponent(selection.connectionRef)}` : '/relatorios';
      navigate(historyPath);
    },
    onError: (error) => { if (error instanceof AppHttpError && error.status === 409) setConflict(true); },
  });
  const resetGeneration = generate.reset;
  useEffect(() => { setChosen(undefined); setConflict(false); resetGeneration(); }, [resetGeneration, selection.scope]);

  const returnTo = selectionReturnPath(location.state);
  const cancel = () => { selection.store.cancel(); navigate('/relatorios'); };
  const exceedsCourseLimit = limits.data !== undefined && ids.length > limits.data.limits.maxCourses;
  const canGenerate = Boolean(jobOrigin && limits.data && !exceedsCourseLimit && !conflict && !generate.isPending && ids.length && selection.snapshot.pending.size === 0 && selection.snapshot.errors.size === 0);

  if (selection.snapshot.active && !selection.scope) return <main role="status">Preparando seleção de cursos…</main>;
  if (!selection.available) return <main className="space-y-4" aria-labelledby="report-generation-title"><h1 id="report-generation-title" className="text-2xl font-semibold">Gerar relatório</h1><p role="alert">A geração de relatórios não está disponível para esta conta.</p><Button asChild variant="outline"><Link to="/relatorios">Voltar aos relatórios</Link></Button></main>;
  if (!selection.active || ids.length === 0) return <main className="space-y-4" aria-labelledby="report-generation-title"><h1 id="report-generation-title" className="text-2xl font-semibold">Gerar relatório</h1><p role="alert">A seleção de cursos não está disponível. Volte à lista e selecione os cursos novamente.</p><div className="flex flex-wrap gap-2"><Button asChild><Link to="/meus-cursos">Escolher cursos</Link></Button><Button asChild variant="outline"><Link to="/relatorios">Histórico de relatórios</Link></Button></div></main>;
  if (!selection.templatesEnabled) return <main className="space-y-4" aria-labelledby="report-generation-title"><h1 id="report-generation-title" className="text-2xl font-semibold">Gerar relatório</h1><p role="alert">A geração por modelos está desativada.</p><Button variant="outline" onClick={() => navigate(returnTo)}>Voltar à seleção</Button></main>;

  return <main className="space-y-6 animate-fade-in" aria-labelledby="report-generation-title">
    <header className="page-heading"><div><nav aria-label="Breadcrumb" className="text-sm text-muted-foreground"><Link to="/relatorios">Relatórios</Link> / Gerar relatório</nav><h1 id="report-generation-title">Gerar relatório</h1><p>{ids.length} cursos / UCs selecionados · conexão {selection.connectionAlias || selection.connectionRef}</p>{limits.data && <p className="text-xs text-muted-foreground">Limites: {limits.data.limits.maxCourses} cursos, {limits.data.limits.maxRows.toLocaleString('pt-BR')} linhas e {limits.data.limits.maxAssignmentsPerCourse} atividades por curso quando selecionadas; até {Math.round(limits.data.limits.generationTimeoutSeconds / 60)} minutos.</p>}</div><Button variant="outline" disabled={generate.isPending} onClick={() => navigate(returnTo)}>Voltar à seleção</Button></header>

    <section aria-label="Configuração do relatório" className="space-y-4 rounded-lg border bg-card p-5">
      <div className="max-w-2xl space-y-2"><label id="report-model-label" className="text-sm font-medium">Modelo do relatório</label><Select value={chosen?.id ?? ''} onValueChange={(id) => { setChosen(models.data?.find((item) => item.id === id)); setConflict(false); generate.reset(); }} disabled={generate.isPending || models.isPending}><SelectTrigger aria-labelledby="report-model-label"><SelectValue placeholder="Selecione um modelo" /></SelectTrigger><SelectContent>{models.data?.map((model) => <SelectItem key={model.id} value={model.id}>{model.name} · revisão {model.revision}</SelectItem>)}</SelectContent></Select>{models.isError && <p role="alert">Não foi possível carregar os modelos. <Button variant="link" onClick={() => void models.refetch()}>Tentar novamente</Button></p>}{models.isSuccess && models.data.length === 0 && <p className="text-sm text-muted-foreground">Nenhum modelo disponível. <Link className="text-primary underline" to="/relatorios/modelos/novo">Criar modelo</Link></p>}</div>
    </section>

    <section aria-labelledby="report-preview-title" className="min-w-0 space-y-4">
      <div><h2 id="report-preview-title" className="text-lg font-semibold">Prévia da geração</h2><p className="text-sm text-muted-foreground">A amostra é temporária; o relatório final fará uma nova leitura dos dados.</p></div>
      {jobOrigin && preview.isPending && <p role="status">Carregando amostra…</p>}
      {jobOrigin && preview.isError && <p role="alert">{preview.error instanceof AppHttpError && preview.error.body?.error?.code === 'preview_busy' ? 'Outra prévia está em andamento. Aguarde e tente novamente.' : preview.error instanceof AppHttpError && preview.error.body?.error?.code === 'report_course_limit' ? `A prévia aceita até ${limits.data?.limits.maxCourses ?? 20} cursos. Reduza a seleção.` : preview.error instanceof AppHttpError && preview.error.status === 429 ? 'Muitas consultas em pouco tempo. Aguarde o prazo indicado e tente novamente.' : 'Não foi possível carregar a amostra.'} <Button variant="link" onClick={() => void preview.refetch()}>Tentar novamente</Button></p>}
      {!jobOrigin && <p role="status">Selecione um modelo para carregar a amostra.</p>}
      {preview.data && <ReportPreviewTable preview={preview.data} />}
    </section>

    {selection.snapshot.pending.size > 0 && <p role="status">Aguarde a confirmação de todas as páginas da seleção.</p>}
    {selection.snapshot.errors.size > 0 && <p role="alert">A seleção contém categorias não confirmadas. Volte à lista e tente novamente.</p>}
    {limits.isError && <p role="alert">Não foi possível carregar os limites de geração. <Button variant="link" onClick={() => void limits.refetch()}>Tentar novamente</Button></p>}
    {exceedsCourseLimit && <p role="alert">A seleção ultrapassa o limite de {limits.data?.limits.maxCourses} {limits.data?.limits.maxCourses === 1 ? 'curso' : 'cursos'} por relatório. Volte à lista e reduza a seleção.</p>}
    {generate.isError && <p role="alert" className="text-sm text-destructive">{conflict ? 'A revisão ou o estado disponível mudou. Recarregue os modelos e confirme novamente.' : 'Não foi possível confirmar a geração. Consulte o histórico antes de reenviar.'}</p>}
    {conflict && <Button variant="outline" onClick={() => { setChosen(undefined); setConflict(false); generate.reset(); void models.refetch(); }}>Recarregar modelos</Button>}
    <footer className="flex flex-wrap justify-between gap-3 border-t pt-4"><Button variant="outline" disabled={generate.isPending} onClick={cancel}>Cancelar geração</Button><Button disabled={!canGenerate} onClick={() => generate.mutate()}>{generate.isPending ? 'Confirmando…' : 'Gerar relatório'}</Button></footer>
  </main>;
}
