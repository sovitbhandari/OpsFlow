import { useEffect, useId, useRef, type ReactNode } from "react";
import { ApiError } from "./api";

export function Banner({ error }: { error: unknown }) {
  if (!error) return null;
  const apiError = error instanceof ApiError ? error : null;
  const message = apiError?.status === 412
    ? "Someone else updated this work order. Refresh before trying again so you do not overwrite their change."
    : apiError?.status === 409
      ? apiError.message
      : apiError?.status === 403
        ? "You do not have permission to do that in this organization."
        : error instanceof Error
          ? error.message
          : "Something went wrong.";
  return (
    <div className="banner" role="alert">
      <strong>{apiError?.status === 409 ? "Conflict" : apiError?.status === 412 ? "Stale update" : "Request failed"}</strong>
      <span>{message}</span>
      {apiError?.errors && (
        <ul>
          {Object.entries(apiError.errors).flatMap(([field, messages]) => messages.map((item) => <li key={`${field}-${item}`}>{field}: {item}</li>))}
        </ul>
      )}
    </div>
  );
}

export function Empty({ title, body }: { title: string; body: string }) {
  return (
    <div className="empty">
      <h2>{title}</h2>
      <p>{body}</p>
    </div>
  );
}

export function Modal({ title, children, onClose }: { title: string; children: ReactNode; onClose: () => void }) {
  const titleId = useId();
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    dialog.showModal();
    const onCancel = (event: Event) => {
      event.preventDefault();
      onClose();
    };
    dialog.addEventListener("cancel", onCancel);
    return () => dialog.removeEventListener("cancel", onCancel);
  }, [onClose]);
  return (
    <dialog ref={ref} className="modal" aria-labelledby={titleId}>
      <h2 id={titleId}>{title}</h2>
      {children}
    </dialog>
  );
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}
