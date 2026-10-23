import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Checkbox } from '@/components/ui/checkbox';
import { Skeleton } from '@/components/ui/skeleton';
import { ChevronDown, House, Search, Trash2 } from 'lucide-react';
import { useSession } from '../../auth/useSession';
import { AppHttpError } from '../../../integrations/http/api-client';
import { ReportTemplatesGate } from './ReportTemplatesGate';
import { reportTemplatesGateway, type ReportDefinition, type ReportField, type ReportFilter } from './report-templates-gateway';
import { ReportTemplateSample } from './ReportTemplateSample';
import { ReportPreviewColumnHeaders, type ReportPreviewColumnActions } from '../../reports/ReportPreviewColumnHeaders';

const DEFAULT_COLUMNS = ['student.name', 'course.name', 'access.course_at', 'access.days_since_course', 'grade.value', 'tracking.situation'];
const DEFAULT_PARAMETERS = { inactiveDaysThreshold: 7, minGradePercent: 60 };

export function ReportTemplateEditorPage() {
  return <ReportTemplatesGate><Editor /></ReportTemplatesGate>;
}
function Editor() {
  const { id } = useParams();
  const { user } = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [columns, setColumns] = useState<ReportDefinition['columns']>([]);
  const [filters, setFilters] = useState<ReportFilter[]>([]);
  const [parameters, setParameters] = useState(DEFAULT_PARAMETERS);
  const [search, setSearch] = useState('');
  const [activeTab, setActiveTab] = useState('campos');
  const [revision, setRevision] = useState<number>();
  const [message, setMessage] = useState('');
  const [columnFeedback, setColumnFeedback] = useState('');
  const [sampleOpen, setSampleOpen] = useState(false);
  const [revisionConflict, setRevisionConflict] = useState(false);
  const initialized = useRef<string>();
  const catalog = useQuery({ queryKey: ['report-catalog', user?.id], queryFn: ({ signal }) => reportTemplatesGateway.catalog(signal), retry: false });
  const template = useQuery({ queryKey: ['report-template', user?.id, id], queryFn: ({ signal }) => reportTemplatesGateway.get(id!, signal), enabled: !!id, retry: false });
  const fields = useMemo(() => catalog.data?.fields ?? [], [catalog.data?.fields]);
  const columnsValid = columns.length > 0 && columns.length <= Math.min(30, fields.length) &&
    new Set(columns.map(c => (c.label?.trim() || fields.find(f => f.fieldId === c.fieldId)?.label || '').toLocaleLowerCase('en-US'))).size === columns.length &&
    columns.every(c => fields.some(f => f.fieldId === c.fieldId) && (c.label?.trim().length ?? 0) <= 80);

  useEffect(() => {
    initialized.current = undefined;
    setName(''); setDescription(''); setColumns([]); setFilters([]); setParameters(DEFAULT_PARAMETERS);
    setRevision(undefined); setMessage(''); setSampleOpen(false); setRevisionConflict(false); setActiveTab('campos');
  }, [user?.id, id]);
  useEffect(() => {
    if (!id && catalog.data && initialized.current !== 'new') {
      initialized.current = 'new';
      setColumns(DEFAULT_COLUMNS.filter(fieldId => fields.some(field => field.fieldId === fieldId)).map(fieldId => ({ fieldId })));
    }
  }, [catalog.data, fields, id]);
  useEffect(() => {
    if (template.data && initialized.current !== id) {
      initialized.current = id;
      setName(template.data.name); setDescription(template.data.description ?? '');
      setColumns(template.data.definition.columns);
      setFilters(template.data.definition.filters ?? []);
      setParameters(template.data.definition.parameters ?? DEFAULT_PARAMETERS);
      setRevision(template.data.revision);
    }
  }, [id, template.data, user?.id]);

  const columnLabels = columns.map(c => (c.label?.trim() || fields.find(f => f.fieldId === c.fieldId)?.label || '').toLocaleLowerCase('en-US'));
  const invalidFilter = filters.some(filter => {
    const field = fields.find(f => f.fieldId === filter.fieldId);
    return !field?.filterable || !filter.operator || !['is_missing', 'is_unavailable'].includes(filter.operator) && !filter.value?.trim();
  });
  const invalid = !name.trim() || name.trim().length > 120 || !columnsValid || new Set(columnLabels).size !== columnLabels.length ||
    filters.length > (catalog.data?.limits.maxFilters ?? 20) || invalidFilter ||
    !Number.isFinite(parameters.inactiveDaysThreshold) || parameters.inactiveDaysThreshold < 1 || parameters.inactiveDaysThreshold > 3650 ||
    !Number.isFinite(parameters.minGradePercent) || parameters.minGradePercent < 0 || parameters.minGradePercent > 100;
  const save = useMutation({
    mutationFn: () => {
      const definition = { schemaVersion: 1 as const, sourceId: 'student_course' as const, columns, filters, parameters };
      const input = { name, description, definition };
      return id ? reportTemplatesGateway.update(id, input, revision!) : reportTemplatesGateway.create(input);
    },
    retry: false,
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['report-templates', user?.id] }); navigate('/relatorios/modelos'); },
    onError: error => { setRevisionConflict(error instanceof AppHttpError && error.status === 409); setMessage(error instanceof AppHttpError && error.status === 409
      ? 'O modelo foi alterado em outra sessão. Seu rascunho foi preservado. Confira a revisão antes de salvar novamente.'
      : 'Não foi possível salvar. Seu rascunho foi preservado. Consulte os modelos antes de reenviar se o resultado for incerto.'); },
  });
  const toggle = (fieldId: string, checked: boolean) => {
    setMessage('');
    setColumns(current => checked ? [...current, { fieldId }] : current.filter(c => c.fieldId !== fieldId));
  };
  const reorderColumns = (draggedFieldId: string, targetFieldId: string, position: 'before' | 'after') => setColumns(current => {
    const fromIndex = current.findIndex(column => column.fieldId === draggedFieldId);
    const toIndex = current.findIndex(column => column.fieldId === targetFieldId);
    if (fromIndex < 0 || toIndex < 0 || fromIndex === toIndex) return current;
    const next = [...current];
    const [moving] = next.splice(fromIndex, 1);
    let insertionIndex = toIndex + (position === 'after' ? 1 : 0);
    if (fromIndex < insertionIndex) insertionIndex -= 1;
    next.splice(insertionIndex, 0, moving);
    return next;
  });
  const renameColumn = (fieldId: string, label: string) => setColumns(current => current.map(column => column.fieldId === fieldId ? { ...column, label } : column));
  const columnActions: ReportPreviewColumnActions = { disabled: save.isPending, onAnnounce: setColumnFeedback, onRemove: fieldId => toggle(fieldId, false), onRename: renameColumn, onReorder: reorderColumns };
  const visible = fields.filter(f => `${f.label} ${f.group}`.toLocaleLowerCase('pt-BR').includes(search.toLocaleLowerCase('pt-BR')));

  if (catalog.isPending || id && template.isPending) return <main className="space-y-6 pb-12" aria-busy="true"><p role="status" className="sr-only">Carregando modelo…</p><Skeleton className="h-8 w-2/3" /><div className="grid gap-5 md:grid-cols-2"><Skeleton className="h-32" /><Skeleton className="h-32" /></div><div className="grid gap-6 lg:grid-cols-2"><Skeleton className="h-96" /><Skeleton className="h-96" /></div></main>;
  if (catalog.isError || template.isError) return <div role="alert">Não foi possível carregar o modelo. <Link to="/relatorios/modelos">Voltar aos modelos</Link></div>;
  const groups = [...new Set(visible.map(field => field.group))];
  const recommended = DEFAULT_COLUMNS.map(fieldId => visible.find(field => field.fieldId === fieldId)).filter((field): field is ReportField => !!field);
  const previewColumns = columns.map(column => ({ fieldId: column.fieldId, label: column.label ?? fields.find(field => field.fieldId === column.fieldId)?.label ?? column.fieldId }));
  return <main className="flex min-h-0 flex-1 flex-col gap-6 pb-4">
    <header className="space-y-3"><nav aria-label="Breadcrumb" className="text-sm text-muted-foreground"><Link to="/relatorios">Relatórios</Link><span className="px-2">/</span><Link to="/relatorios/modelos">Modelos</Link><span className="px-2">/</span>{id ? 'Editar modelo' : 'Novo modelo'}</nav>
      <div className="flex items-start gap-3"><span className="mt-0.5 grid size-9 shrink-0 place-items-center rounded-lg bg-primary/10 text-primary"><House className="size-5" aria-hidden="true" /></span><div><h1 className="text-2xl font-semibold tracking-tight">{id ? 'Editar modelo de relatório' : 'Novo modelo de relatório'}</h1><p className="text-muted-foreground">Crie e configure um modelo a partir dos campos disponíveis.</p></div></div></header>
    <div aria-live="polite">{message && <p role="alert" className="rounded-lg border p-4">{message}</p>}</div>
    <p className="sr-only" aria-live="polite" aria-atomic="true">{columnFeedback}</p>
    {revisionConflict && <Button variant="outline" onClick={async () => { const result = await template.refetch(); if (result.data && !result.isError) { setName(result.data.name); setDescription(result.data.description ?? ''); setColumns(result.data.definition.columns); setFilters(result.data.definition.filters ?? []); setParameters(result.data.definition.parameters ?? DEFAULT_PARAMETERS); setRevision(result.data.revision); setRevisionConflict(false); setMessage('Revisão atual carregada; o rascunho anterior foi substituído por sua escolha.'); } }}>Descartar rascunho e recarregar revisão atual</Button>}
    <Tabs value={activeTab} onValueChange={setActiveTab} className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <TabsList className="h-auto w-full justify-start gap-1 overflow-x-auto sm:w-auto">
          <TabsTrigger value="campos">Campos e prévia</TabsTrigger>
          <TabsTrigger value="configuracoes">Configurações</TabsTrigger>
        </TabsList>
        <div className="flex gap-2">
          <Button variant="outline" disabled={save.isPending} onClick={() => navigate('/relatorios/modelos')}>Cancelar</Button>
          <Button onClick={() => save.mutate()} disabled={invalid || revisionConflict || save.isPending || !!id && revision === undefined}>{save.isPending ? 'Salvando…' : 'Salvar modelo'}</Button>
        </div>
      </div>
      <TabsContent value="campos" className="mt-0">
        <div className="grid min-h-[32rem] min-w-0 flex-1 items-stretch gap-4 lg:grid-cols-[minmax(250px,0.28fr)_minmax(0,0.72fr)]">
          <section className="flex min-h-0 flex-col gap-4 overflow-hidden rounded-xl border bg-card p-4" aria-label="Campos disponíveis">
            <div><h2 className="text-lg font-semibold">Campos disponíveis</h2><p className="text-sm text-muted-foreground">Adicione campos às colunas do relatório.</p></div>
            <label className="relative block"><span className="sr-only">Buscar campos</span><Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" /><Input className="pl-9" placeholder="Buscar campos..." value={search} onChange={e => setSearch(e.target.value)} /></label>
            {recommended.length > 0 && <section className="space-y-3 rounded-lg bg-primary/5 p-3" aria-label="Recomendados"><h3 className="text-sm font-semibold text-primary">✦ Recomendados</h3><fieldset className="space-y-2"><legend className="sr-only">Campos recomendados</legend>{recommended.map(field => <label key={field.fieldId} className="flex cursor-pointer items-center gap-2.5 text-sm"><Checkbox checked={columns.some(c => c.fieldId === field.fieldId)} onCheckedChange={checked => toggle(field.fieldId, checked === true)} disabled={save.isPending} /><span>{field.label}</span></label>)}</fieldset></section>}
            <div className="-mx-1 min-h-0 flex-1 divide-y overflow-auto pr-1">
              {groups.map(group => { const groupFields = visible.filter(field => field.group === group); return <details key={group} className="group px-1" open><summary className="flex cursor-pointer list-none items-center justify-between gap-2 py-3 text-sm font-medium [&::-webkit-details-marker]:hidden"><span>{group}</span><span className="flex items-center gap-2 text-xs text-muted-foreground">{groupFields.length} campos<ChevronDown className="size-4 transition-transform group-open:rotate-180" /></span></summary><fieldset className="space-y-2 pb-3 pl-1"><legend className="sr-only">{group}</legend>{groupFields.map(field => <label key={field.fieldId} className="flex cursor-pointer items-center gap-2.5 text-sm"><Checkbox checked={columns.some(c => c.fieldId === field.fieldId)} onCheckedChange={checked => toggle(field.fieldId, checked === true)} disabled={save.isPending} /><span>{field.label}</span></label>)}</fieldset></details>; })}
              {!visible.length && <p className="py-4 text-sm text-muted-foreground">Nenhum campo encontrado.</p>}
            </div>
          </section>
          <div className="flex min-h-0 min-w-0 flex-col gap-4">
            <section className="shrink-0 space-y-4 rounded-xl border bg-card p-4" aria-label="Colunas do modelo">
              <div className="flex flex-wrap items-center justify-between gap-3"><div><h2 className="font-semibold">Colunas do relatório</h2><p className="text-sm text-muted-foreground">Arraste pela alça do cabeçalho para reordenar. O destaque azul indica onde soltar; use as setas do teclado.</p></div><Button type="button" variant="ghost" size="sm" className="text-primary" onClick={() => setColumns([])} disabled={!columns.length || save.isPending}><Trash2 /> Limpar todas</Button></div>
              {!columns.length && <p className="rounded-lg border border-dashed p-4 text-center text-sm text-muted-foreground">Selecione campos à esquerda para montar as colunas do modelo.</p>}
              {new Set(columnLabels).size !== columnLabels.length && <p role="alert" className="text-sm text-destructive">Os títulos das colunas devem ser únicos.</p>}
              <p className="border-t pt-3 text-xs text-muted-foreground">{columns.length} de {Math.min(30, fields.length)} colunas selecionadas.</p>
            </section>
            <section className="flex min-h-[28rem] min-w-0 flex-1 flex-col overflow-hidden rounded-xl border bg-card" aria-label="Prévia do relatório">
              <div className="flex shrink-0 flex-wrap items-center justify-between gap-3 border-b px-4 py-3"><div><h2 className="font-semibold">Prévia do relatório</h2><p className="text-sm text-muted-foreground">{sampleOpen ? 'Amostra real · os dados não são salvos no modelo.' : 'Prévia estrutural · escolha um curso para carregar uma amostra real.'}</p></div><Button type="button" variant="outline" size="sm" onClick={() => setSampleOpen(open => !open)}>{sampleOpen ? 'Fechar amostra' : 'Escolher curso de amostra'}</Button></div>
              {!sampleOpen && <div className="min-h-0 flex-1 overflow-auto p-4"><table className="h-full w-full min-w-max table-auto border-collapse text-left text-sm"><thead className="sticky top-0 z-10"><ReportPreviewColumnHeaders columns={previewColumns} actions={columnActions} /></thead><tbody>{columns.length ? <tr><td colSpan={columns.length} className="border-b border-border/70 px-4 py-12 text-center text-muted-foreground"><p className="font-medium text-foreground">As colunas do modelo estão configuradas.</p><p className="mt-1">Escolha um curso de amostra para ver os dados nas linhas.</p></td></tr> : <tr><td colSpan={1} className="px-3 py-6 text-center text-muted-foreground">Adicione colunas para ver a estrutura do relatório.</td></tr>}</tbody></table></div>}
              {sampleOpen && <div className="min-h-0 flex-1 overflow-auto p-4"><ReportTemplateSample key={`${user?.id}:${id ?? 'new'}`} definition={{ schemaVersion: 1, sourceId: 'student_course', columns, filters, parameters }} fields={fields} columnActions={columnActions} valid={columnsValid && !invalidFilter} /></div>}
            </section>
            <div className="flex shrink-0 items-center gap-2 px-1 text-sm text-muted-foreground"><span>{columns.length} colunas</span></div>
          </div>
        </div>
      </TabsContent>
      <TabsContent value="configuracoes" className="mt-0 space-y-4">
        <section className="space-y-4 rounded-xl border bg-card p-4" aria-label="Identificação do modelo">
          <div><h2 className="font-semibold">Identificação do modelo</h2><p className="text-sm text-muted-foreground">Nome e descrição usados para localizar e entender este relatório.</p></div>
          <div className="grid items-end gap-4 lg:grid-cols-[minmax(240px,1.3fr)_minmax(240px,1fr)]">
            <label className="block space-y-2"><span className="text-sm font-medium">Nome do modelo <span className="text-destructive">*</span></span><Input aria-label="Nome do modelo" value={name} onChange={e => setName(e.target.value)} maxLength={120} disabled={save.isPending} placeholder="Ex.: Acompanhamento da turma" /></label>
            <label className="block space-y-2"><span className="text-sm font-medium">Descrição</span><Textarea value={description} onChange={e => setDescription(e.target.value)} maxLength={500} disabled={save.isPending} rows={2} placeholder="Para que este relatório será usado?" /></label>
          </div>
        </section>
        <section className="space-y-4 rounded-xl border bg-card p-4" aria-label="Parâmetros do relatório">
          <div><h2 className="font-semibold">Parâmetros do relatório</h2><p className="text-sm text-muted-foreground">Defina os limites usados nos indicadores deste modelo.</p></div>
          <div className="grid items-end gap-4 sm:grid-cols-2 xl:grid-cols-[minmax(240px,1fr)_minmax(240px,1fr)]">
            <div className="space-y-2"><label htmlFor="inactive-days-threshold" className="text-sm font-medium">Dias sem acesso para considerar atenção</label><Input id="inactive-days-threshold" aria-describedby="inactive-days-threshold-help" type="number" min={1} max={3650} value={parameters.inactiveDaysThreshold} disabled={save.isPending} onChange={event => setParameters(current => ({ ...current, inactiveDaysThreshold: Number(event.target.value) }))} /><p id="inactive-days-threshold-help" className="text-xs text-muted-foreground">Usado nos indicadores de acompanhamento.</p></div>
            <div className="space-y-2"><label htmlFor="minimum-grade-percent" className="text-sm font-medium">Nota mínima (%)</label><Input id="minimum-grade-percent" aria-describedby="minimum-grade-percent-help" type="number" min={0} max={100} step="0.1" value={parameters.minGradePercent} disabled={save.isPending} onChange={event => setParameters(current => ({ ...current, minGradePercent: Number(event.target.value) }))} /><p id="minimum-grade-percent-help" className="text-xs text-muted-foreground">Usada para sinalizar notas abaixo do limite.</p></div>
          </div>
        </section>
      </TabsContent>
    </Tabs>
  </main>;
}
