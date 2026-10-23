import { Calendar, Clock3, Edit2, Trash2 } from "lucide-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { cn } from "@/lib/utils";
import type { Task, TaskPriority, TaskStatus } from "../tasks-gateway";
import { PlannerReferenceTags } from "../PlannerReferenceTags";
import { tasksGateway } from "../tasks-gateway";

const statusLabels: Record<TaskStatus, string> = {
  todo: "A fazer",
  in_progress: "Em andamento",
  blocked: "Bloqueada",
  done: "Concluída",
  cancelled: "Cancelada",
};

const priorityLabels: Record<TaskPriority, string> = {
  low: "Baixa",
  medium: "Média",
  high: "Alta",
  urgent: "Urgente",
};

const priorityStyles: Record<TaskPriority, string> = {
  low: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300",
  medium: "bg-blue-100 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300",
  high: "bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300",
  urgent: "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300",
};

const statuses: TaskStatus[] = [
  "todo",
  "in_progress",
  "blocked",
  "done",
  "cancelled",
];

function formatDueDate(value?: string) {
  if (!value) return "Sem prazo";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Sem prazo"
    : date.toLocaleDateString("pt-BR");
}

function formatStartDate(value?: string) {
  if (!value) return "Sem início";
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Sem início"
    : date.toLocaleDateString("pt-BR");
}

function isOverdue(task: Task) {
  if (!task.dueAt || task.status === "done") return false;
  const due = new Date(task.dueAt);
  if (Number.isNaN(due.getTime())) return false;
  const today = new Date();
  return (
    due < new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1)
  );
}

type TaskDetailDrawerProps = {
  task: Task | null;
  onClose: () => void;
  onEdit?: (task: Task) => void;
  onDelete?: (id: string) => void;
  onStatusChange?: (id: string, status: TaskStatus) => void;
  onScheduleEvent?: (task: Task) => void;
  onLinkEvent?: (task: Task) => void;
  onDuplicate?: (task: Task) => void;
};

