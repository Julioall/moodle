import { CalendarDays, Clock3, Edit2, Trash2 } from "lucide-react";
import { useQuery } from "@tanstack/react-query";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { PlannerReferenceTags } from "@/features/tasks/PlannerReferenceTags";
import type { AgendaEvent } from "../agenda-gateway";
import { agendaGateway } from "../agenda-gateway";

const eventTypeLabels: Record<string, string> = {
  manual: "Compromisso",
  meeting: "Reunião",
  alignment: "Alinhamento",
  delivery: "Entrega",
  training: "Formação",
  webclass: "WebAula",
  class: "Aula",
  deadline: "Prazo",
};

function formatDateTime(value?: string) {
  if (!value) return "Não informado";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Não informado"
    : date.toLocaleString("pt-BR", { dateStyle: "medium", timeStyle: "short" });
}

type AgendaEventDetailDrawerProps = {
  event: AgendaEvent | null;
  onClose: () => void;
  onEdit: (event: AgendaEvent) => void;
  onDelete: (id: string) => void;
  onCreateTask?: (id: string) => void;
  onLinkTask?: (id: string) => void;
  onDuplicate?: (event: AgendaEvent) => void;
};

export function AgendaEventDetailDrawer({
  event,
  onClose,
  onEdit,
  onDelete,
  onCreateTask,
  onLinkTask,
  onDuplicate,
}: AgendaEventDetailDrawerProps) {
  const detailQuery = useQuery({
    queryKey: ["app", "agenda-detail", event?.id],
    queryFn: () => agendaGateway.detail(event!.id),
    enabled: Boolean(event?.id),
    staleTime: 15_000,
  });
  const linksQuery = useQuery({
    queryKey: ["app", "agenda-links", event?.id],
    queryFn: () => agendaGateway.relatedTasks(event!.id),
    enabled: Boolean(event?.id),
    staleTime: 15_000,
  });
  const detail = detailQuery.data?.data ?? event;
  return (
    <Sheet
      open={Boolean(event)}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <SheetContent
        side="right"
        hideOverlay
        className="flex w-full flex-col gap-0 border-l bg-background p-0 shadow-xl sm:max-w-[32rem]"
      >
        <SheetHeader className="border-b px-5 pb-4 pt-5 text-left">
          <div className="flex items-start gap-3 pr-7">
            <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full border bg-primary/10 text-primary">
              <CalendarDays className="h-4 w-4" />
            </div>
            <div className="min-w-0">
              <SheetTitle>{event?.title}</SheetTitle>
              <SheetDescription>
                {event?.description
                  ? "Detalhes deste compromisso."
                  : "Compromisso da agenda."}
              </SheetDescription>
            </div>
          </div>
        </SheetHeader>
        {event && (
          <>
            <div className="grid gap-4 border-b px-5 py-4 sm:grid-cols-2">
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Tipo
                </p>
                <Badge variant="outline" className="mt-1">
                  {eventTypeLabels[event.type] ?? event.type}
                </Badge>
              </div>
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Início
                </p>
                <p className="mt-1 flex items-center gap-1 text-xs text-muted-foreground">
                  <CalendarDays className="h-3.5 w-3.5" />
                  {formatDateTime(event.startAt)}
                </p>
              </div>
              <div className="sm:col-span-2">
                <p className="text-xs font-medium text-muted-foreground">Fim</p>
                <p className="mt-1 flex items-center gap-1 text-xs text-muted-foreground">
                  <Clock3 className="h-3.5 w-3.5" />
                  {formatDateTime(event.endAt)}
                </p>
              </div>
            </div>
            <div className="flex-1 space-y-4 overflow-auto px-5 py-5">
              <div>
                <h3 className="text-sm font-semibold">Descrição</h3>
                <p className="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">
                  {detail?.description || "Este evento não possui descrição."}
                </p>
              </div>
              {detail?.tags?.length ? (
                <div>
                  <h3 className="text-sm font-semibold">Tags</h3>
                  <div className="mt-2 flex flex-wrap gap-1.5">
                    {detail.tags.map((tag) => (
                      <Badge key={tag} variant="secondary">
                        #{tag}
                      </Badge>
                    ))}
                  </div>
                </div>
              ) : null}
              {event.references?.length ? (
                <div>
                  <h3 className="text-sm font-semibold">Vínculos</h3>
                  <div className="mt-2">
                    <PlannerReferenceTags references={event.references} />
                  </div>
                </div>
              ) : null}
              {(detail?.location ||
                detail?.rRule ||
                detail?.availabilityStatus) && (
                <div className="grid gap-2 rounded-lg bg-muted/50 p-3 text-xs text-muted-foreground">
                  <p>
                    <strong>Disponibilidade:</strong>{" "}
                    {detail.availabilityStatus === "free"
                      ? "Livre"
                      : detail.availabilityStatus === "tentative"
                        ? "Provisório"
                        : "Ocupado"}
                  </p>
                  {detail.location && (
                    <p>
                      <strong>Local:</strong> {detail.location}
                    </p>
                  )}
                  {detail.rRule && (
                    <p>
                      <strong>Recorrência:</strong> {detail.rRule}
                    </p>
                  )}
                </div>
              )}
              {linksQuery.data?.data?.length ? (
                <div>
                  <h3 className="text-sm font-semibold">Tasks relacionadas</h3>
                  <ul className="mt-2 space-y-1 text-xs text-muted-foreground">
                    {linksQuery.data.data.map((link) => (
                      <li key={link.id} className="rounded border px-2 py-1">
                        {link.taskId} · {link.relation}
                        {link.occurrenceStartAt
                          ? ` · ${formatDateTime(link.occurrenceStartAt)}`
                          : ""}
                      </li>
                    ))}
                  </ul>
                </div>
              ) : null}
              <div className="rounded-lg bg-muted/50 p-3 text-xs text-muted-foreground">
                <p>Criado em {formatDateTime(event.createdAt)}</p>
                <p className="mt-1">
                  Atualizado em {formatDateTime(event.updatedAt)}
                </p>
              </div>
            </div>
            <div className="sticky bottom-0 flex flex-wrap gap-2 border-t bg-background px-5 py-4">
              <Button
                type="button"
                className="flex-1"
                onClick={() => onEdit(event)}
              >
                <Edit2 className="mr-2 h-4 w-4" />
                Editar evento
              </Button>
              {onDuplicate && (
                <Button type="button" variant="outline" onClick={() => onDuplicate(event)}>
                  Duplicar
                </Button>
              )}
              {onCreateTask && (
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => onCreateTask(event.id)}
                >
                  Criar Task
                </Button>
              )}
              {onLinkTask && (
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => onLinkTask(event.id)}
                >
                  Vincular Task existente
                </Button>
              )}
              <Button
                type="button"
                variant="outline"
                onClick={() => onDelete(event.id)}
              >
                <Trash2 className="mr-2 h-4 w-4" />
                Remover
              </Button>
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}
