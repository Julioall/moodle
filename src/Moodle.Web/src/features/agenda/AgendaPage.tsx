import { type FormEvent, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AlertCircle,
  CalendarDays,
  CalendarRange,
  CheckSquare,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Download,
  Copy,
  Edit2,
  List,
  ListFilter,
  Plus,
  Upload,
} from "lucide-react";
import { toast } from "sonner";

import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import { TaskDetailDrawer } from "../tasks/components/TaskDetailDrawer";
import { AgendaEventDetailDrawer } from "./components/AgendaEventDetailDrawer";
import {
  agendaGateway,
  type AgendaEvent,
  type AgendaInput,
} from "./agenda-gateway";
import {
  tasksGateway,
  type PlannerReference,
  type Task,
  type TaskPriority,
  type TaskStatus,
} from "../tasks/tasks-gateway";
import { PlannerReferenceTags } from "../tasks/PlannerReferenceTags";
import { TagInput, type TagSuggestion } from "../tasks/TagInput";
import { coursesGateway } from "../courses/courses-gateway";
import { studentsGateway } from "../students/students-gateway";

type ViewMode = "calendar" | "list";
type RecurrenceEditScope = "series" | "occurrence";
type AgendaItem =
  | { kind: "event"; id: string; date: string; event: AgendaEvent }
  | { kind: "task"; id: string; date: string; task: Task };

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

function dateKey(value: string | Date) {
  const date = typeof value === "string" ? new Date(value) : value;
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function formatDate(value: string, options?: Intl.DateTimeFormatOptions) {
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? "Data indisponível"
    : date.toLocaleString(
        "pt-BR",
        options ?? { dateStyle: "medium", timeStyle: "short" },
      );
}

function formatGroupLabel(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "Sem data";
  return date.toLocaleDateString("pt-BR", {
    weekday: "long",
    day: "numeric",
    month: "long",
  });
}

function toDateTimeLocal(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function parseIsoList(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => new Date(item))
    .filter((date) => !Number.isNaN(date.getTime()))
    .map((date) => date.toISOString());
}

function parseReferences(value: string): PlannerReference[] {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => {
      const [referenceType, ...rest] = item.split(":");
      return {
        referenceType: referenceType?.trim() || "custom",
        referenceId: rest.join(":").trim(),
      };
    })
    .filter((reference) => reference.referenceId);
}

