import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';

import { Button } from '../../components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '../../components/ui/dialog';
import { Textarea } from '../../components/ui/textarea';
import { followupGateway } from '../followup/followup-gateway';
import type { Student } from '../students/students-gateway';

type CourseFollowupDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  connectionRef: string;
  courseId: string;
  students: Student[];
};

const reasons = [
  ['falta_acesso', 'Falta de acesso'],
  ['atividade_pendente', 'Atividade pendente'],
  ['desempenho', 'Desempenho'],
  ['participacao', 'Participação'],
  ['duvida', 'Dúvida'],
  ['outro', 'Outro'],
] as const;

const actions = [
  ['mensagem', 'Mensagem'],
  ['ligacao', 'Ligação'],
  ['orientacao', 'Orientação'],
  ['conversa_presencial', 'Conversa presencial'],
  ['verificacao', 'Verificação'],
  ['outro', 'Outro'],
] as const;

const statuses = [
  ['em_acompanhamento', 'Em acompanhamento'],
  ['aguardando_aluno', 'Aguardando aluno'],
  ['resolvido', 'Resolvido'],
] as const;

export function CourseFollowupDialog({ open, onOpenChange, connectionRef, courseId, students }: CourseFollowupDialogProps) {
  const queryClient = useQueryClient();
  const [studentId, setStudentId] = useState('');
  const [reason, setReason] = useState('atividade_pendente');
  const [action, setAction] = useState('mensagem');
  const [status, setStatus] = useState('em_acompanhamento');
  const [notes, setNotes] = useState('');
  const create = useMutation({
    mutationFn: () => {
      const student = students.find((item) => item.studentId === studentId);
      if (!student) throw new Error('Selecione um aluno.');
      return followupGateway.create({
        studentRef: `${student.connectionRef}:${student.studentId}`,
        studentName: student.name,
        courseRef: `${connectionRef}:${courseId}`,
        kind: 'acompanhamento',
        reason,
        action,
        status,
        notes: notes.trim(),
      });
    },
    onSuccess: () => {
      setStudentId('');
      setNotes('');
      void queryClient.invalidateQueries({ queryKey: ['app', 'followups'] });
      onOpenChange(false);
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Registrar acompanhamento</DialogTitle>
          <DialogDescription>Registre uma ação contextual para um aluno deste curso.</DialogDescription>
        </DialogHeader>
        <form className="grid gap-4" onSubmit={(event) => { event.preventDefault(); if (studentId && notes.trim()) create.mutate(); }}>
          <label className="grid gap-1.5 text-sm font-medium">Aluno
            <select className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" value={studentId} onChange={(event) => setStudentId(event.target.value)} required>
              <option value="">Selecione um aluno</option>
              {students.map((student) => <option key={student.studentId} value={student.studentId}>{student.name}</option>)}
            </select>
          </label>
          <div className="grid gap-4 sm:grid-cols-3">
            <label className="grid gap-1.5 text-sm font-medium">Motivo
              <select className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" value={reason} onChange={(event) => setReason(event.target.value)}>{reasons.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
            </label>
            <label className="grid gap-1.5 text-sm font-medium">Ação
              <select className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" value={action} onChange={(event) => setAction(event.target.value)}>{actions.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
            </label>
            <label className="grid gap-1.5 text-sm font-medium">Status
              <select className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" value={status} onChange={(event) => setStatus(event.target.value)}>{statuses.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>
            </label>
          </div>
          <label className="grid gap-1.5 text-sm font-medium">Observação
            <Textarea className="min-h-28" value={notes} onChange={(event) => setNotes(event.target.value)} placeholder="Descreva o contato, a orientação ou o próximo passo." required />
          </label>
          {students.length === 0 && <p className="text-sm text-muted-foreground">Carregue a aba Alunos para selecionar um aluno.</p>}
          {create.isError && <p className="text-sm text-destructive" role="alert">Não foi possível registrar o acompanhamento.</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>Cancelar</Button>
            <Button type="submit" disabled={create.isPending || !studentId || !notes.trim()}>{create.isPending ? 'Registrando…' : 'Registrar acompanhamento'}</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
