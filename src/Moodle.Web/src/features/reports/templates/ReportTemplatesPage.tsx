import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Skeleton } from '@/components/ui/skeleton';
import { useSession } from '../../auth/useSession';
import { ReportTemplatesGate } from './ReportTemplatesGate';
import { ReportSectionHeader } from '../ReportSectionHeader';
import { reportTemplatesGateway, type ReportTemplate } from './report-templates-gateway';

export function ReportTemplatesPage() { return <ReportTemplatesGate><Templates /></ReportTemplatesGate>; }
function Templates() {
  const { user } = useSession();
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<ReportTemplate>();
  const [error, setError] = useState('');
  useEffect(() => { setPage(1); setSelected(undefined); setError(''); }, [user?.id]);
  const queryClient = useQueryClient();
  const models = useQuery({ queryKey: ['report-templates', user?.id, page], queryFn: ({ signal }) => reportTemplatesGateway.list(page, signal), retry: false });
  const remove = useMutation({ mutationFn: (model: ReportTemplate) => reportTemplatesGateway.delete(model.id, model.revision), retry: false,
    onSuccess: async () => { setSelected(undefined); setError(''); await queryClient.invalidateQueries({ queryKey: ['report-templates', user?.id] }); },
    onError: () => setError('Não foi possível excluir. O modelo pode ter sido alterado; atualize a lista para conferir sua revisão.') });
  return <main className="space-y-6 pb-12" aria-labelledby="reports-page-title">
    <ReportSectionHeader active="models" action={<Button asChild><Link to="/relatorios/modelos/novo">Novo modelo</Link></Button>} />
    <section aria-labelledby="report-models-title"><h2 id="report-models-title" className="text-lg font-semibold">Modelos de relatório</h2><p className="text-muted-foreground">Modelos prontos e pessoais. Escolha os cursos ao gerar o relatório.</p></section>
    <p className="text-xs text-muted-foreground">Modelos prontos são copiados para sua lista na primeira visita. Todos podem ser editados ou excluídos sem afetar relatórios já gerados. Acesso, entrega e notas são indicadores para análise da equipe; não determinam sozinhos engajamento, aprovação ou recuperação.</p>
    {models.isPending && <div role="status" aria-busy="true" className="overflow-hidden rounded-xl border bg-card"><span className="sr-only">Carregando modelos…</span><div aria-hidden="true" className="grid grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)] gap-4 bg-muted/40 p-4"><Skeleton className="h-4" /><Skeleton className="h-4" /><Skeleton className="h-4" /><Skeleton className="h-4" /></div>{Array.from({ length: 4 }, (_, index) => <div key={index} aria-hidden="true" className="grid grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)] items-center gap-4 border-t p-4"><Skeleton className="h-4 w-2/3" /><Skeleton className="h-4 w-8" /><Skeleton className="h-4 w-8" /><Skeleton className="h-8 w-24" /></div>)}</div>}
    {models.isError && <p role="alert">Não foi possível carregar os modelos. <Button variant="outline" onClick={() => models.refetch()}>Tentar novamente</Button></p>}
    {models.data && <><div className="overflow-x-auto rounded-xl border bg-card"><table className="w-full text-left text-sm"><thead><tr><th className="p-4">Modelo</th><th className="p-4">Colunas</th><th className="p-4">Revisão</th><th className="p-4">Ações</th></tr></thead><tbody>{models.data.items.map(model => <tr key={model.id} className="border-t"><td className="p-4"><div className="font-medium">{model.name}</div><p className="text-muted-foreground">{model.description}</p></td><td className="p-4">{model.definition.columns.length}</td><td className="p-4">{model.revision}</td><td className="p-4"><div className="flex gap-2"><Button asChild variant="outline" size="sm"><Link aria-label={`Editar ${model.name}`} to={`/relatorios/modelos/${model.id}/editar`}>Editar</Link></Button><Button variant="ghost" size="sm" aria-label={`Excluir ${model.name}`} onClick={() => { setSelected(model); setError(''); }}>Excluir</Button></div></td></tr>)}</tbody></table>{!models.data.items.length && <p className="p-6 text-muted-foreground">Nenhum modelo nesta página.</p>}</div><div className="flex items-center justify-between gap-3"><span className="text-sm text-muted-foreground">{models.data.total} modelos · página {page}</span><div className="flex gap-2"><Button variant="outline" disabled={page === 1} onClick={() => setPage(p => p - 1)}>Anterior</Button><Button variant="outline" disabled={page * 20 >= models.data.total} onClick={() => setPage(p => p + 1)}>Próxima</Button></div></div></>}
    <Dialog open={!!selected} onOpenChange={open => { if (!open && !remove.isPending) setSelected(undefined); }}><DialogContent><DialogHeader><DialogTitle>Excluir modelo?</DialogTitle><DialogDescription>Excluir {selected?.name} preserva os relatórios e arquivos já gerados.</DialogDescription></DialogHeader>{error && <><p role="alert">{error}</p><Button variant="outline" onClick={() => { setSelected(undefined); setError(''); void models.refetch(); }}>Atualizar lista</Button></>}<DialogFooter><Button variant="outline" disabled={remove.isPending} onClick={() => setSelected(undefined)}>Cancelar</Button><Button variant="destructive" disabled={remove.isPending} onClick={() => selected && remove.mutate(selected)}>{remove.isPending ? 'Excluindo…' : 'Confirmar exclusão'}</Button></DialogFooter></DialogContent></Dialog>
  </main>;
}
