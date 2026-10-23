import { useMemo, useState, type ReactNode } from 'react';
import { EditModeContext } from './edit-mode-context';

export function EditModeProvider({ children }: { children: ReactNode }) {
  const [editMode, setEditMode] = useState(false);
  const value = useMemo(() => ({ editMode, setEditMode }), [editMode]);

  return <EditModeContext.Provider value={value}>{children}</EditModeContext.Provider>;
}
