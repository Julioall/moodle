import type { ComponentPropsWithoutRef } from 'react';
import { cn } from '@/lib/utils';

type MoodleIconProps = ComponentPropsWithoutRef<'svg'>;

export function MoodleIcon({ className, ...props }: MoodleIconProps) {
  return (
    <svg
      viewBox="0 0 192 192"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={cn('h-4 w-4 text-[#F98012]', className)}
      aria-hidden="true"
      focusable="false"
      {...props}
    >
      <path
        d="M5.999 60.165V93.79M101.97 80l.005 54.095m-51.128-56.35c-.265 18.528 0 37.687 0 56.35m103.151 0V79.999c-.107-28.355-51.575-28.715-52.024 0 .133-7.346-3.623-14.21-9.867-18.032m5.382-7.212-8.97-8.565 24.667-20.286A222.577 222.577 0 0 0 6 60.164h32.29v15.327c21.841 5.004 42.561 3.44 59.2-20.736z"
        transform="translate(16.001 16)"
        stroke="currentColor"
        strokeWidth="12"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}