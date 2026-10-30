import { useEffect, useRef, useState, type ReactNode } from "react";

/**
 * A horizontally scrolling box that fades its right edge while there is more to see, so a
 * table wider than its panel does not look cut off.
 */
export function ScrollFade({ children, className = "" }: { children: ReactNode; className?: string }) {
  const box = useRef<HTMLDivElement>(null);
  const [more, setMore] = useState(false);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const check = () => setMore(el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
    check();
    el.addEventListener("scroll", check, { passive: true });
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(check);
    observer?.observe(el);
    if (el.firstElementChild) observer?.observe(el.firstElementChild);
    return () => {
      el.removeEventListener("scroll", check);
      observer?.disconnect();
    };
  }, []);
  return (
    <div ref={box} className={`table-scroll ${more ? "has-more" : ""} ${className}`}>
      {children}
    </div>
  );
}
