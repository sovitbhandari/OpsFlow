import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRef, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router";
import { ApiError, api, downloadAttachment, type Activity, type Attachment, type Comment, type Member, type Paged, type Priority, type Status, type WorkOrderDetail, type WorkOrderSummary } from "../api";
import { useAuth } from "../useAuth";
import { Banner, Empty, Field, Modal } from "../components";
import { formatWhen, statusLabel } from "../format";

const statuses: Array<Status | ""> = ["", "Open", "Assigned", "InProgress", "OnHold", "Resolved", "Closed", "Cancelled"];
const priorities: Array<Priority | ""> = ["", "Low", "Medium", "High", "Critical"];

export function WorkOrderListPage() {
  const { organization } = useAuth();
  const [params, setParams] = useSearchParams();
  const orgId = organization?.id;
  const query = useQuery({
    queryKey: ["work-orders", orgId, params.toString()],
    enabled: Boolean(orgId),
    queryFn: () => api<Paged<WorkOrderSummary>>(`/api/organizations/${orgId}/work-orders?${params.toString() || "page=1&pageSize=25"}`),
  });

  function update(name: string, value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set(name, value);
    else next.delete(name);
    if (name !== "page") next.set("page", "1");
    setParams(next);
  }

  return (
    <section>
      <header className="page-head">
        <h1>Work orders</h1>
        <Link className="button" to="/work-orders/new">New work order</Link>
      </header>
      <form className="filters" onSubmit={(event) => event.preventDefault()}>
        <Field label="Search"><input value={params.get("search") ?? ""} onChange={(event) => update("search", event.target.value)} placeholder="Number, title, or description" /></Field>
        <Field label="Status"><select value={params.get("status") ?? ""} onChange={(event) => update("status", event.target.value)}>{statuses.map((item) => <option key={item} value={item}>{item ? statusLabel(item) : "Any"}</option>)}</select></Field>
        <Field label="Priority"><select value={params.get("priority") ?? ""} onChange={(event) => update("priority", event.target.value)}>{priorities.map((item) => <option key={item} value={item}>{item || "Any"}</option>)}</select></Field>
        <Field label="Sort">
          <select value={params.get("sort") ?? "createdAt"} onChange={(event) => update("sort", event.target.value)}>
            <option value="createdAt">Created</option>
            <option value="updatedAt">Updated</option>
            <option value="dueAt">Due</option>
            <option value="priority">Priority</option>
            <option value="status">Status</option>
            <option value="number">Number</option>
          </select>
        </Field>
        <Field label="Direction">
          <select value={params.get("direction") ?? "desc"} onChange={(event) => update("direction", event.target.value)}>
            <option value="desc">Descending</option>
            <option value="asc">Ascending</option>
          </select>
        </Field>
      </form>
      {query.isLoading && <p>Loading work orders…</p>}
      {query.isError && <Banner error={query.error} />}
      {query.data && query.data.items.length === 0 && <Empty title="No matching work orders" body="Adjust the filters or create a new request." />}
      {query.data && query.data.items.length > 0 && (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr><th>Number</th><th>Title</th><th>Priority</th><th>Status</th><th>Assignee</th><th>Due</th></tr>
              </thead>
              <tbody>
                {query.data.items.map((item) => (
                  <tr key={item.id}>
                    <td><Link to={`/work-orders/${item.id}`}>{item.displayNumber}</Link></td>
                    <td>{item.title}</td>
                    <td><span className={`pill ${item.priority.toLowerCase()}`}>{item.priority}</span></td>
                    <td>{statusLabel(item.status)}{item.slaPaused ? " · SLA paused" : ""}{item.isOverdue ? " · Overdue" : ""}</td>
                    <td>{item.assigneeName ?? "Unassigned"}</td>
                    <td>{formatWhen(item.dueAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="pager">
            <button type="button" disabled={query.data.page <= 1} onClick={() => update("page", String(query.data.page - 1))}>Previous</button>
            <span>Page {query.data.page} of {Math.max(1, Math.ceil(query.data.totalCount / query.data.pageSize))} · {query.data.totalCount} total</span>
            <button type="button" disabled={query.data.page * query.data.pageSize >= query.data.totalCount} onClick={() => update("page", String(query.data.page + 1))}>Next</button>
          </div>
        </>
      )}
    </section>
  );
}

export function NewWorkOrderPage() {
  const { organization } = useAuth();
  const navigate = useNavigate();
  const key = useRef(crypto.randomUUID());
  const [error, setError] = useState<unknown>(null);
  const mutation = useMutation({
    mutationFn: (input: { title: string; description: string; category: string; priority: Priority }) =>
      api<WorkOrderDetail>(`/api/organizations/${organization?.id}/work-orders`, {
        method: "POST",
        json: input,
        headers: { "Idempotency-Key": key.current },
      }),
    onSuccess: (created) => navigate(`/work-orders/${created.id}`),
    onError: setError,
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    mutation.mutate({
      title: String(form.get("title") ?? ""),
      description: String(form.get("description") ?? ""),
      category: String(form.get("category") ?? ""),
      priority: String(form.get("priority") ?? "Medium") as Priority,
    });
  }

  return (
    <section className="narrow">
      <h1>New work order</h1>
      <Banner error={error} />
      <form className="stack" onSubmit={submit}>
        <Field label="Title"><input name="title" required maxLength={200} /></Field>
        <Field label="Category"><input name="category" required maxLength={100} placeholder="HVAC, Electrical, Access" /></Field>
        <Field label="Priority">
          <select name="priority" defaultValue="Medium">{priorities.filter(Boolean).map((item) => <option key={item}>{item}</option>)}</select>
        </Field>
        <Field label="Description"><textarea name="description" maxLength={4000} rows={5} /></Field>
        <button className="button" type="submit" disabled={mutation.isPending}>{mutation.isPending ? "Creating…" : "Create work order"}</button>
      </form>
    </section>
  );
}

export function WorkOrderDetailPage() {
  const { organization, user } = useAuth();
  const { workOrderId } = useParams();
  const queryClient = useQueryClient();
  const orgId = organization?.id;
  const claimKey = useRef(crypto.randomUUID());
  const [error, setError] = useState<unknown>(null);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const detail = useQuery({
    queryKey: ["work-order", orgId, workOrderId],
    enabled: Boolean(orgId && workOrderId),
    queryFn: () => api<WorkOrderDetail>(`/api/organizations/${orgId}/work-orders/${workOrderId}`),
  });
  const comments = useQuery({
    queryKey: ["comments", orgId, workOrderId],
    enabled: Boolean(orgId && workOrderId && detail.isSuccess),
    queryFn: () => api<Comment[]>(`/api/organizations/${orgId}/work-orders/${workOrderId}/comments`),
  });
  const activity = useQuery({
    queryKey: ["activity", orgId, workOrderId],
    enabled: Boolean(orgId && workOrderId && detail.isSuccess),
    queryFn: () => api<Activity[]>(`/api/organizations/${orgId}/work-orders/${workOrderId}/activity`),
  });
  const attachments = useQuery({
    queryKey: ["attachments", orgId, workOrderId],
    enabled: Boolean(orgId && workOrderId && detail.isSuccess),
    queryFn: () => api<Attachment[]>(`/api/organizations/${orgId}/work-orders/${workOrderId}/attachments`),
  });
  const members = useQuery({
    queryKey: ["members", orgId],
    enabled: organization?.role === "Admin" || organization?.role === "Dispatcher",
    queryFn: () => api<Member[]>(`/api/organizations/${orgId}/members`),
  });

  function invalidate() {
    return Promise.all([
      queryClient.invalidateQueries({ queryKey: ["work-order", orgId, workOrderId] }),
      queryClient.invalidateQueries({ queryKey: ["comments", orgId, workOrderId] }),
      queryClient.invalidateQueries({ queryKey: ["activity", orgId, workOrderId] }),
      queryClient.invalidateQueries({ queryKey: ["attachments", orgId, workOrderId] }),
      queryClient.invalidateQueries({ queryKey: ["work-orders"] }),
    ]);
  }

  async function mutate(path: string, method: string, body?: unknown, rowVersion?: string) {
    setError(null);
    try {
      await api(path, {
        method,
        json: body,
        headers: rowVersion ? { "If-Match": rowVersion } : undefined,
      });
      await invalidate();
    } catch (caught) {
      setError(caught);
      if (caught instanceof ApiError && caught.status === 412) await invalidate();
    }
  }

  const workOrder = detail.data;
  const role = organization?.role;
  const isAssignee = workOrder?.assignedTechnicianUserId === user?.id;

  if (detail.isLoading) return <p>Loading work order…</p>;
  if (detail.isError) return <Banner error={detail.error} />;
  if (!workOrder || !orgId) return <Empty title="Not available" body="This work order could not be loaded." />;

  const base = `/api/organizations/${orgId}/work-orders/${workOrder.id}`;
  const transitions = allowedTransitions(workOrder, role, isAssignee, workOrder.requesterUserId === user?.id);

  return (
    <section>
      <header className="page-head">
        <div>
          <p className="eyebrow">{workOrder.displayNumber} · {workOrder.category}</p>
          <h1>{workOrder.title}</h1>
        </div>
        <div className="pills">
          <span className={`pill ${workOrder.priority.toLowerCase()}`}>{workOrder.priority}</span>
          <span className="pill">{statusLabel(workOrder.status)}</span>
          {workOrder.slaPaused && <span className="pill">SLA paused</span>}
          {workOrder.isOverdue && <span className="pill danger">Overdue</span>}
        </div>
      </header>
      <Banner error={error} />
      {error instanceof ApiError && error.status === 412 && <button type="button" className="button secondary" onClick={() => invalidate()}>Refresh latest version</button>}
      <div className="split">
        <article className="card">
          <p>{workOrder.description || "No description."}</p>
          <dl className="meta">
            <div><dt>Requester</dt><dd>{workOrder.requesterName}</dd></div>
            <div><dt>Assignee</dt><dd>{workOrder.assigneeName ?? "Unassigned"}</dd></div>
            <div><dt>Created</dt><dd>{formatWhen(workOrder.createdAtUtc)}</dd></div>
            <div><dt>Due</dt><dd>{formatWhen(workOrder.dueAtUtc)}</dd></div>
            <div><dt>Resolved</dt><dd>{formatWhen(workOrder.resolvedAtUtc)}</dd></div>
            <div><dt>Closed</dt><dd>{formatWhen(workOrder.closedAtUtc)}</dd></div>
          </dl>
          <div className="actions">
            {role === "Technician" && workOrder.status === "Open" && !workOrder.assignedTechnicianUserId && (
              <button type="button" className="button" onClick={async () => {
                setError(null);
                try {
                  await api(`${base}/claim`, { method: "POST", headers: { "Idempotency-Key": claimKey.current } });
                  claimKey.current = crypto.randomUUID();
                  await invalidate();
                } catch (caught) {
                  setError(caught);
                }
              }}>Claim</button>
            )}
            {(role === "Admin" || role === "Dispatcher") && workOrder.status !== "Closed" && workOrder.status !== "Cancelled" && workOrder.status !== "OnHold" && (
              <form className="inline" onSubmit={async (event) => {
                event.preventDefault();
                const technicianUserId = String(new FormData(event.currentTarget).get("technicianUserId"));
                await mutate(`${base}/assign`, "POST", { technicianUserId }, workOrder.rowVersion);
              }}>
                <label>Assign technician
                  <select name="technicianUserId" required defaultValue="">
                    <option value="" disabled>Select</option>
                    {(members.data ?? []).filter((member) => member.role === "Technician").map((member) => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}
                  </select>
                </label>
                <button className="button secondary" type="submit">Assign</button>
              </form>
            )}
            {(role === "Admin" || role === "Dispatcher") && workOrder.status === "Assigned" && (
              <button type="button" className="button secondary" onClick={() => mutate(`${base}/unassign`, "POST", {}, workOrder.rowVersion)}>Unassign</button>
            )}
            {transitions.map((status) => (
              <button key={status} type="button" className={status === "Cancelled" ? "button danger" : "button secondary"} onClick={() => {
                if (status === "Cancelled") setConfirmCancel(true);
                else void mutate(`${base}/status`, "POST", { status }, workOrder.rowVersion);
              }}>{workOrder.status === "OnHold" ? "Resume" : status === "InProgress" && (workOrder.status === "Resolved" || workOrder.status === "Closed") ? "Reopen" : statusLabel(status)}</button>
            ))}
          </div>
        </article>
        <article className="card">
          <h2>Activity</h2>
          <ol className="timeline">
            {(activity.data ?? []).map((item) => (
              <li key={item.id}><strong>{item.activityType}</strong><span>{item.message}</span><time>{formatWhen(item.createdAtUtc)}</time></li>
            ))}
          </ol>
        </article>
      </div>
      <div className="split">
        <article className="card">
          <h2>Comments</h2>
          {(comments.data ?? []).length === 0 && <p>No comments yet.</p>}
          <ul className="comments">
            {(comments.data ?? []).map((comment) => <li key={comment.id}><strong>{comment.authorName}</strong> <time>{formatWhen(comment.createdAtUtc)}</time><p>{comment.body}</p></li>)}
          </ul>
          {workOrder.status !== "Cancelled" && (
            <form className="stack" onSubmit={async (event) => {
              event.preventDefault();
              const form = event.currentTarget;
              const body = String(new FormData(form).get("body") ?? "");
              await mutate(`${base}/comments`, "POST", { body });
              form.reset();
            }}>
              <Field label="Add a comment"><textarea name="body" required maxLength={2000} rows={3} /></Field>
              <button className="button" type="submit">Post comment</button>
            </form>
          )}
        </article>
        <article className="card">
          <h2>Attachments</h2>
          <ul>
            {(attachments.data ?? []).map((file) => (
              <li key={file.id}>
                <button type="button" className="linkish" onClick={() => downloadAttachment(`${base}/attachments/${file.id}`, file.fileName)}>{file.fileName}</button>
                <span>{Math.ceil(file.sizeBytes / 1024)} KB · {file.uploadedByName}</span>
                {(role === "Admin" || role === "Dispatcher" || file.uploadedByUserId === user?.id) && (
                  <button type="button" className="button danger" onClick={async () => {
                    if (window.confirm(`Delete ${file.fileName}?`)) await mutate(`${base}/attachments/${file.id}`, "DELETE");
                    await queryClient.invalidateQueries({ queryKey: ["attachments", orgId, workOrderId] });
                  }}>Delete</button>
                )}
              </li>
            ))}
          </ul>
          <form className="stack" onSubmit={async (event) => {
            event.preventDefault();
            const form = event.currentTarget;
            const input = form.elements.namedItem("file") as HTMLInputElement;
            if (!input.files?.[0]) return;
            const body = new FormData();
            body.set("file", input.files[0]);
            setError(null);
            try {
              await api(`${base}/attachments`, { method: "POST", body });
              form.reset();
              await queryClient.invalidateQueries({ queryKey: ["attachments", orgId, workOrderId] });
              await queryClient.invalidateQueries({ queryKey: ["activity", orgId, workOrderId] });
            } catch (caught) {
              setError(caught);
            }
          }}>
            <Field label="Upload pdf, png, jpg, gif, webp, txt, or csv"><input name="file" type="file" accept=".pdf,.png,.jpg,.jpeg,.gif,.webp,.txt,.csv" /></Field>
            <button className="button secondary" type="submit">Upload</button>
          </form>
        </article>
      </div>
      {(role === "Admin" || role === "Dispatcher" || (role === "Requester" && workOrder.requesterUserId === user?.id && workOrder.status === "Open")) && workOrder.status !== "Closed" && workOrder.status !== "Cancelled" && (
        <form key={workOrder.rowVersion} className="card stack" onSubmit={async (event) => {
          event.preventDefault();
          const form = new FormData(event.currentTarget);
          await mutate(base, "PUT", {
            title: String(form.get("title")),
            description: String(form.get("description")),
            category: String(form.get("category")),
            priority: String(form.get("priority")),
          }, workOrder.rowVersion);
        }}>
          <h2>Edit details</h2>
          <Field label="Title"><input name="title" defaultValue={workOrder.title} required maxLength={200} /></Field>
          <Field label="Category"><input name="category" defaultValue={workOrder.category} required maxLength={100} /></Field>
          <Field label="Priority"><select name="priority" defaultValue={workOrder.priority}>{priorities.filter(Boolean).map((item) => <option key={item}>{item}</option>)}</select></Field>
          <Field label="Description"><textarea name="description" defaultValue={workOrder.description} maxLength={4000} rows={4} /></Field>
          <button className="button" type="submit">Save changes</button>
        </form>
      )}
      {confirmCancel && (
        <Modal title="Cancel this work order?" onClose={() => setConfirmCancel(false)}>
          <p>Cancellation is terminal. The work order will stay in the audit history.</p>
          <div className="actions">
            <button type="button" className="button secondary" onClick={() => setConfirmCancel(false)}>Keep it</button>
            <button type="button" className="button danger" onClick={async () => {
              setConfirmCancel(false);
              await mutate(`${base}/status`, "POST", { status: "Cancelled" }, workOrder.rowVersion);
            }}>Cancel work order</button>
          </div>
        </Modal>
      )}
    </section>
  );
}

function allowedTransitions(workOrder: WorkOrderDetail, role: string | undefined, isAssignee: boolean, isRequester: boolean): Status[] {
  const status = workOrder.status;
  const staff = role === "Admin" || role === "Dispatcher";
  const tech = role === "Technician" && isAssignee;
  if (status === "Open") return staff || (role === "Requester" && isRequester) ? ["Cancelled"] : [];
  if (status === "Assigned") return [...(staff || tech ? ["InProgress", "OnHold"] as Status[] : []), ...(staff ? ["Cancelled"] as Status[] : [])];
  if (status === "InProgress") return [...(staff || tech ? ["OnHold", "Resolved"] as Status[] : []), ...(staff ? ["Cancelled"] as Status[] : [])];
  if (status === "OnHold") return staff || tech ? [workOrder.holdResumeStatus ?? "InProgress"] : [];
  if (status === "Resolved") return [...(staff || (role === "Requester" && isRequester) ? ["Closed"] as Status[] : []), ...(staff ? [workOrder.assignedTechnicianUserId ? "InProgress" : "Open"] as Status[] : [])];
  if (status === "Closed") return staff ? [workOrder.assignedTechnicianUserId ? "InProgress" : "Open"] : [];
  return [];
}
