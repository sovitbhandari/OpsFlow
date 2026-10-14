import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router";
import { api, type Paged, type ReportSummary, type WorkOrderSummary } from "../api";
import { useAuth } from "../useAuth";
import { Banner, Empty } from "../components";
import { formatDuration, statusLabel } from "../format";

export function DashboardPage() {
  const { organization } = useAuth();
  const orgId = organization?.id;
  const canReport = organization?.role === "Admin" || organization?.role === "Dispatcher";
  const report = useQuery({
    queryKey: ["report", orgId],
    enabled: Boolean(orgId && canReport),
    queryFn: () => {
      const from = new Date();
      from.setUTCDate(from.getUTCDate() - 30);
      return api<ReportSummary>(`/api/organizations/${orgId}/reports/summary?from=${from.toISOString()}&to=${new Date().toISOString()}`);
    },
  });
  const queue = useQuery({
    queryKey: ["queue", orgId],
    enabled: Boolean(orgId),
    queryFn: () => api<Paged<WorkOrderSummary>>(`/api/organizations/${orgId}/work-orders?page=1&pageSize=5&sort=dueAt&direction=asc`),
  });

  if (!organization) return <Empty title="No organization" body="Your account is not a member of an organization yet." />;

  return (
    <section>
      <header className="page-head">
        <div>
          <p className="eyebrow">{organization.role}</p>
          <h1>{organization.name}</h1>
        </div>
        <Link className="button" to="/work-orders/new">New work order</Link>
      </header>
      {canReport && report.isError && <Banner error={report.error} />}
      {canReport && report.data && (
        <>
          <div className="stats">
            <article><span>Created in 30 days</span><strong>{report.data.cohortCount}</strong></article>
            <article><span>Overdue in cohort</span><strong>{report.data.overdueCount}</strong></article>
            <article><span>Resolved in cohort</span><strong>{report.data.resolvedCount}</strong></article>
            <article><span>Avg resolution</span><strong>{formatDuration(report.data.averageResolutionSeconds)}</strong></article>
          </div>
          <div className="split">
            <CountCard title="Status" rows={report.data.statusCounts} />
            <CountCard title="Priority" rows={report.data.priorityCounts} />
          </div>
          <p className="hint">The cohort is work orders created in the last 30 days. Overdue excludes on-hold, resolved, closed, and cancelled work. Resolution time is wall-clock time and its average uses resolved work orders as the denominator.</p>
        </>
      )}
      {!canReport && <p className="hint">Your queue shows unassigned open work and work assigned to you, or the requests you created.</p>}
      <h2>Next up</h2>
      {queue.isLoading && <p>Loading work orders…</p>}
      {queue.isError && <Banner error={queue.error} />}
      {queue.data && queue.data.items.length === 0 && <Empty title="Queue is clear" body="No work orders match your access." />}
      {queue.data && queue.data.items.length > 0 && (
        <ul className="queue">
          {queue.data.items.map((item) => (
            <li key={item.id}>
              <Link to={`/work-orders/${item.id}`}>
                <strong>{item.displayNumber}</strong> {item.title}
              </Link>
              <span className={item.isOverdue ? "pill danger" : "pill"}>{item.isOverdue ? "Overdue" : statusLabel(item.status)}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function CountCard({ title, rows }: { title: string; rows: { label: string; count: number }[] }) {
  return (
    <article className="card">
      <h2>{title}</h2>
      {rows.length === 0 && <p>No rows in this cohort.</p>}
      <ul className="counts">
        {rows.map((row) => <li key={row.label}><span>{statusLabel(row.label)}</span><strong>{row.count}</strong></li>)}
      </ul>
    </article>
  );
}