export function TaskDetailDrawer({
  task,
  onClose,
  onEdit,
  onDelete,
  onStatusChange,
  onScheduleEvent,
  onLinkEvent,
  onDuplicate,
}: TaskDetailDrawerProps) {
  const client = useQueryClient();
  const [comment, setComment] = useState("");
  const detailQuery = useQuery({
    queryKey: ["app", "task-detail", task?.id],
    queryFn: () => tasksGateway.detail(task!.id),
    enabled: Boolean(task?.id),
    staleTime: 15_000,
  });
  const activityQuery = useQuery({
    queryKey: ["app", "task-activity", task?.id],
    queryFn: () => tasksGateway.activity(task!.id, 1, 20),
    enabled: Boolean(task?.id),
    staleTime: 15_000,
  });
  const detail = detailQuery.data?.data;
  const addComment = useMutation({
    mutationFn: (content: string) => tasksGateway.addComment(task!.id, content),
    onSuccess: () => {
      setComment("");
      void client.invalidateQueries({
        queryKey: ["app", "task-activity", task?.id],
      });
    },
  });
  const toggleSubtask = useMutation({
    mutationFn: (subtask: Task) =>
      subtask.status === "done"
        ? tasksGateway.reopen(subtask.id, subtask.version)
        : tasksGateway.complete(subtask.id, subtask.version),
    onSuccess: () => {
      void client.invalidateQueries({
        queryKey: ["app", "task-detail", task?.id],
      });
      void client.invalidateQueries({ queryKey: ["app", "tasks"] });
      void client.invalidateQueries({
        queryKey: ["app", "task-activity", task?.id],
      });
    },
  });
  return (
    <Sheet
      open={Boolean(task)}
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
          <SheetTitle>{task?.title}</SheetTitle>
          {task?.description ? (
            <SheetDescription>{task.description}</SheetDescription>
          ) : (
            <SheetDescription>Detalhes da tarefa operacional.</SheetDescription>
          )}
        </SheetHeader>
        {task && (
          <>
            <div className="grid gap-4 border-b px-5 py-4 sm:grid-cols-2">
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Prioridade
                </p>
                <span
                  className={cn(
                    "mt-1 inline-flex rounded-full px-2 py-0.5 text-xs font-medium",
                    priorityStyles[task.priority],
                  )}
                >
                  {priorityLabels[task.priority]}
                </span>
              </div>
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Data de início
                </p>
                <p className="mt-1 flex items-center gap-1 text-xs text-muted-foreground">
                  <Calendar className="h-3.5 w-3.5" />
                  {formatStartDate(task.startAt)}
                </p>
              </div>
              <div>
                <p className="text-xs font-medium text-muted-foreground">
                  Prazo
                </p>
                <p
                  className={cn(
                    "mt-1 flex items-center gap-1 text-xs",
                    isOverdue(task)
                      ? "text-destructive"
                      : "text-muted-foreground",
                  )}
                >
                  <Calendar className="h-3.5 w-3.5" />
                  {formatDueDate(task.dueAt)}
                </p>
              </div>
              {onStatusChange && (
                <label className="grid gap-1.5 text-xs font-medium sm:col-span-2">
                  Status
                  <Select
                    value={task.status}
                    onValueChange={(value) =>
                      onStatusChange(task.id, value as TaskStatus)
                    }
                  >
                    <SelectTrigger className="h-9 text-sm">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {statuses.map((status) => (
                        <SelectItem key={status} value={status}>
                          {statusLabels[status]}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </label>
              )}
            </div>
            <div className="flex-1 space-y-4 overflow-auto px-5 py-5">
              <div>
                <h3 className="text-sm font-semibold">Descrição</h3>
                <p className="mt-2 whitespace-pre-wrap text-sm text-muted-foreground">
                  {task.description || "Esta tarefa não possui descrição."}
                </p>
              </div>
              {task.references?.length ? (
                <div>
                  <h3 className="text-sm font-semibold">Vínculos</h3>
                  <div className="mt-2">
                    <PlannerReferenceTags references={task.references} />
                  </div>
                </div>
              ) : null}
              {detail && (
                <>
                  {detail.tags?.length ? (
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
                  {detail.owner && (
                    <div>
                      <h3 className="text-sm font-semibold">Responsável</h3>
                      <p className="mt-1 text-sm text-muted-foreground">
                        {detail.owner.userId} ·{" "}
                        {detail.participants?.filter(
                          (participant) => participant.role !== "owner",
                        ).length ?? 0}{" "}
                        colaborador(es)
                      </p>
                    </div>
                  )}
                  {detail.subtaskProgress && (
                    <div>
                      <h3 className="text-sm font-semibold">
                        Progresso das subtarefas
                      </h3>
                      <div className="mt-2 h-2 overflow-hidden rounded-full bg-muted">
                        <div
                          className="h-full bg-primary transition-all"
                          style={{
                            width: `${detail.subtaskProgress.percent}%`,
                          }}
                        />
                      </div>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {detail.subtaskProgress.done}/
                        {detail.subtaskProgress.total} concluídas (
                        {detail.subtaskProgress.percent}%)
                      </p>
                    </div>
                  )}
                  {detail.subtasks?.length ? (
                    <div>
                      <h3 className="text-sm font-semibold">Subtarefas</h3>
                      <ul className="mt-2 space-y-1 text-sm">
                        {detail.subtasks.map((subtask) => (
                          <li
                            key={subtask.id}
                            className={cn(
                              "flex items-center justify-between rounded border px-2 py-1",
                              subtask.status === "done" &&
                                "text-muted-foreground line-through",
                            )}
                          >
                            <span className="flex min-w-0 items-center gap-2">
                              <Button
                                type="button"
                                variant="ghost"
                                size="sm"
                                className="h-6 w-6 shrink-0 p-0 text-base"
                                aria-label={
                                  subtask.status === "done"
                                    ? `Reabrir ${subtask.title}`
                                    : `Concluir ${subtask.title}`
                                }
                                disabled={toggleSubtask.isPending}
                                onClick={() => toggleSubtask.mutate(subtask)}
                              >
                                {subtask.status === "done" ? "✓" : "○"}
                              </Button>
                              <span className="truncate">{subtask.title}</span>
                            </span>
                            <span className="text-xs text-muted-foreground">
                              {statusLabels[subtask.status]}
                            </span>
                          </li>
                        ))}
                      </ul>
                    </div>
                  ) : null}
                  {detail.dependsOn?.length || detail.blocks?.length ? (
                    <div>
                      <h3 className="text-sm font-semibold">Dependências</h3>
                      <p className="mt-1 text-xs text-muted-foreground">
                        Bloqueada por: {detail.dependsOn?.length ?? 0} ·
                        Bloqueia: {detail.blocks?.length ?? 0}
                      </p>
                    </div>
                  ) : null}
                  {detail.events?.length ? (
                    <div>
                      <h3 className="text-sm font-semibold">
                        Events relacionados
                      </h3>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {detail.events.length} vínculo(s) explícito(s)
                      </p>
                    </div>
                  ) : null}
                  {activityQuery.data?.data && (
                    <div>
                      <h3 className="text-sm font-semibold">Timeline</h3>
                      <div className="mt-2 space-y-2">
                        {[
                          ...activityQuery.data.data.comments.map(
                            (comment) => ({
                              at: comment.createdAt,
                              text: comment.content,
                              kind: "Comentário",
                            }),
                          ),
                          ...activityQuery.data.data.activities.map(
                            (activity) => ({
                              at: activity.createdAt,
                              text: activity.eventType,
                              kind: "Histórico",
                            }),
                          ),
                        ]
                          .sort(
                            (a, b) =>
                              new Date(b.at).getTime() -
                              new Date(a.at).getTime(),
                          )
                          .slice(0, 8)
                          .map((item) => (
                            <div
                              key={`${item.kind}-${item.at}-${item.text}`}
                              className="rounded border px-2 py-1.5 text-xs"
                            >
                              <span className="font-medium">{item.kind}</span>
                              <span className="ml-2 text-muted-foreground">
                                {new Date(item.at).toLocaleString("pt-BR")}
                              </span>
                              <p className="mt-1 text-sm">{item.text}</p>
                            </div>
                          ))}
                      </div>
                    </div>
                  )}
                  <div>
                    <h3 className="text-sm font-semibold">
                      Adicionar comentário
                    </h3>
                    <div className="mt-2 flex gap-2">
                      <textarea
                        value={comment}
                        onChange={(event) => setComment(event.target.value)}
                        maxLength={4000}
                        placeholder="Escreva uma atualização para a equipe…"
                        className="min-h-16 flex-1 rounded-md border bg-background p-2 text-sm"
                      />
                      <Button
                        type="button"
                        size="sm"
                        className="self-end"
                        disabled={!comment.trim() || addComment.isPending}
                        onClick={() => addComment.mutate(comment.trim())}
                      >
                        {addComment.isPending ? "Enviando…" : "Comentar"}
                      </Button>
                    </div>
                  </div>
                </>
              )}
              <div className="rounded-lg bg-muted/50 p-3 text-xs text-muted-foreground">
                <p className="flex items-center gap-1.5">
                  <Clock3 className="h-3.5 w-3.5" />
                  Status: {statusLabels[task.status]}
                </p>
                <p className="mt-1">
                  Criada em {new Date(task.createdAt).toLocaleString("pt-BR")}
                </p>
                <p className="mt-1">
                  Atualizada em{" "}
                  {new Date(task.updatedAt).toLocaleString("pt-BR")}
                </p>
              </div>
            </div>
            {(onEdit || onDelete || onScheduleEvent || onLinkEvent || onDuplicate) && (
              <div className="sticky bottom-0 flex flex-wrap gap-2 border-t bg-background px-5 py-4">
                {onEdit && (
                  <Button
                    type="button"
                    className="flex-1"
                    onClick={() => onEdit(task)}
                  >
                    <Edit2 className="mr-2 h-4 w-4" />
                    Editar tarefa
                  </Button>
                )}
                {onDuplicate && (
                  <Button type="button" variant="outline" onClick={() => onDuplicate(task)}>
                    Duplicar
                  </Button>
                )}
                {onScheduleEvent && (
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => onScheduleEvent(task)}
                  >
                    Agendar Event
                  </Button>
                )}
                {onLinkEvent && (
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => onLinkEvent(task)}
                  >
                    Vincular Event existente
                  </Button>
                )}
                {onDelete && (
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => onDelete(task.id)}
                  >
                    <Trash2 className="mr-2 h-4 w-4" />
                    Remover
                  </Button>
                )}
              </div>
            )}
          </>
        )}
      </SheetContent>
    </Sheet>
  );
}
