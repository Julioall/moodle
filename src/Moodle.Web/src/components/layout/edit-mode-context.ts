import { createContext, useContext, type Dispatch, type SetStateAction } from 'react';

export type EditModeContextValue = {
  editMode: boolean;
  setEditMode: Dispatch<SetStateAction<boolean>>;
};

export const EditModeContext = createContext<EditModeContextValue | null>(null);

export function useEditMode() {
  const context = useContext(EditModeContext);
  if (!context) throw new Error('useEditMode must be used inside EditModeProvider');
  return context;
}
