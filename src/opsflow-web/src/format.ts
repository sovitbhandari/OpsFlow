export function formatWhen(value?: string | null) {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date);
}

export function formatDuration(seconds?: number | null) {
  if (seconds === null || seconds === undefined) return "—";
  const hours = Math.round(seconds / 3600);
  if (hours < 48) return `${hours} h`;
  return `${Math.round(hours / 24)} d`;
}

export function statusLabel(status: string) {
  return status === "InProgress" ? "In progress" : status === "OnHold" ? "On hold" : status;
}
