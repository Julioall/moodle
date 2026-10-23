import { Link } from 'react-router-dom';

type ReportSectionTabsProps = { active: 'history' | 'models' };

export function ReportSectionTabs({ active }: ReportSectionTabsProps) {
  const tabClass = 'inline-flex items-center justify-center whitespace-nowrap rounded-sm px-3 py-1.5 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2';

  return <nav className="inline-flex h-auto w-full justify-start gap-1 overflow-x-auto rounded-md bg-muted p-1 text-muted-foreground" aria-label="Seções de relatórios">
    <Link to="/relatorios" aria-current={active === 'history' ? 'page' : undefined}
      className={`${tabClass} ${active === 'history' ? 'bg-background text-foreground shadow-sm' : 'hover:text-foreground'}`}>
      Histórico
    </Link>
    <Link to="/relatorios/modelos" aria-current={active === 'models' ? 'page' : undefined}
      className={`${tabClass} ${active === 'models' ? 'bg-background text-foreground shadow-sm' : 'hover:text-foreground'}`}>
      Modelos
    </Link>
  </nav>;
}
