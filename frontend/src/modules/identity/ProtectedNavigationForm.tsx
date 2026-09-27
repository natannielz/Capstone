"use client";

import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { createIdentityGateway } from "./identity-client";
import { identityMessage } from "./identity-workflow";
import styles from "./identity.module.css";

/** Full-document POST is deliberate: the provider's authorization redirect must not be followed by fetch. */
export function ProtectedNavigationForm({ action, fields, children, label, disabled = false, secondary = false }: {
  action: "/auth/login" | "/auth/step-up" | "/auth/logout";
  fields: Record<string, string>;
  children?: ReactNode;
  label: string;
  disabled?: boolean;
  secondary?: boolean;
}) {
  const [gateway] = useState(() => createIdentityGateway());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const pending = useRef(false);
  const active = useRef(true);
  const controller = useRef<AbortController | null>(null);
  const submittedForm = useRef<HTMLFormElement | null>(null);
  useEffect(() => {
    active.current = true;
    const restore = (event: PageTransitionEvent) => {
      if (!event.persisted) return;
      submittedForm.current?.remove(); submittedForm.current = null;
      pending.current = false; setBusy(false); setError(null);
    };
    window.addEventListener("pageshow", restore);
    return () => { active.current = false; controller.current?.abort(); submittedForm.current?.remove(); window.removeEventListener("pageshow", restore); };
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (disabled || pending.current) return;
    pending.current = true; setBusy(true); setError(null);
    const snapshot = { ...fields };
    controller.current = new AbortController();
    try {
      const token = await gateway.csrf(controller.current.signal);
      if (!active.current || controller.current.signal.aborted) return;
      // Freeze the submitted actor and fields while the token request was pending.
      const form = document.createElement("form");
      form.method = "POST"; form.action = action; form.hidden = true;
      for (const [name, value] of Object.entries({ ...snapshot, __RequestVerificationToken: token })) {
        const input = document.createElement("input"); input.type = "hidden"; input.name = name; input.value = value; form.appendChild(input);
      }
      submittedForm.current = form;
      document.body.appendChild(form);
      HTMLFormElement.prototype.submit.call(form);
    } catch (failure) {
      if (!active.current) return;
      pending.current = false; setBusy(false); setError(failure);
    }
  }

  return <form method="post" action={action} onSubmit={event => void submit(event)} className={styles.navigationForm}>
    {children}
    {error !== null && <p className={styles.alert} role="alert">{identityMessage(error)}</p>}
    <button type="submit" className={secondary ? styles.secondary : styles.primary} disabled={disabled || busy}>
      {busy ? "Menyiapkan sesi…" : label}
    </button>
    <span className={styles.srOnly} role="status">{busy ? "Menyiapkan perlindungan sesi. Anda akan diarahkan setelah siap." : ""}</span>
  </form>;
}
