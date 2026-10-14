export type Role = "Admin" | "Dispatcher" | "Technician" | "Requester";
export type Priority = "Low" | "Medium" | "High" | "Critical";
export type Status = "Open" | "Assigned" | "InProgress" | "OnHold" | "Resolved" | "Closed" | "Cancelled";

export interface Organization {
  id: string;
  name: string;
  slug: string;
  role: Role;
}

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  organizations: Organization[];
}

export interface WorkOrderSummary {
  id: string;
  number: number;
  displayNumber: string;
  title: string;
  category: string;
  priority: Priority;
  status: Status;
  requesterUserId: string;
  requesterName: string;
  assignedTechnicianUserId: string | null;
  assigneeName: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  dueAtUtc: string;
  isOverdue: boolean;
  slaPaused: boolean;
  rowVersion: string;
}

export interface WorkOrderDetail extends WorkOrderSummary {
  description: string;
  resolvedAtUtc: string | null;
  closedAtUtc: string | null;
  slaDurationSeconds: number;
  holdAccumulatedSeconds: number;
  holdResumeStatus: Status | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface Comment {
  id: string;
  authorUserId: string;
  authorName: string;
  body: string;
  createdAtUtc: string;
}

export interface Activity {
  id: string;
  activityType: string;
  message: string;
  actorUserId: string | null;
  actorName: string | null;
  createdAtUtc: string;
}

export interface Attachment {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedByUserId: string;
  uploadedByName: string;
  createdAtUtc: string;
}

export interface Member {
  userId: string;
  email: string;
  displayName: string;
  role: Role;
  createdAtUtc: string;
}

export interface SlaPolicy {
  priority: Priority;
  durationSeconds: number;
  updatedAtUtc: string;
}

export interface AuditItem {
  id: string;
  actorUserId: string | null;
  actorName: string | null;
  action: string;
  entityType: string;
  entityId: string;
  summary: string;
  createdAtUtc: string;
}

export interface NotificationJob {
  id: string;
  type: string;
  status: "Pending" | "Processing" | "Succeeded" | "Failed";
  attemptCount: number;
  nextAttemptAtUtc: string;
  leaseUntilUtc: string | null;
  lastError: string | null;
  createdAtUtc: string;
}

export interface ReportSummary {
  cohortCount: number;
  overdueCount: number;
  resolvedCount: number;
  averageResolutionSeconds: number | null;
  minResolutionSeconds: number | null;
  maxResolutionSeconds: number | null;
  statusCounts: { label: string; count: number }[];
  priorityCounts: { label: string; count: number }[];
}

export class ApiError extends Error {
  status: number;
  errors?: Record<string, string[]>;

  constructor(status: number, message: string, errors?: Record<string, string[]>) {
    super(message);
    this.status = status;
    this.errors = errors;
  }
}

let csrfToken: string | null = null;

export function clearSessionToken() {
  csrfToken = null;
}

export async function login(email: string, password: string) {
  clearSessionToken();
  const user = await api<CurrentUser>("/api/auth/login", { method: "POST", json: { email, password } });
  clearSessionToken();
  await readCsrf();
  return user;
}

export async function logout() {
  await api("/api/auth/logout", { method: "POST" });
  clearSessionToken();
  sessionStorage.removeItem("opsflow.organizationId");
}

async function readCsrf() {
  const response = await fetch("/api/auth/csrf", { credentials: "include" });
  if (!response.ok) {
    throw new ApiError(response.status, "Could not start a secure session.");
  }
  const body = (await response.json()) as { token: string };
  csrfToken = body.token;
  return csrfToken;
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const headers = new Headers(init.headers);
  const method = init.method ?? "GET";
  if (init.json !== undefined) {
    headers.set("Content-Type", "application/json");
  }
  if (method !== "GET" && method !== "HEAD") {
    headers.set("X-XSRF-TOKEN", csrfToken ?? (await readCsrf()));
  }
  const response = await fetch(path, {
    ...init,
    headers,
    credentials: "include",
    body: init.json !== undefined ? JSON.stringify(init.json) : init.body,
  });
  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  const payload = text ? (JSON.parse(text) as Record<string, unknown>) : {};
  if (!response.ok) {
    const errors = payload.errors as Record<string, string[]> | undefined;
    const detail = typeof payload.detail === "string" ? payload.detail : response.statusText;
    throw new ApiError(response.status, detail || "Request failed.", errors);
  }
  return payload as T;
}

export async function downloadAttachment(path: string, fileName: string) {
  const response = await fetch(path, { credentials: "include" });
  if (!response.ok) {
    throw new ApiError(response.status, "The attachment could not be downloaded.");
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
