import type { ReactNode } from 'react';
import { ReportSectionTabs } from './ReportSectionTabs';

type ReportSectionHeaderProps = {
  active: 'history' | 'models';
  action?: ReactNode;
  showTabs?: boolean;
};

export function ReportSectionHeader({ active, action, showTabs = true }: ReportSectionHeaderProps) {
  return <>
    <header className="page-heading">
      <div>
        <p className="eyebrow">GESTÃO</p>
        <h1 id="reports-page-title">Relatórios</h1>
        <p>Acompanhe a criação e baixe os arquivos gerados.</p>
      </div>
      {action && <div className="flex flex-wrap gap-2">{action}</div>}
    </header>
    {showTabs && <ReportSectionTabs active={active} />}
  </>;
}
