import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { api, type AuditItem, type Member, type NotificationJob, type Paged, type Role, type SlaPolicy } from "../api";
import { useAuth } from "../useAuth";
import { Banner, Empty, Field } from "../components";
import { formatDuration, formatWhen } from "../format";

export function MembersPage() {
  const { organization } = useAuth();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const members = useQuery({
    queryKey: ["members", organization?.id],
    enabled: Boolean(organization),
    queryFn: () => api<Member[]>(`/api/organizations/${organization?.id}/members`),
  });
  const add = useMutation({
    mutationFn: (input: { email: string; displayName: string; role: Role; password?: string }) =>
      api(`/api/organizations/${organization?.id}/members`, { method: "POST", json: input }),
    onSuccess: async () => {
      setError(null);
      await queryClient.invalidateQueries({ queryKey: ["members", organization?.id] });
    },
    onError: setError,
  });

  if (!organization || (organization.role !== "Admin" && organization.role !== "Dispatcher")) {
    return <Banner error={new Error("Only admins and dispatchers can view members. Role changes are limited to admins.")} />;
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const formElement = event.currentTarget;
    const form = new FormData(formElement);
    const password = String(form.get("password") ?? "");
    add.mutate({
      email: String(form.get("email") ?? ""),
      displayName: String(form.get("displayName") ?? ""),
      role: String(form.get("role")) as Role,
      password: password || undefined,
    }, { onSuccess: () => formElement.reset() });
  }

  return (
    <section>
      <h1>Members</h1>
      <Banner error={error ?? members.error} />
      {members.data && members.data.length === 0 && <Empty title="No members" body="Add the first teammate." />}
      <div className="table-wrap">
        <table>
          <thead><tr><th>Name</th><th>Email</th><th>Role</th>{organization.role === "Admin" && <th>Actions</th>}</tr></thead>
          <tbody>
            {(members.data ?? []).map((member) => (
              <tr key={member.userId}>
                <td>{member.displayName}</td>
                <td>{member.email}</td>
                <td>{member.role}</td>
                {organization.role === "Admin" && (
                  <td className="actions">
                    <select aria-label={`Role for ${member.displayName}`} value={member.role} onChange={async (event) => {
                      try {
                        await api(`/api/organizations/${organization.id}/members/${member.userId}`, { method: "PUT", json: { role: event.target.value } });
                        await queryClient.invalidateQueries({ queryKey: ["members", organization.id] });
                      } catch (caught) {
                        setError(caught);
                      }
                    }}>
                      {(["Admin", "Dispatcher", "Technician", "Requester"] as Role[]).map((role) => <option key={role}>{role}</option>)}
                    </select>
                    <button type="button" className="button danger" onClick={async () => {
                      if (!window.confirm(`Remove ${member.displayName}?`)) return;
                      try {
                        await api(`/api/organizations/${organization.id}/members/${member.userId}`, { method: "DELETE" });
                        await queryClient.invalidateQueries({ queryKey: ["members", organization.id] });
                      } catch (caught) {
                        setError(caught);
                      }
                    }}>Remove</button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {organization.role === "Admin" && (
        <form className="card stack" onSubmit={submit}>
          <h2>Add member</h2>
          <p className="hint">If the email already has an OpsFlow account, leave the password blank. A password is required only when creating a new account, and it is not stored in the browser.</p>
          <Field label="Email"><input name="email" type="email" required /></Field>
          <Field label="Display name"><input name="displayName" required maxLength={120} /></Field>
          <Field label="Role"><select name="role" defaultValue="Requester">{(["Admin", "Dispatcher", "Technician", "Requester"] as Role[]).map((role) => <option key={role}>{role}</option>)}</select></Field>
          <Field label="Password for a new account"><input name="password" type="password" autoComplete="new-password" minLength={12} /></Field>
          <button className="button" type="submit" disabled={add.isPending}>Add member</button>
        </form>
      )}
    </section>
  );
}

export function SlaPage() {
  const { organization } = useAuth();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const policies = useQuery({
    queryKey: ["sla", organization?.id],
    enabled: Boolean(organization),
    queryFn: () => api<SlaPolicy[]>(`/api/organizations/${organization?.id}/sla-policies`),
  });
  if (organization?.role !== "Admin") return <Banner error={new Error("Only an organization admin can manage SLA policies.")} />;
  return (
    <section className="narrow">
      <h1>SLA policies</h1>
      <p className="hint">These are elapsed-time targets. Putting a work order on hold pauses the SLA clock. Business-hour calendars are not used.</p>
      <Banner error={error ?? policies.error} />
      {policies.data && (
        <form className="stack" onSubmit={async (event) => {
          event.preventDefault();
          const form = new FormData(event.currentTarget);
          const body = (policies.data ?? []).map((policy) => ({
            priority: policy.priority,
            durationSeconds: Number(form.get(policy.priority)) * 60,
          }));
          try {
            await api(`/api/organizations/${organization.id}/sla-policies`, { method: "PUT", json: { policies: body } });
            await queryClient.invalidateQueries({ queryKey: ["sla", organization.id] });
            setError(null);
          } catch (caught) {
            setError(caught);
          }
        }}>
          {policies.data.map((policy) => (
            <Field key={policy.priority} label={`${policy.priority} duration in minutes`}>
              <input name={policy.priority} type="number" min={15} required defaultValue={Math.round(policy.durationSeconds / 60)} />
            </Field>
          ))}
          <button className="button" type="submit">Save policies</button>
          <p className="hint">Last change {formatWhen(policies.data[0]?.updatedAtUtc)}. Current critical target {formatDuration(policies.data.find((item) => item.priority === "Critical")?.durationSeconds)}.</p>
        </form>
      )}
    </section>
  );
}

export function AuditPage() {
  const { organization } = useAuth();
  const [page, setPage] = useState(1);
  const audit = useQuery({
    queryKey: ["audit", organization?.id, page],
    enabled: organization?.role === "Admin",
    queryFn: () => api<Paged<AuditItem>>(`/api/organizations/${organization?.id}/audit?page=${page}&pageSize=25`),
  });
  if (organization?.role !== "Admin") return <Banner error={new Error("Only an organization admin can view audit history.")} />;
  return (
    <section>
      <h1>Audit history</h1>
      <p className="hint">Audit rows are append-only through the application. They are ordinary database records, not a tamper-proof ledger.</p>
      <Banner error={audit.error} />
      {audit.data && audit.data.items.length === 0 && <Empty title="No audit events" body="Changes will appear here." />}
      <ul className="timeline">
        {(audit.data?.items ?? []).map((item) => (
          <li key={item.id}><strong>{item.action}</strong><span>{item.summary}</span><time>{item.actorName ?? "System"} · {formatWhen(item.createdAtUtc)}</time></li>
        ))}
      </ul>
      <div className="pager">
        <button type="button" disabled={page <= 1} onClick={() => setPage((current) => current - 1)}>Previous</button>
        <span>Page {page}</span>
        <button type="button" disabled={!audit.data || page * audit.data.pageSize >= audit.data.totalCount} onClick={() => setPage((current) => current + 1)}>Next</button>
      </div>
    </section>
  );
}

export function NotificationsPage() {
  const { organization } = useAuth();
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  const [page, setPage] = useState(1);
  const jobs = useQuery({
    queryKey: ["jobs", organization?.id, page],
    enabled: organization?.role === "Admin",
    queryFn: () => api<Paged<NotificationJob>>(`/api/organizations/${organization?.id}/notification-jobs?page=${page}&pageSize=25`),
  });
  if (organization?.role !== "Admin") return <Banner error={new Error("Only an organization admin can inspect notification jobs.")} />;
  return (
    <section>
      <h1>Notification jobs</h1>
      <p className="hint">Delivery is at-least-once. A crash after SMTP accepts a message and before the job is marked complete can send the email again on retry.</p>
      <Banner error={error ?? jobs.error} />
      <div className="table-wrap">
        <table>
          <thead><tr><th>Type</th><th>Status</th><th>Attempts</th><th>Next attempt</th><th>Last error</th><th></th></tr></thead>
          <tbody>
            {(jobs.data?.items ?? []).map((job) => (
              <tr key={job.id}>
                <td>{job.type}</td>
                <td>{job.status}</td>
                <td>{job.attemptCount}</td>
                <td>{formatWhen(job.nextAttemptAtUtc)}</td>
                <td>{job.lastError ?? "—"}</td>
                <td>{(job.status === "Failed" || job.status === "Succeeded") && <button type="button" className="button secondary" onClick={async () => {
                  try {
                    await api(`/api/organizations/${organization.id}/notification-jobs/${job.id}/retry`, { method: "POST" });
                    await queryClient.invalidateQueries({ queryKey: ["jobs", organization.id] });
                  } catch (caught) {
                    setError(caught);
                  }
                }}>Retry</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="pager">
        <button type="button" disabled={page <= 1} onClick={() => setPage((current) => current - 1)}>Previous</button>
        <button type="button" disabled={!jobs.data || page * jobs.data.pageSize >= jobs.data.totalCount} onClick={() => setPage((current) => current + 1)}>Next</button>
      </div>
    </section>
  );
}
