import * as React from "react";
import * as ScrollAreaPrimitive from "@radix-ui/react-scroll-area";

import { cn } from "@/lib/utils";

type ScrollAreaProps = React.ComponentPropsWithoutRef<typeof ScrollAreaPrimitive.Root> & {
  viewportClassName?: string;
  preventContentOverflow?: boolean;
};

const ScrollArea = React.forwardRef<
  React.ElementRef<typeof ScrollAreaPrimitive.Root>,
  ScrollAreaProps
>(({ className, children, viewportClassName, preventContentOverflow = false, ...props }, ref) => (
  <ScrollAreaPrimitive.Root ref={ref} className={cn("group/scrollarea relative overflow-hidden", className)} {...props}>
    <ScrollAreaPrimitive.Viewport
      className={cn(
        "h-full w-full rounded-[inherit]",
        preventContentOverflow && "[&>div]:!block [&>div]:!min-w-0 [&>div]:!w-full",
        viewportClassName,
      )}
    >
      {children}
    </ScrollAreaPrimitive.Viewport>
    <ScrollBar />
    <ScrollAreaPrimitive.Corner />
  </ScrollAreaPrimitive.Root>
));
ScrollArea.displayName = ScrollAreaPrimitive.Root.displayName;

const ScrollBar = React.forwardRef<
  React.ElementRef<typeof ScrollAreaPrimitive.ScrollAreaScrollbar>,
  React.ComponentPropsWithoutRef<typeof ScrollAreaPrimitive.ScrollAreaScrollbar>
>(({ className, orientation = "vertical", ...props }, ref) => (
  <ScrollAreaPrimitive.ScrollAreaScrollbar
    ref={ref}
    orientation={orientation}
    className={cn(
      "flex touch-none select-none opacity-0 transition-[opacity,color] duration-150 ease-out group-hover/scrollarea:opacity-100",
      orientation === "vertical" && "h-full w-2 p-[3px]",
      orientation === "horizontal" && "h-2 flex-col p-[3px]",
      className,
    )}
    {...props}
  >
    <ScrollAreaPrimitive.ScrollAreaThumb className="relative flex-1 rounded-full bg-black/20 dark:bg-white/20" />
  </ScrollAreaPrimitive.ScrollAreaScrollbar>
));
ScrollBar.displayName = ScrollAreaPrimitive.ScrollAreaScrollbar.displayName;

export { ScrollArea, ScrollBar };