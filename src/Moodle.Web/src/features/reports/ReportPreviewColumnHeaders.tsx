import { useEffect, useRef, useState } from 'react';
import { GripVertical, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

export type ReportPreviewColumn = { fieldId: string; label: string };
export type ReportPreviewColumnActions = {
  disabled?: boolean;
  onAnnounce: (message: string) => void;
  onRemove: (fieldId: string) => void;
  onRename: (fieldId: string, label: string) => void;
  onReorder: (draggedFieldId: string, targetFieldId: string, position: 'before' | 'after') => void;
};

type DropTarget = { fieldId: string; position: 'before' | 'after' };
type PointerDrag = {
  pointerId: number;
  fieldId: string;
  startX: number;
  startY: number;
  dragging: boolean;
  target?: DropTarget;
};

export function ReportPreviewColumnHeaders({ columns, actions }: { columns: ReportPreviewColumn[]; actions?: ReportPreviewColumnActions }) {
  const pointerDrag = useRef<PointerDrag>();
  const feedbackTimer = useRef<number>();
  const [draggingFieldId, setDraggingFieldId] = useState<string>();
  const [dropTarget, setDropTarget] = useState<DropTarget>();
  const [recentlyMovedFieldId, setRecentlyMovedFieldId] = useState<string>();

  useEffect(() => () => window.clearTimeout(feedbackTimer.current), []);

  const finishDrag = (commit: boolean) => {
    const drag = pointerDrag.current;
    if (!drag) return;
    if (commit && drag.dragging && drag.target && drag.target.fieldId !== drag.fieldId && actions) {
      const fromIndex = columns.findIndex(column => column.fieldId === drag.fieldId);
      const targetIndex = columns.findIndex(column => column.fieldId === drag.target!.fieldId);
      const destinationIndex = targetIndex + (drag.target.position === 'after' ? 1 : 0) - (fromIndex < targetIndex + (drag.target.position === 'after' ? 1 : 0) ? 1 : 0);
      actions.onReorder(drag.fieldId, drag.target.fieldId, drag.target.position);
      actions.onAnnounce(`${columns[fromIndex]?.label ?? 'Coluna'} movida para a posição ${destinationIndex + 1} de ${columns.length}.`);
      setRecentlyMovedFieldId(drag.fieldId);
      window.clearTimeout(feedbackTimer.current);
      feedbackTimer.current = window.setTimeout(() => setRecentlyMovedFieldId(undefined), 700);
    } else if (drag.dragging) {
      actions?.onAnnounce('Movimentação cancelada.');
    }
    pointerDrag.current = undefined;
    setDraggingFieldId(undefined);
    setDropTarget(undefined);
  };

  const startPointerDrag = (event: React.PointerEvent<HTMLButtonElement>, fieldId: string) => {
    if (!actions || actions.disabled || event.button !== 0) return;
    pointerDrag.current = { pointerId: event.pointerId, fieldId, startX: event.clientX, startY: event.clientY, dragging: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const updatePointerDrag = (event: React.PointerEvent<HTMLButtonElement>) => {
    const drag = pointerDrag.current;
    if (!drag || drag.pointerId !== event.pointerId || actions?.disabled) return;
    if (!drag.dragging && Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < 5) return;
    if (!drag.dragging) {
      drag.dragging = true;
      setDraggingFieldId(drag.fieldId);
      actions?.onAnnounce(`Movendo ${columns.find(column => column.fieldId === drag.fieldId)?.label ?? 'coluna'}. Arraste até a posição desejada.`);
    }

    const element = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-report-column-id]');
    const targetFieldId = element?.dataset.reportColumnId;
    if (!element || !targetFieldId || targetFieldId === drag.fieldId) {
      drag.target = undefined;
      setDropTarget(undefined);
      return;
    }
    const position = event.clientX < element.getBoundingClientRect().left + element.getBoundingClientRect().width / 2 ? 'before' : 'after';
    if (drag.target?.fieldId !== targetFieldId || drag.target.position !== position) {
      drag.target = { fieldId: targetFieldId, position };
      setDropTarget(drag.target);
      const targetLabel = columns.find(column => column.fieldId === targetFieldId)?.label ?? 'coluna';
      actions?.onAnnounce(`Soltar ${position === 'before' ? 'antes' : 'depois'} de ${targetLabel}.`);
    }
  };

  const moveWithKeyboard = (fieldId: string, index: number, direction: -1 | 1) => {
    if (!actions || actions.disabled) return;
    const neighbor = columns[index + direction];
    if (!neighbor) return;
    actions.onReorder(fieldId, neighbor.fieldId, direction < 0 ? 'before' : 'after');
    actions.onAnnounce(`${columns[index]?.label ?? 'Coluna'} movida para a posição ${index + direction + 1} de ${columns.length}.`);
    setRecentlyMovedFieldId(fieldId);
    window.clearTimeout(feedbackTimer.current);
    feedbackTimer.current = window.setTimeout(() => setRecentlyMovedFieldId(undefined), 700);
  };

  return <tr>{columns.map((column, index) => {
    const isDragging = draggingFieldId === column.fieldId;
    const isDropTarget = dropTarget?.fieldId === column.fieldId;
    const isRecentlyMoved = recentlyMovedFieldId === column.fieldId;
    const dropMarker = isDropTarget
      ? dropTarget.position === 'before'
        ? 'before:absolute before:inset-y-0 before:left-0 before:w-1 before:rounded-full before:bg-primary before:content-[""]'
        : 'after:absolute after:inset-y-0 after:right-0 after:w-1 after:rounded-full after:bg-primary after:content-[""]'
      : '';
    return <th key={column.fieldId} scope="col" data-report-column-id={actions ? column.fieldId : undefined}
      className={`relative ${actions ? 'min-w-[176px]' : 'min-w-[140px]'} whitespace-nowrap border-b border-r border-border/70 bg-muted/50 px-2 py-2 align-middle transition-[background-color,box-shadow,opacity,transform] duration-200 ${dropMarker} ${isDragging ? 'z-10 scale-[0.98] bg-primary/15 opacity-50 shadow-lg ring-2 ring-primary' : ''} ${isDropTarget ? 'z-10 bg-primary/10 shadow-inner ring-2 ring-primary/60' : ''} ${isRecentlyMoved ? 'bg-primary/10 ring-2 ring-primary/40' : ''}`}>
      {actions ? <div className="flex min-w-0 items-center gap-1">
        <button type="button" className={`inline-flex size-7 shrink-0 touch-none select-none items-center justify-center rounded text-muted-foreground transition-[background-color,color,transform] duration-150 hover:bg-primary/10 hover:text-primary ${isDragging ? 'scale-110 cursor-grabbing bg-primary/15 text-primary' : 'cursor-grab active:scale-90'}`}
          aria-label={`Reordenar ${column.label}. Arraste para mover ou use as setas esquerda e direita.`}
          aria-keyshortcuts="ArrowLeft ArrowRight" disabled={actions.disabled}
          onPointerDown={event => startPointerDrag(event, column.fieldId)}
          onPointerMove={updatePointerDrag}
          onPointerUp={() => finishDrag(true)}
          onPointerCancel={() => finishDrag(false)}
          onKeyDown={event => {
            if (event.key === 'ArrowLeft') { event.preventDefault(); moveWithKeyboard(column.fieldId, index, -1); }
            if (event.key === 'ArrowRight') { event.preventDefault(); moveWithKeyboard(column.fieldId, index, 1); }
          }}><GripVertical className="size-4" aria-hidden="true" /></button>
        <Input aria-label={`Título de ${column.label}`} className="h-8 min-w-0 w-full flex-1 border-0 bg-transparent px-1 text-xs font-medium shadow-none focus-visible:ring-1" value={column.label} maxLength={80} disabled={actions.disabled} onChange={event => actions.onRename(column.fieldId, event.target.value)} />
        <Button type="button" variant="ghost" size="icon" className="size-7 shrink-0 transition-colors hover:bg-destructive/10 hover:text-destructive" aria-label={`Remover ${column.label}`} disabled={actions.disabled} onClick={() => actions.onRemove(column.fieldId)}><X className="size-4" /></Button>
      </div> : column.label}
      {actions && <span className="sr-only">Coluna {index + 1} de {columns.length}</span>}
    </th>;
  })}</tr>;
}
