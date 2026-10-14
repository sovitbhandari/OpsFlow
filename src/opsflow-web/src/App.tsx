import { useQueryClient } from "@tanstack/react-query";
import { useState, type FormEvent } from "react";
import { NavLink, Route, Routes, useNavigate } from "react-router";
import { ApiError, login, logout } from "./api";
import { useAuth } from "./useAuth";
import { Banner, Field } from "./components";
import { AuditPage, MembersPage, NotificationsPage, SlaPage } from "./pages/Admin";
import { DashboardPage } from "./pages/Dashboard";
import { NewWorkOrderPage, WorkOrderDetailPage, WorkOrderListPage } from "./pages/WorkOrders";

export default function App() {
  const { user, loading } = useAuth();
  if (loading) return <p className="boot">Loading OpsFlow…</p>;
  if (!user) return <LoginPage />;
  return <Shell />;
}

function Shell() {
  const { user, organization, setOrganizationId } = useAuth();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const admin = organization?.role === "Admin";
  const dispatch = admin || organization?.role === "Dispatcher";
  return (
    <div className="shell">
      <aside>
        <p className="brand">OpsFlow</p>
        <nav>
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/work-orders">Work orders</NavLink>
          {dispatch && <NavLink to="/members">Members</NavLink>}
          {admin && <NavLink to="/sla">SLA policies</NavLink>}
          {admin && <NavLink to="/audit">Audit</NavLink>}
          {admin && <NavLink to="/notifications">Notifications</NavLink>}
        </nav>
      </aside>
      <div className="main">
        <header className="topbar">
          <label>
            Organization
            <select aria-label="Organization" value={organization?.id ?? ""} onChange={(event) => { setOrganizationId(event.target.value); navigate("/"); }}>
              {(user?.organizations ?? []).map((item) => <option key={item.id} value={item.id}>{item.name} · {item.role}</option>)}
            </select>
          </label>
          <div className="who">
            <span>{user?.displayName}</span>
            <button type="button" className="button secondary" onClick={async () => { await logout(); await queryClient.invalidateQueries({ queryKey: ["me"] }); }}>Log out</button>
          </div>
        </header>
        <main>
          <Routes>
            <Route index element={<DashboardPage />} />
            <Route path="work-orders" element={<WorkOrderListPage />} />
            <Route path="work-orders/new" element={<NewWorkOrderPage />} />
            <Route path="work-orders/:workOrderId" element={<WorkOrderDetailPage />} />
            <Route path="members" element={<MembersPage />} />
            <Route path="sla" element={<SlaPage />} />
            <Route path="audit" element={<AuditPage />} />
            <Route path="notifications" element={<NotificationsPage />} />
          </Routes>
        </main>
      </div>
    </div>
  );
}

function LoginPage() {
  const queryClient = useQueryClient();
  const [error, setError] = useState<unknown>(null);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    try {
      await login(String(form.get("email")), String(form.get("password")));
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      setError(null);
    } catch (caught) {
      setError(caught instanceof ApiError && caught.status === 401 ? new Error("The email or password is incorrect.") : caught);
    }
  }
  return (
    <main className="login">
      <form className="card stack" onSubmit={submit}>
        <p className="eyebrow">Service operations</p>
        <h1>Sign in to OpsFlow</h1>
        <Banner error={error} />
        <Field label="Email"><input name="email" type="email" autoComplete="username" required /></Field>
        <Field label="Password"><input name="password" type="password" autoComplete="current-password" required /></Field>
        <button className="button" type="submit">Sign in</button>
      </form>
    </main>
  );
}
