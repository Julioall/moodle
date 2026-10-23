import { useIsFetching, useIsMutating } from '@tanstack/react-query';

export function GlobalActivityLine() {
  const activeCount = useIsFetching() + useIsMutating();
  const active = activeCount > 0;

  return (
    <div aria-live={active ? 'polite' : 'off'} className="pointer-events-none absolute inset-x-0 bottom-0 z-10 h-px overflow-hidden" role={active ? 'status' : undefined}>
      <div className="absolute inset-0 bg-primary/25" />
      {active && <div className="background-activity-line absolute inset-y-0 left-0 w-28 bg-primary shadow-[0_0_14px_hsl(var(--primary)/0.45)] sm:w-40" />}
    </div>
  );
}
