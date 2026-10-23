import { cn } from '@/lib/utils';

interface LogoProps {
  className?: string;
}

/** Product wordmark with a variant that stays readable in each theme. */
export function MoodleLogo({ className }: LogoProps) {
  return (
    <>
      <img src="/brand/moodle-logo.png" alt="Moodle Conector" className={cn('dark:hidden', className)} />
      <img src="/brand/moodle-logo-white.png" alt="Moodle Conector" className={cn('hidden dark:block', className)} />
    </>
  );
}
