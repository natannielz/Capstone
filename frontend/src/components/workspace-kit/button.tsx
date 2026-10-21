import type { ComponentProps } from "react";

export type ButtonVariant = "primary" | "secondary" | "tertiary" | "danger" | "danger-confirm";
/** default 44 px, compact 36 px (tables and table toolbars on staff desktop only), icon 44×44 / 36×36, large 48 px (mobile sticky checkout bar). */
export type ButtonSize = "default" | "compact" | "icon" | "icon-compact" | "large";

const sizeClass: Record<ButtonSize, string> = {
  default: "", compact: " ui-button--compact", icon: " ui-button--icon", "icon-compact": " ui-button--icon ui-button--compact", large: " ui-button--large",
};

/** The class string for anything that should look like a button (a Next <Link>, an <a>, a <summary>). */
export function buttonClassName({ variant = "secondary", size = "default", className }: { variant?: ButtonVariant; size?: ButtonSize; className?: string } = {}): string {
  return `ui-button ui-button--${variant}${sizeClass[size]}${className ? " " + className : ""}`;
}

/**
 * The one button (ui-standard §8). At most one `primary` per page region; icon-only sizes need an `aria-label`.
 * `busy` keeps the label, shows a spinner, sets aria-busy and blocks a second press.
 */
export function Button({ variant = "secondary", size = "default", busy = false, className, type = "button", disabled, children, ...props }:
  ComponentProps<"button"> & { variant?: ButtonVariant; size?: ButtonSize; busy?: boolean }) {
  return <button type={type} className={buttonClassName({ variant, size, className })} {...props} aria-busy={busy || undefined} disabled={disabled || busy}>
    {busy && <span className="ui-spinner" aria-hidden="true" />}{children}
  </button>;
}
