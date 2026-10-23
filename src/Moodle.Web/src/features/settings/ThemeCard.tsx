import { useTheme } from 'next-themes';
import { Monitor, Moon, Sun } from 'lucide-react';

import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { cn } from '../../lib/utils';

const modes = [
  { value: 'light', label: 'Claro', icon: Sun },
  { value: 'dark', label: 'Escuro', icon: Moon },
  { value: 'system', label: 'Sistema', icon: Monitor },
] as const;

export function ThemeCard() {
  const { theme, setTheme } = useTheme();

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-lg"><Sun className="h-5 w-5" />Aparência</CardTitle>
        <CardDescription>Escolha o modo de exibição da interface Moodle Conector.</CardDescription>
      </CardHeader>
      <CardContent className="grid grid-cols-3 gap-3">
        {modes.map(({ value, label, icon: Icon }) => (
          <button
            key={value}
            type="button"
            onClick={() => setTheme(value)}
            className={cn(
              'flex flex-col items-center gap-2 rounded-lg border p-4 text-sm font-medium transition-colors hover:bg-accent',
              theme === value ? 'border-primary bg-primary/5 text-primary' : 'border-border text-muted-foreground',
            )}
            aria-pressed={theme === value}
          >
            <Icon className="h-5 w-5" />
            {label}
          </button>
        ))}
      </CardContent>
    </Card>
  );
}