function AgendaEventCard({
  event,
  onOpen,
  onEdit,
  onDuplicate,
}: {
  event: AgendaEvent;
  onOpen: (event: AgendaEvent) => void;
  onEdit?: (event: AgendaEvent) => void;
  onDuplicate?: (event: AgendaEvent) => void;
}) {
  return (
    <article
      className="group cursor-pointer border-b bg-card px-3 py-3 last:border-b-0 transition-colors hover:bg-primary/[0.03] sm:px-4"
      onClick={() => onOpen(event)}
    >
      <div className="grid items-start gap-3 sm:grid-cols-[4.25rem,2.5rem,minmax(0,1fr),auto]">
        <div className="text-xs text-muted-foreground">
          <p className="font-semibold text-foreground">
            {new Date(event.startAt).toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" })}
          </p>
          <p>
            {event.endAt
              ? `${Math.max(1, Math.round((new Date(event.endAt).getTime() - new Date(event.startAt).getTime()) / 60000))} min`
              : "—"}
          </p>
        </div>
        <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-md border border-primary/20 bg-primary/10 text-primary">
          <CalendarDays className="h-4 w-4" />
        </div>
        <div className="min-w-0">
          <div className="flex flex-wrap items-start gap-2">
            <h3 className="font-semibold leading-5">{event.title}</h3>
            <Badge variant="outline" className="text-[11px]">
              {eventTypeLabels[event.type] ?? event.type}
            </Badge>
          </div>
          {event.description && (
            <p className="mt-1 line-clamp-1 text-xs text-muted-foreground">
              {event.description}
            </p>
          )}
          {event.location && (
            <p className="mt-2 text-xs text-muted-foreground">
              Local: {event.location}
            </p>
          )}
          {event.rRule && (
            <p className="mt-1 text-xs text-muted-foreground">
              Recorrente · {event.rRule}
            </p>
          )}
          {event.tags?.length ? (
            <div className="mt-2 flex flex-wrap gap-1">
              {event.tags.slice(0, 4).map((tag) => (
                <Badge
                  key={tag}
                  variant="secondary"
                  className="px-1.5 py-0 text-[10px]"
                >
                  #{tag}
                </Badge>
              ))}
            </div>
          ) : null}
          <PlannerReferenceTags references={event.references} compact />
        </div>
        <div className="flex items-center justify-end gap-1 opacity-70 transition-opacity group-hover:opacity-100">
            {onEdit && (
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="h-7 w-7 opacity-70 hover:opacity-100"
                aria-label={`Editar ${event.title}`}
                onClick={(clickEvent) => {
                  clickEvent.stopPropagation();
                  onEdit(event);
                }}
              >
                <Edit2 className="h-3.5 w-3.5" />
              </Button>
            )}
            {onDuplicate && (
              <Button
                type="button"
                variant="ghost"
                size="icon"
                className="h-7 w-7 opacity-70 hover:opacity-100"
                aria-label={`Duplicar ${event.title}`}
                onClick={(clickEvent) => {
                  clickEvent.stopPropagation();
                  onDuplicate(event);
                }}
              >
                <Copy className="h-3.5 w-3.5" />
              </Button>
            )}
            <Button
              type="button"
              variant="ghost"
              size="sm"
              className="text-primary"
              onClick={() => onOpen(event)}
            >
              Abrir detalhes
            </Button>
        </div>
      </div>
    </article>
  );
}

function AgendaTaskCard({
  task,
  onClick,
}: {
  task: Task;
  onClick: (task: Task) => void;
}) {
  return (
    <button
      type="button"
      onClick={() => onClick(task)}
      className="group flex w-full items-start gap-3 rounded-lg border border-status-warning/25 bg-card px-3 py-3 text-left transition-colors hover:border-primary/30 hover:bg-muted/20"
    >
      <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-md border bg-status-warning/10 text-status-warning">
        <CheckSquare className="h-4 w-4" />
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div>
            <p
              className={cn(
                "font-semibold leading-5",
                task.status === "done" && "text-muted-foreground line-through",
              )}
            >
              {task.title}
            </p>
            <p className="mt-1 flex items-center gap-1.5 text-sm text-muted-foreground">
              <Clock3 className="h-3.5 w-3.5" />
              {task.dueAt ? formatDate(task.dueAt) : "Sem prazo"}
            </p>
          </div>
          <Badge variant="outline" className={`priority-${task.priority}`}>
            {priorityLabels[task.priority]}
          </Badge>
        </div>
        {task.description && (
          <p className="mt-3 text-sm text-muted-foreground">
            {task.description}
          </p>
        )}
        <div className="mt-3 flex items-center justify-between gap-3 border-t pt-3">
          <Badge variant="secondary">{statusLabels[task.status]}</Badge>
          <span className="text-sm font-medium text-primary">
            Abrir detalhes
          </span>
        </div>
      </div>
    </button>
  );
}

function MonthCalendar({
  month,
  items,
  onCreateOnDate,
  onSetMonth,
  onSetYear,
  onNavigate,
  onToday,
  onOpenTask,
  onOpenEvent,
}: {
  month: Date;
  items: AgendaItem[];
  onCreateOnDate: (date: Date) => void;
  onSetMonth: (month: number) => void;
  onSetYear: (year: number) => void;
  onNavigate: (offset: number) => void;
  onToday: () => void;
  onOpenTask: (task: Task) => void;
  onOpenEvent: (event: AgendaEvent) => void;
}) {
  const [selectedDate, setSelectedDate] = useState<Date | null>(null);
  const firstDay = new Date(month.getFullYear(), month.getMonth(), 1);
  const daysInMonth = new Date(
    month.getFullYear(),
    month.getMonth() + 1,
    0,
  ).getDate();
  const leadingDays = firstDay.getDay();
  const cellCount = Math.ceil((leadingDays + daysInMonth) / 7) * 7;
  const cells = Array.from(
    { length: cellCount },
    (_, index) =>
      new Date(month.getFullYear(), month.getMonth(), index - leadingDays + 1),
  );
  const itemsByDay = useMemo(() => {
    const grouped = new Map<string, AgendaItem[]>();
    items.forEach((item) =>
      grouped.set(dateKey(item.date), [
        ...(grouped.get(dateKey(item.date)) ?? []),
        item,
      ]),
    );
    return grouped;
  }, [items]);
  const today = dateKey(new Date());
  const selectedItems = selectedDate
    ? (itemsByDay.get(dateKey(selectedDate)) ?? [])
    : [];
  const months = Array.from({ length: 12 }, (_, index) =>
    new Date(2026, index, 1).toLocaleDateString("pt-BR", { month: "long" }),
  );
  const years = Array.from(
    { length: 5 },
    (_, index) => month.getFullYear() - 2 + index,
  );
  const selectMonth = (value: string) => {
    setSelectedDate(null);
    onSetMonth(Number(value));
  };
  const selectYear = (value: string) => {
    setSelectedDate(null);
    onSetYear(Number(value));
  };
  const goToday = () => {
    setSelectedDate(new Date());
    onToday();
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-2">
          <Select value={String(month.getMonth())} onValueChange={selectMonth}>
            <SelectTrigger
              aria-label="Selecionar mês"
              className="h-8 w-40 text-xs"
            >
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {months.map((label, index) => (
                <SelectItem key={label} value={String(index)}>
                  {label.charAt(0).toUpperCase() + label.slice(1)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Select
            value={String(month.getFullYear())}
            onValueChange={selectYear}
          >
            <SelectTrigger
              aria-label="Selecionar ano"
              className="h-8 w-28 text-xs"
            >
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {years.map((year) => (
                <SelectItem key={year} value={String(year)}>
                  {year}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex items-center gap-1">
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="h-8 px-2 text-xs"
            onClick={goToday}
          >
            Hoje
          </Button>
          <Button
            type="button"
            size="icon"
            variant="outline"
            className="h-8 w-8"
            onClick={() => {
              setSelectedDate(null);
              onNavigate(-1);
            }}
            aria-label="Mês anterior"
          >
            <ChevronLeft className="h-4 w-4" />
          </Button>
          <Button
            type="button"
            size="icon"
            variant="outline"
            className="h-8 w-8"
            onClick={() => {
              setSelectedDate(null);
              onNavigate(1);
            }}
            aria-label="Próximo mês"
          >
            <ChevronRight className="h-4 w-4" />
          </Button>
        </div>
      </div>
      <div className="overflow-hidden rounded-lg border">
        <div className="grid grid-cols-7 border-b bg-muted/40 text-center text-[11px] font-medium text-muted-foreground">
          {["Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb"].map((day) => (
            <div key={day} className="py-2">
              {day}
            </div>
          ))}
        </div>
        <div className="grid grid-cols-7">
          {cells.map((date, index) => {
            const dayItems = itemsByDay.get(dateKey(date)) ?? [];
            const isSelected = selectedDate
              ? dateKey(selectedDate) === dateKey(date)
              : false;
            const isCurrentMonthDay = date.getMonth() === month.getMonth();
            return (
              <div
                key={dateKey(date)}
                className={cn(
                  "group relative min-h-[80px] cursor-pointer border-b border-r p-1 transition-colors hover:bg-muted/40",
                  !isCurrentMonthDay && "bg-muted/20",
                  (index + 1) % 7 === 0 && "border-r-0",
                  dateKey(date) === today &&
                    "ring-2 ring-primary/30 ring-inset",
                  isSelected && "bg-primary/5",
                )}
                onClick={() =>
                  setSelectedDate((current) =>
                    current && dateKey(current) === dateKey(date) ? null : date,
                  )
                }
              >
                <div className="flex items-center justify-between">
                  <span
                    className={cn(
                      "inline-flex h-5 w-5 items-center justify-center rounded-full text-[11px] font-medium",
                      dateKey(date) === today &&
                        "bg-primary text-primary-foreground",
                      dateKey(date) !== today &&
                        !isCurrentMonthDay &&
                        "text-muted-foreground/50",
                    )}
                  >
                    {date.getDate()}
                  </span>
                  {isCurrentMonthDay && (
                    <Button
                      type="button"
                      size="icon"
                      variant="ghost"
                      className="h-4 w-4 opacity-0 transition-opacity group-hover:opacity-100"
                      onClick={(event) => {
                        event.stopPropagation();
                        onCreateOnDate(date);
                      }}
                      title="Novo evento"
                    >
                      <Plus className="h-3 w-3" />
                    </Button>
                  )}
                </div>
                <div className="mt-0.5 space-y-0.5">
                  {dayItems.slice(0, 3).map((item) => (
                    <button
                      key={`${item.kind}-${item.id}`}
                      type="button"
                      className={cn(
                        "block w-full truncate rounded border px-1 py-0.5 text-left text-[10px] font-medium transition-opacity hover:opacity-80",
                        item.kind === "task"
                          ? "border-status-warning/20 bg-status-warning/10 text-status-warning"
                          : "border-primary/20 bg-primary/10 text-primary",
                      )}
                      onClick={(event) => {
                        event.stopPropagation();
                        setSelectedDate(date);
                        if (item.kind === "task") onOpenTask(item.task);
                        else onOpenEvent(item.event);
                      }}
                      title={
                        item.kind === "task"
                          ? item.task.title
                          : item.event.title
                      }
                    >
                      {item.kind === "task"
                        ? item.task.title
                        : item.event.title}
                    </button>
                  ))}
                  {dayItems.length > 3 && (
                    <span className="block pl-1 text-[10px] text-muted-foreground">
                      +{dayItems.length - 3} mais
                    </span>
                  )}
                </div>
              </div>
            );
          })}
        </div>
      </div>
      {selectedDate && (
        <div className="space-y-3 rounded-lg border p-4">
          <div className="flex items-center justify-between gap-3">
            <h3 className="text-sm font-semibold">
              {selectedDate.toLocaleDateString("pt-BR", {
                weekday: "long",
                day: "numeric",
                month: "long",
              })}
            </h3>
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="h-7 gap-1 text-xs"
              onClick={() => onCreateOnDate(selectedDate)}
            >
              <Plus className="h-3 w-3" />
              Novo evento
            </Button>
          </div>
          {selectedItems.length === 0 ? (
            <p className="text-xs text-muted-foreground">
              Nenhum compromisso neste dia.
            </p>
          ) : (
            <div className="space-y-2">
              {selectedItems.map((item) => (
                <div
                  key={`${item.kind}-${item.id}`}
                  className="flex w-full items-start gap-3 rounded-lg border bg-card p-3"
                >
                  <div
                    className={cn(
                      "flex h-7 w-7 shrink-0 items-center justify-center rounded-full border",
                      item.kind === "task"
                        ? "border-status-warning/20 bg-status-warning/10 text-status-warning"
                        : "border-primary/20 bg-primary/10 text-primary",
                    )}
                  >
                    {item.kind === "task" ? (
                      <CheckSquare className="h-3.5 w-3.5" />
                    ) : (
                      <CalendarDays className="h-3.5 w-3.5" />
                    )}
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-start justify-between gap-2">
                      <p className="text-sm font-medium">
                        {item.kind === "task"
                          ? item.task.title
                          : item.event.title}
                      </p>
                    </div>
                    {(item.kind === "task"
                      ? item.task.description
                      : item.event.description) && (
                      <p className="line-clamp-1 text-xs text-muted-foreground">
                        {item.kind === "task"
                          ? item.task.description
                          : item.event.description}
                      </p>
                    )}
                    <div className="mt-1 flex flex-wrap items-center gap-2">
                      <span className="text-xs text-muted-foreground">
                        {item.kind === "task"
                          ? `${statusLabels[item.task.status]} · ${priorityLabels[item.task.priority]}`
                          : formatDate(item.event.startAt, {
                              hour: "2-digit",
                              minute: "2-digit",
                            })}
                      </span>
                      <Badge
                        variant="outline"
                        className={cn(
                          "px-1.5 py-0 text-[10px]",
                          item.kind === "task"
                            ? "border-status-warning/20 text-status-warning"
                            : "border-primary/20 text-primary",
                        )}
                      >
                        {item.kind === "task"
                          ? "Tarefa"
                          : (eventTypeLabels[item.event.type] ??
                            item.event.type)}
                      </Badge>
                      <button
                        type="button"
                        className="text-xs font-medium text-primary hover:underline"
                        onClick={() =>
                          item.kind === "task"
                            ? onOpenTask(item.task)
                            : onOpenEvent(item.event)
                        }
                      >
                        Abrir detalhes
                      </button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function AgendaPage() {
  const client = useQueryClient();
  const [viewMode, setViewMode] = useState<ViewMode>("list");
  const [month, setMonth] = useState(() => new Date());
  const [eventSearch, setEventSearch] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [tagFilter, setTagFilter] = useState("");
  const [referenceFilter, setReferenceFilter] = useState("");
  const monthFrom = new Date(
    month.getFullYear(),
    month.getMonth(),
    1,
  ).toISOString();
  const monthTo = new Date(
    month.getFullYear(),
    month.getMonth() + 1,
    1,
  ).toISOString();
  const eventsQuery = useQuery({
    queryKey: ["app", "agenda", monthFrom, monthTo, tagFilter, referenceFilter],
    queryFn: () =>
      agendaGateway.list(
        monthFrom,
        monthTo,
        tagFilter.trim() || undefined,
        undefined,
        parseReferences(referenceFilter)[0]?.referenceType,
        parseReferences(referenceFilter)[0]?.referenceId,
      ),
    staleTime: 30_000,
  });
  const tasksQuery = useQuery({
    queryKey: ["app", "tasks", "all"],
    queryFn: () => tasksGateway.list(1, 100),
    staleTime: 30_000,
  });
  const schoolsQuery = useQuery({
    queryKey: ["app", "agenda", "tag-schools"],
    queryFn: () => coursesGateway.hierarchy(),
    staleTime: 60_000,
  });
  const coursesQuery = useQuery({
    queryKey: ["app", "agenda", "tag-courses"],
    queryFn: () => coursesGateway.listAll(undefined, 100),
    staleTime: 60_000,
  });
  const [formOpen, setFormOpen] = useState(false);
  const [editingEvent, setEditingEvent] = useState<AgendaEvent | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [detailTask, setDetailTask] = useState<Task | null>(null);
  const [detailEvent, setDetailEvent] = useState<AgendaEvent | null>(null);
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [startAt, setStartAt] = useState("");
  const [endAt, setEndAt] = useState("");
  const [type, setType] = useState("manual");
  const [timeZoneId, setTimeZoneId] = useState("America/Sao_Paulo");
  const [location, setLocation] = useState("");
  const [availabilityStatus, setAvailabilityStatus] = useState<
    "free" | "busy" | "tentative"
  >("busy");
  const [isAllDay, setIsAllDay] = useState(false);
  const [tags, setTags] = useState<string[]>([]);
  const [rRule, setRRule] = useState("");
  const [exDates, setExDates] = useState("");
  const [rDates, setRDates] = useState("");
  const [referencesInput, setReferencesInput] = useState("");
  const [recurrenceEditScope, setRecurrenceEditScope] =
    useState<RecurrenceEditScope>("series");
  const courseItems = useMemo(
    () => coursesQuery.data?.data ?? [],
    [coursesQuery.data?.data],
  );
  const studentCourse = courseItems[0];
  const studentsQuery = useQuery({
    queryKey: ["app", "agenda", "tag-students", studentCourse?.connectionRef, studentCourse?.courseId],
    queryFn: () => studentsGateway.byCourse(studentCourse!.connectionRef, studentCourse!.courseId, 1, 100),
    enabled: Boolean(studentCourse),
    staleTime: 60_000,
  });
  const tagSuggestions = useMemo<TagSuggestion[]>(() => {
    return [
      ...(schoolsQuery.data?.data ?? []).map((school) => ({ value: school.name, label: school.name, category: "school" as const, hint: `${school.courseCount} cursos` })),
      ...courseItems.map((course) => ({ value: course.fullName, label: course.fullName, category: "course" as const, hint: course.categoryName ?? "Curso" })),
      ...(studentsQuery.data?.data ?? []).map((student) => ({ value: student.name, label: student.name, category: "student" as const, hint: studentCourse?.fullName ?? "Aluno" })),
    ];
  }, [courseItems, schoolsQuery.data?.data, studentsQuery.data?.data, studentCourse?.fullName]);
  const fileInput = useRef<HTMLInputElement>(null);
  const agendaItems = useMemo<AgendaItem[]>(
    () =>
      [
        ...(eventsQuery.data?.data ?? []).map((event) => ({
          kind: "event" as const,
          id: event.id,
          date: event.startAt,
          event,
        })),
        ...(tasksQuery.data?.data ?? [])
          .filter((task) => Boolean(task.dueAt))
          .map((task) => ({
            kind: "task" as const,
            id: task.id,
            date: task.dueAt!,
            task,
          })),
      ].filter((item) => {
        const query = eventSearch.trim().toLocaleLowerCase("pt-BR");
        if (!query) return true;
        const entity = item.kind === "event" ? item.event : item.task;
        return `${entity.title} ${entity.description ?? ""} ${(entity.tags ?? []).join(" ")}`
          .toLocaleLowerCase("pt-BR")
          .includes(query);
      }),
    [eventSearch, eventsQuery.data?.data, tasksQuery.data?.data],
  );
  const sortedItems = useMemo(
    () =>
      [...agendaItems].sort(
        (left, right) =>
          new Date(left.date).getTime() - new Date(right.date).getTime(),
      ),
    [agendaItems],
  );
  const groupedItems = useMemo(
    () =>
      sortedItems.reduce<Record<string, AgendaItem[]>>((groups, item) => {
        const key = dateKey(item.date);
        groups[key] = [...(groups[key] ?? []), item];
        return groups;
      }, {}),
    [sortedItems],
  );
  const resetForm = () => {
    setTitle("");
    setDescription("");
    setStartAt("");
    setEndAt("");
    setType("manual");
    setTimeZoneId("America/Sao_Paulo");
    setLocation("");
    setAvailabilityStatus("busy");
    setIsAllDay(false);
    setTags([]);
    setRRule("");
    setExDates("");
    setRDates("");
    setReferencesInput("");
    setRecurrenceEditScope("series");
    setEditingEvent(null);
  };
  const refreshPlannerCounters = () =>
    void client.invalidateQueries({
      queryKey: ["app", "dashboard", "summary"],
    });
  const handleSaved = () => {
    resetForm();
    setFormOpen(false);
    void client.invalidateQueries({ queryKey: ["app", "agenda"] });
    refreshPlannerCounters();
  };
  const create = useMutation({
    mutationFn: (input: AgendaInput) => agendaGateway.create(input),
    onSuccess: handleSaved,
    onError: (error) =>
      toast.error("Não foi possível criar o evento", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const update = useMutation({
    mutationFn: ({ id, input }: { id: string; input: AgendaInput }) =>
      agendaGateway.update(id, input),
    onSuccess: handleSaved,
    onError: (error) =>
      toast.error("Não foi possível atualizar o evento", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const updateOccurrence = useMutation({
    mutationFn: ({
      id,
      originalStartAt,
      input,
    }: {
      id: string;
      originalStartAt: string;
      input: {
        title?: string;
        description?: string;
        startAt?: string;
        endAt?: string;
      };
    }) => agendaGateway.updateOccurrence(id, originalStartAt, input),
    onSuccess: handleSaved,
    onError: (error) =>
      toast.error("Não foi possível atualizar a ocorrência", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const remove = useMutation({
    mutationFn: agendaGateway.remove,
    onSuccess: () => {
      setDeleteId(null);
      void client.invalidateQueries({ queryKey: ["app", "agenda"] });
      refreshPlannerCounters();
    },
    onError: (error) =>
      toast.error("Não foi possível remover o evento", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const importIcs = useMutation({
    mutationFn: agendaGateway.importIcs,
    onSuccess: ({ data }) => {
      const summaries = [
        data.imported > 0
          ? `${data.imported} ${data.imported === 1 ? "novo item" : "novos itens"}`
          : null,
        data.updated > 0
          ? `${data.updated} ${data.updated === 1 ? "item atualizado" : "itens atualizados"}`
          : null,
        data.skipped > 0
          ? `${data.skipped} ${data.skipped === 1 ? "item ignorado" : "itens ignorados"}`
          : null,
      ]
        .filter(Boolean)
        .join(", ");
      toast.success("Importação concluída", {
        description: summaries || "Nenhum item foi encontrado no arquivo.",
      });
      void client.invalidateQueries({ queryKey: ["app", "agenda"] });
      void client.invalidateQueries({ queryKey: ["app", "tasks"] });
      refreshPlannerCounters();
    },
    onError: (error) =>
      toast.error("Não foi possível importar a agenda", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const updateTask = useMutation({
    mutationFn: ({ id, status }: { id: string; status: TaskStatus }) =>
      tasksGateway.update(id, { status }),
    onSuccess: (response) => {
      setDetailTask(response.data);
      void client.invalidateQueries({ queryKey: ["app", "tasks"] });
      refreshPlannerCounters();
    },
  });
  const createTaskFromEvent = useMutation({
    mutationFn: (id: string) => agendaGateway.createTask(id, {}),
    onSuccess: ({ data }) => {
      setDetailEvent(null);
      setDetailTask(data);
      void client.invalidateQueries({ queryKey: ["app", "tasks"] });
      refreshPlannerCounters();
    },
    onError: (error) =>
      toast.error("Não foi possível criar a Task", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const linkTaskToEvent = useMutation({
    mutationFn: ({ eventId, taskId }: { eventId: string; taskId: string }) =>
      agendaGateway.linkTask(eventId, taskId),
    onSuccess: () => {
      toast.success("Task vinculada ao Event.");
      void client.invalidateQueries({ queryKey: ["app", "agenda-links"] });
      void client.invalidateQueries({ queryKey: ["app", "task-detail"] });
    },
    onError: (error) =>
      toast.error("Não foi possível vincular a Task", {
        description: error instanceof Error ? error.message : undefined,
      }),
  });
  const openCreate = (date?: Date) => {
    resetForm();
    if (date) {
      const next = new Date(date);
      next.setHours(9, 0, 0, 0);
      setStartAt(
        `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, "0")}-${String(next.getDate()).padStart(2, "0")}T09:00`,
      );
    }
    setFormOpen(true);
  };
  const openEdit = (event: AgendaEvent) => {
    setDetailEvent(null);
    setEditingEvent(event);
    setTitle(event.title);
    setDescription(event.description ?? "");
    setStartAt(toDateTimeLocal(event.startAt));
    setEndAt(event.endAt ? toDateTimeLocal(event.endAt) : "");
    setType(event.type);
    setTimeZoneId(event.timeZoneId ?? "America/Sao_Paulo");
    setLocation(event.location ?? "");
    setAvailabilityStatus(event.availabilityStatus ?? "busy");
    setIsAllDay(event.isAllDay ?? false);
    setTags(event.tags ?? []);
    setReferencesInput(
      event.references
        ?.map(
          (reference) => `${reference.referenceType}:${reference.referenceId}`,
        )
        .join(", ") ?? "",
    );
    setRRule(event.rRule ?? "");
    setExDates("");
    setRDates("");
    setRecurrenceEditScope("series");
    setFormOpen(true);
  };
  const openDuplicate = (event: AgendaEvent) => {
    setDetailEvent(null);
    resetForm();
    setTitle(`${event.title} (cópia)`);
    setDescription(event.description ?? "");
    setStartAt(toDateTimeLocal(event.startAt));
    setEndAt(event.endAt ? toDateTimeLocal(event.endAt) : "");
    setType(event.type);
    setTimeZoneId(event.timeZoneId ?? "America/Sao_Paulo");
    setLocation(event.location ?? "");
    setAvailabilityStatus(event.availabilityStatus ?? "busy");
    setIsAllDay(event.isAllDay ?? false);
    setTags(event.tags ?? []);
    setReferencesInput(
      event.references
        ?.map((reference) => `${reference.referenceType}:${reference.referenceId}`)
        .join(", ") ?? "",
    );
    setRRule(event.rRule ?? "");
    setFormOpen(true);
  };
  const saveEvent = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!title.trim() || !startAt) return;
    const input: AgendaInput = {
      title: title.trim(),
      description: description.trim() || undefined,
      startAt: new Date(startAt).toISOString(),
      endAt: endAt ? new Date(endAt).toISOString() : undefined,
      type,
      timeZoneId,
      location: location.trim() || undefined,
      availabilityStatus,
      isAllDay,
      tags,
      references: parseReferences(referencesInput),
      recurrence: rRule.trim() &&
        (!editingEvent || rRule.trim() !== (editingEvent.rRule ?? ""))
        ? {
            rRule: rRule.trim(),
            exDates: parseIsoList(exDates),
            rDates: parseIsoList(rDates),
          }
        : undefined,
      expectedVersion: editingEvent?.version,
    };
    if (
      editingEvent &&
      recurrenceEditScope === "occurrence" &&
      editingEvent.rRule &&
      editingEvent.occurrenceStartAt
    ) {
      updateOccurrence.mutate({
        id: editingEvent.id,
        originalStartAt: editingEvent.occurrenceStartAt,
        input: {
          title: input.title,
          description: input.description,
          startAt: input.startAt,
          endAt: input.endAt,
        },
      });
    } else if (editingEvent) update.mutate({ id: editingEvent.id, input });
    else create.mutate(input);
  };
  const isLoading = eventsQuery.isPending || tasksQuery.isPending;
  const isError = eventsQuery.isError || tasksQuery.isError;

  return (
    <main className="content-frame space-y-6 animate-fade-in" aria-labelledby="agenda-title">
      <header className="page-heading">
        <div>
          <p className="eyebrow">OPERACIONAL</p>
          <h1
            id="agenda-title"
            className="text-2xl font-bold tracking-tight"
          >
            Agenda
          </h1>
          <p>
            Compromissos, reuniões, WebAulas, entregas e prazos importantes.
          </p>
        </div>
        <div className="flex flex-wrap items-center justify-end gap-2">
          <input
            ref={fileInput}
            type="file"
            accept=".ics,text/calendar"
            className="hidden"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) importIcs.mutate(file);
              event.target.value = "";
            }}
          />
          <div className="flex overflow-hidden rounded-md border">
            <Button
              type="button"
              variant={viewMode === "calendar" ? "default" : "ghost"}
              size="sm"
              className="h-9 gap-1.5 rounded-none px-3 text-xs"
              onClick={() => setViewMode("calendar")}
            >
              <CalendarRange className="h-3.5 w-3.5" />
              Calendário
            </Button>
            <Button
              type="button"
              variant={viewMode === "list" ? "default" : "ghost"}
              size="sm"
              className="h-9 gap-1.5 rounded-none px-3 text-xs"
              onClick={() => setViewMode("list")}
            >
              <List className="h-3.5 w-3.5" />
              Lista
            </Button>
          </div>
          <Button
            type="button"
            onClick={() => openCreate()}
            className="shrink-0"
          >
            <Plus className="mr-1.5 h-4 w-4" />
            Novo evento
          </Button>
        </div>
      </header>
      <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-card p-3">
        <div className="relative min-w-[14rem] flex-1 sm:max-w-sm">
          <Input
            aria-label="Buscar eventos"
            value={eventSearch}
            onChange={(event) => setEventSearch(event.target.value)}
            placeholder="Buscar eventos…"
          />
        </div>
        <Button
          type="button"
          variant={filtersOpen ? "secondary" : "outline"}
          size="sm"
          className="h-9 gap-1.5 text-xs"
          onClick={() => setFiltersOpen((value) => !value)}
        >
          <ListFilter className="h-3.5 w-3.5" />
          Filtros
          {(tagFilter || referenceFilter) && (
            <span className="rounded-full bg-primary px-1.5 py-0.5 text-[10px] text-primary-foreground">{Number(Boolean(tagFilter)) + Number(Boolean(referenceFilter))}</span>
          )}
        </Button>
        {filtersOpen && <>
        <Input
          aria-label="Filtrar por tag"
          value={tagFilter}
          onChange={(event) => setTagFilter(event.target.value)}
          placeholder="Filtrar por tag"
          className="w-full sm:w-40"
        />
        <Input
          aria-label="Filtrar por referência"
          value={referenceFilter}
          onChange={(event) => setReferenceFilter(event.target.value)}
          placeholder="Contexto tipo:id"
          className="w-full sm:w-48"
        />
        </>}
        {(eventSearch || tagFilter || referenceFilter) && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            className="h-9 text-xs"
            onClick={() => {
              setEventSearch("");
              setTagFilter("");
              setReferenceFilter("");
            }}
          >
            Limpar filtros
          </Button>
        )}
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => fileInput.current?.click()}
            disabled={importIcs.isPending}
          >
            <Upload className="mr-1.5 h-4 w-4" />
            {importIcs.isPending ? "Importando…" : "Importar .ics"}
          </Button>
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() =>
              window.open(
                "/api/agenda/export.ics",
                "_blank",
                "noopener,noreferrer",
              )
            }
          >
            <Download className="mr-1.5 h-4 w-4" />
            Exportar Outlook
          </Button>
        </div>
      </div>
      {isLoading && (
        <Card>
          <CardContent className="flex items-center justify-center p-12 text-sm text-muted-foreground">
            Carregando agenda…
          </CardContent>
        </Card>
      )}
      {isError && (
        <Card className="border-destructive/30">
          <CardContent
            className="flex items-start gap-3 p-6 text-sm"
            role="alert"
          >
            <AlertCircle className="h-4 w-4 text-destructive" />
            Não foi possível carregar a agenda.
          </CardContent>
        </Card>
      )}
      {!isLoading && !isError && viewMode === "calendar" && (
        <MonthCalendar
          month={month}
          items={agendaItems}
          onCreateOnDate={openCreate}
          onSetMonth={(nextMonth) =>
            setMonth((current) => new Date(current.getFullYear(), nextMonth, 1))
          }
          onSetYear={(nextYear) =>
            setMonth((current) => new Date(nextYear, current.getMonth(), 1))
          }
          onNavigate={(offset) =>
            setMonth(
              (current) =>
                new Date(current.getFullYear(), current.getMonth() + offset, 1),
            )
          }
          onToday={() => setMonth(new Date())}
          onOpenTask={setDetailTask}
          onOpenEvent={setDetailEvent}
        />
      )}
      {!isLoading &&
        !isError &&
        viewMode === "list" &&
        agendaItems.length === 0 && (
          <div className="flex flex-col items-center justify-center rounded-lg border border-dashed py-16 text-center">
            <CalendarDays className="mb-3 h-10 w-10 text-muted-foreground/40" />
            <p className="text-sm font-medium text-muted-foreground">
              Nenhum compromisso na agenda ainda
            </p>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="mt-4"
              onClick={() => openCreate()}
            >
              <Plus className="mr-1.5 h-4 w-4" />
              Criar primeiro evento
            </Button>
          </div>
        )}
      {!isLoading &&
        !isError &&
        viewMode === "list" &&
        agendaItems.length > 0 && (
          <div className="space-y-6">
            {Object.entries(groupedItems).map(([group, groupItems]) => (
              <div key={group}>
                <h2 className="mb-3 text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                  {formatGroupLabel(group)}
                </h2>
                <div className="overflow-hidden rounded-lg border bg-card">
                  {groupItems.map((item) =>
                    item.kind === "event" ? (
                      <AgendaEventCard
                        key={`event-${item.id}`}
                        event={item.event}
                        onOpen={setDetailEvent}
                        onEdit={openEdit}
                        onDuplicate={openDuplicate}
                      />
                    ) : (
                      <AgendaTaskCard
                        key={`task-${item.id}`}
                        task={item.task}
                        onClick={setDetailTask}
                      />
                    ),
                  )}
                </div>
              </div>
            ))}
          </div>
        )}
      <TaskDetailDrawer
        task={detailTask}
        onClose={() => setDetailTask(null)}
        onStatusChange={(id, status) => updateTask.mutate({ id, status })}
      />
      <AgendaEventDetailDrawer
        event={detailEvent}
        onClose={() => setDetailEvent(null)}
        onEdit={openEdit}
        onDelete={(id) => {
          setDetailEvent(null);
          setDeleteId(id);
        }}
        onCreateTask={(id) => createTaskFromEvent.mutate(id)}
        onLinkTask={(eventId) => {
          const taskId = window.prompt("Informe o ID da Task existente:");
          if (taskId?.trim())
            linkTaskToEvent.mutate({ eventId, taskId: taskId.trim() });
        }}
        onDuplicate={openDuplicate}
      />
      <Dialog open={formOpen} onOpenChange={setFormOpen}>
        <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>
              {editingEvent ? "Editar evento" : "Novo evento"}
            </DialogTitle>
            <DialogDescription>
              {editingEvent
                ? "Atualize os dados deste compromisso."
                : "Preencha os dados para criar um novo evento na agenda."}
            </DialogDescription>
          </DialogHeader>
          <form className="grid gap-4" onSubmit={saveEvent}>
            <label className="grid gap-1.5 text-sm font-medium">
              Título
              <Input
                autoFocus
                value={title}
                onChange={(event) => setTitle(event.target.value)}
                placeholder="Ex.: reunião de alinhamento"
                required
              />
            </label>
            <div className="grid gap-4 sm:grid-cols-2">
              <label className="grid gap-1.5 text-sm font-medium">
                Início
                <Input
                  type="datetime-local"
                  value={startAt}
                  onChange={(event) => setStartAt(event.target.value)}
                  required
                />
              </label>
              <label className="grid gap-1.5 text-sm font-medium">
                Fim (opcional)
                <Input
                  type="datetime-local"
                  value={endAt}
                  onChange={(event) => setEndAt(event.target.value)}
                />
              </label>
            </div>
            <div className="grid gap-1.5 text-sm font-medium">
              <span>Tags</span>
              <TagInput
                values={tags}
                onChange={setTags}
                suggestions={tagSuggestions}
                placeholder="Digite para buscar e adicionar uma tag…"
                helperText="Use escola:, curso: ou aluno: apenas para filtrar sugestões. O prefixo não é salvo."
                ariaLabel="Tags do evento"
              />
            </div>
            {editingEvent?.rRule && editingEvent.occurrenceStartAt && (
              <label className="grid gap-1.5 text-sm font-medium">
                Escopo da edição recorrente
                <select
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                  value={recurrenceEditScope}
                  onChange={(event) =>
                    setRecurrenceEditScope(
                      event.target.value as RecurrenceEditScope,
                    )
                  }
                >
                  <option value="series">Toda a série</option>
                  <option value="occurrence">Somente esta ocorrência</option>
                </select>
                <span className="text-[11px] font-normal text-muted-foreground">
                  Ocorrência original:{" "}
                  {new Date(editingEvent.occurrenceStartAt).toLocaleString(
                    "pt-BR",
                  )}
                  .
                </span>
              </label>
            )}
            <label className="grid gap-1.5 text-sm font-medium">
              Descrição
              <Textarea
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                placeholder="Contexto ou observações do evento"
              />
            </label>
            <DialogFooter>
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  resetForm();
                  setFormOpen(false);
                }}
              >
                Cancelar
              </Button>
              <Button
                type="submit"
                disabled={create.isPending || update.isPending}
              >
                {create.isPending || update.isPending
                  ? "Salvando…"
                  : editingEvent
                    ? "Salvar alterações"
                    : "Salvar evento"}
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      <AlertDialog
        open={Boolean(deleteId)}
        onOpenChange={(open) => {
          if (!open) setDeleteId(null);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remover evento?</AlertDialogTitle>
            <AlertDialogDescription>
              Esta ação não pode ser desfeita. O evento será removido da agenda.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancelar</AlertDialogCancel>
            <AlertDialogAction
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
              onClick={() => deleteId && remove.mutate(deleteId)}
            >
              {remove.isPending ? "Removendo…" : "Remover"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </main>
  );
}
