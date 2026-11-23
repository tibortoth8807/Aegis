import { FormEvent, useState } from 'react';
import { useApp, useLoad } from '../app';
import { ActionBar, Empty, Failure, Field, fmtTime, History, human, Loading, PageHead, Pager, Person, Priority, StateRail, Status, Tabs } from '../components/ui';

export function Tickets() {
  const app = useApp();
  const agent = app.has('Hr');
  const [scope, setScope] = useState(agent ? 'queue' : 'mine');
  const [status, setStatus] = useState('open');
  const [page, setPage] = useState(1);
  const list = useLoad(() => app.api.tickets({ page, pageSize: 20 }, scope, status), [scope, status, page]);
  return (
    <div className="page">
      <PageHead title="Tickets" intro="Problems with access, your employee record or trainings. P1 is answered within 4 hours, P4 within 3 days." actions={<a className="btn primary" href="#/tickets/new">Report a problem</a>} />
      <Tabs value={scope} onChange={(v) => { setScope(v); setPage(1); }} options={[...(agent ? [['queue', 'Support queue'] as [string, string]] : []), ['mine', 'Reported by me']]} />
      <div className="filters">
        <Field label="Status">
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="open">Open</option>
            <option value="">All</option>
            {['New', 'InProgress', 'OnHold', 'Resolved', 'Closed', 'Cancelled'].map((s) => <option key={s} value={s}>{human(s)}</option>)}
          </select>
        </Field>
      </div>
      {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.items.length === 0 ? <Empty title="No tickets here">Everything works - or nobody told us yet.</Empty> : (
        <>
          <div className="table-wrap">
            <table className="grid">
              <thead><tr><th>Ticket</th><th>Priority</th><th>Title</th><th>Reporter</th><th>Assignee</th><th>Response due</th><th>Status</th></tr></thead>
              <tbody>
                {list.data.items.map((t) => (
                  <tr key={t.id} className={t.breached ? 'late' : ''}>
                    <td className="num"><a href={`#/tickets/${t.id}`}>{t.number}</a></td>
                    <td><Priority value={t.priority} /></td>
                    <td>{t.title}<div className="sub">{human(t.category)}</div></td>
                    <td>{t.reporter}</td>
                    <td>{t.assignee ?? '–'}</td>
                    <td className="num">{fmtTime(t.dueAt)}{t.breached && <span className="tag late">overdue</span>}</td>
                    <td><Status value={t.status} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Pager page={page} size={20} total={list.data.total} onPage={setPage} />
        </>
      )}
    </div>
  );
}

const STEPS = ['New', 'In progress', 'Resolved', 'Closed'];
const INDEX: Record<string, number> = { New: 0, InProgress: 1, OnHold: 1, Resolved: 2, Closed: 3 };

export function TicketView({ id }: { id: number }) {
  const app = useApp();
  const { data: t, error } = useLoad(() => app.api.ticket(id), [id]);
  if (error) return <div className="page"><Failure error={error} /></div>;
  if (!t) return <div className="page"><Loading /></div>;
  const run = async (action: string, comment: string | null) => {
    try {
      const after = await app.api.ticketTransition(t.id, action, comment);
      app.toast(`${after.number}: ${human(after.status).toLowerCase()}.`);
      app.changed();
    } catch (e) { app.fail(e); }
  };
  return (
    <div className="page">
      <PageHead title={t.title} intro={<>{t.number}, <Priority value={t.priority} /> {human(t.category).toLowerCase()}, reported by <Person p={t.reporter} /></>} />
      <section className="card wide">
        <StateRail steps={STEPS} current={INDEX[t.status] ?? 0} outcome={t.status === 'Cancelled' ? { label: 'Cancelled', tone: 'quiet' } : null}>
          {t.status === 'OnHold' && <p className="muted">On hold - see the last comment for what it is waiting for.</p>}
          <ActionBar actions={t.allowedActions} onAction={run} />
        </StateRail>
      </section>
      <div className="detail-grid">
        <section className="card">
          <h2>Description</h2>
          <p className="prewrap">{t.description || '–'}</p>
          {t.resolution && <><h3>Resolution</h3><p className="prewrap">{t.resolution}</p></>}
          <dl className="facts">
            <div><dt>Opened</dt><dd>{fmtTime(t.createdAt)}</dd></div>
            <div><dt>Response due</dt><dd>{fmtTime(t.dueAt)}</dd></div>
            <div><dt>Assignee</dt><dd><Person p={t.assignee} /></dd></div>
            <div><dt>Status</dt><dd><Status value={t.status} /></dd></div>
          </dl>
        </section>
        <section className="card"><h2>History</h2><History items={t.history} /></section>
      </div>
    </div>
  );
}

export function NewTicket() {
  const app = useApp();
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [priority, setPriority] = useState('P3');
  const [category, setCategory] = useState('access');
  const [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    try {
      const t = await app.api.createTicket({ title, description, priority, category });
      app.toast(`${t.number} opened. The support team answers by ${fmtTime(t.dueAt)}.`);
      app.changed();
      app.navigate(`/tickets/${t.id}`);
    } catch (err) { app.fail(err); } finally { setBusy(false); }
  };
  return (
    <div className="page narrow">
      <PageHead title="Report a problem" intro="Describe what does not work. The more exact, the faster it is fixed." />
      <form className="form" onSubmit={submit}>
        <Field label="Short title"><input required maxLength={150} value={title} onChange={(e) => setTitle(e.target.value)} placeholder="e.g. CRM says access denied on the reports page" /></Field>
        <div className="row">
          <Field label="Topic">
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="access">Access to an application</option>
              <option value="employee-data">My employee record</option>
              <option value="training">Trainings</option>
              <option value="other">Something else</option>
            </select>
          </Field>
          <Field label="Priority" hint="P1: you cannot work at all. P4: no hurry.">
            <select value={priority} onChange={(e) => setPriority(e.target.value)}>
              <option value="P1">P1 – blocked, answer within 4 hours</option>
              <option value="P2">P2 – seriously hindered, 8 hours</option>
              <option value="P3">P3 – annoying, 1 day</option>
              <option value="P4">P4 – no hurry, 3 days</option>
            </select>
          </Field>
        </div>
        <Field label="What happened?"><textarea rows={5} value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
        <div className="form-actions">
          <a className="btn" href="#/tickets">Cancel</a>
          <button type="submit" className="btn primary" disabled={busy}>Open ticket</button>
        </div>
      </form>
    </div>
  );
}
