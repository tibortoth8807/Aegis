import { FormEvent, useMemo, useState } from 'react';
import { RequestDetail as Detail, SystemDto } from '../api/types';
import { useApp, useLoad } from '../app';
import {
  ActionBar, Empty, Failure, Field, fmtDate, fmtTime, History, human, Loading, PageHead, Pager, Person, StateRail, Status, Tabs, useDebounced,
} from '../components/ui';

/** "My requests" (and, for managers and staff, the team's and everyone's), or the approvals inbox. */
export function Requests({ approvals = false }: { approvals?: boolean }) {
  const app = useApp();
  const [scope, setScope] = useState<'mine' | 'team' | 'all'>('mine');
  const [status, setStatus] = useState(approvals ? '' : 'open');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const q = useDebounced(search);
  const effectiveScope = approvals ? 'approvals' : scope;
  const list = useLoad(() => app.api.requests({ page, pageSize: 20, search: q }, { scope: effectiveScope, status }), [effectiveScope, status, q, page]);
  const canAll = app.has('Hr') || app.has('SecurityOfficer');

  return (
    <div className="page">
      <PageHead
        title={approvals ? 'Approvals' : 'Access requests'}
        intro={approvals
          ? 'Requests where you are the next approver: as the manager, the application owner or a security officer.'
          : 'Access you asked for, for yourself or for your team. Each request goes through manager and application owner approval.'}
        actions={<a className="btn primary" href="#/requests/new">Request access</a>}
      />
      {!approvals && (
        <Tabs value={scope} onChange={(v) => { setScope(v as typeof scope); setPage(1); }} options={[
          ['mine', 'Mine'],
          ...(app.has('Manager') ? [['team', 'My team'] as [string, string]] : []),
          ...(canAll ? [['all', 'Everyone'] as [string, string]] : []),
        ]} />
      )}
      <div className="filters">
        <Field label="Search"><input type="search" value={search} placeholder="Number, application, role or person" onChange={(e) => { setSearch(e.target.value); setPage(1); }} /></Field>
        {!approvals && (
          <Field label="Status">
            <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
              <option value="open">Open</option>
              <option value="">All</option>
              {['Draft', 'PendingManager', 'PendingOwner', 'PendingSecurity', 'Approved', 'Active', 'Rejected', 'Cancelled', 'Expired', 'Revoked'].map((s) => <option key={s} value={s}>{human(s)}</option>)}
            </select>
          </Field>
        )}
      </div>
      {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.items.length === 0 ? (
        approvals ? <Empty title="Nothing waits for you">When someone needs your approval, it shows up here and in the menu.</Empty>
          : <Empty title="No requests here">Use “Request access” to ask for a role in one of the applications.</Empty>
      ) : (
        <>
          <div className="table-wrap">
            <table className="grid">
              <thead>
                <tr><th>Request</th><th>Application and role</th><th>For</th><th>Requested by</th><th>Created</th><th>Status</th></tr>
              </thead>
              <tbody>
                {list.data.items.map((r) => (
                  <tr key={r.id} className={r.awaitsMe ? 'mark' : ''}>
                    <td className="num"><a href={`#/requests/${r.id}`}>{r.number}</a></td>
                    <td>{r.system} – {r.role}{r.privileged && <span className="tag priv">privileged</span>}</td>
                    <td>{r.beneficiary}</td>
                    <td>{r.requester}</td>
                    <td className="num">{fmtDate(r.createdAt)}</td>
                    <td><Status value={r.status} />{r.awaitsMe && <span className="tag you">your turn</span>}</td>
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

const OUTCOME: Record<string, string> = { Rejected: 'bad', Cancelled: 'quiet', Expired: 'bad', Revoked: 'bad' };

export function RequestView({ id }: { id: number }) {
  const app = useApp();
  const { data: r, error } = useLoad(() => app.api.request(id), [id]);
  if (error) return <div className="page"><Failure error={error} /></div>;
  if (!r) return <div className="page"><Loading /></div>;
  const run = async (action: string, comment: string | null) => {
    try {
      const after = await app.api.requestTransition(r.id, action, comment);
      app.toast(message(action, after));
      app.changed();
    } catch (e) { app.fail(e); }
  };
  const outcome = OUTCOME[r.status] ? { label: `${human(r.status)} ${fmtDate(r.decidedAt)}`, tone: OUTCOME[r.status] } : null;
  return (
    <div className="page">
      <PageHead title={`${r.system} – ${r.role}`} intro={<>{r.number}, for <Person p={r.beneficiary} />{r.requester.id !== r.beneficiary.id && <>, requested by <Person p={r.requester} /></>}</>} />
      <section className="card wide">
        <StateRail steps={r.steps} current={r.currentStep} outcome={outcome}>
          <ActionBar actions={r.allowedActions} onAction={run} />
          {r.allowedActions.length === 0 && !outcome && r.status !== 'Active' && <p className="muted">Waiting for {nextActor(r)}.</p>}
        </StateRail>
      </section>
      <div className="detail-grid">
        <section className="card">
          <h2>Request</h2>
          <blockquote className="justification">{r.justification}</blockquote>
          <dl className="facts">
            <div><dt>Role</dt><dd>{r.role}{r.privileged && <span className="tag priv">privileged</span>}</dd></div>
            <div><dt>Created</dt><dd>{fmtTime(r.createdAt)}</dd></div>
            <div><dt>Submitted</dt><dd>{fmtTime(r.submittedAt)}</dd></div>
            <div><dt>Decided</dt><dd>{fmtTime(r.decidedAt)}</dd></div>
            <div><dt>Valid until</dt><dd>{fmtDate(r.validUntil)}</dd></div>
          </dl>
          {r.privileged && <p className="note">Privileged roles need a security officer's approval as well and are granted for at most 30 days.</p>}
        </section>
        <section className="card">
          <h2>History</h2>
          <History items={r.history} />
        </section>
      </div>
    </div>
  );
}

function nextActor(r: Detail) {
  return ({ PendingManager: `${r.beneficiary.name}'s manager`, PendingOwner: `the owner of ${r.system}`, PendingSecurity: 'a security officer', Approved: 'provisioning', Draft: 'the requester to submit it' } as Record<string, string>)[r.status] ?? 'the next step';
}

function message(action: string, r: Detail) {
  if (action === 'approve') return r.status === 'Active' ? `${r.number} approved - the access is active.` : `${r.number} approved. Next: ${nextActor(r)}.`;
  if (action === 'reject') return `${r.number} rejected.`;
  if (action === 'cancel') return `${r.number} cancelled.`;
  if (action === 'revoke') return `Access from ${r.number} revoked.`;
  if (action === 'provision') return `${r.number} provisioned.`;
  return `${r.number}: ${human(r.status)}.`;
}

export function NewRequest() {
  const app = useApp();
  const apps = useLoad(() => app.api.applications(), []);
  const reports = useLoad(() => (app.me ? app.api.employee(app.me.id) : Promise.resolve(null)), [app.me?.id]);
  const [systemId, setSystemId] = useState<number | null>(null);
  const [roleId, setRoleId] = useState<number | null>(null);
  const [beneficiaryId, setBeneficiaryId] = useState<number | null>(null);
  const [justification, setJustification] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [filter, setFilter] = useState('');
  const system: SystemDto | undefined = useMemo(() => apps.data?.find((a) => a.id === systemId), [apps.data, systemId]);
  const role = system?.roles.find((r) => r.id === roleId);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!roleId) return;
    setBusy(true);
    setError(null);
    try {
      const r = await app.api.createRequest({ roleId, justification, beneficiaryId, submit: true });
      app.toast(`${r.number} submitted - waiting for ${nextActor(r)}.`);
      app.changed();
      app.navigate(`/requests/${r.id}`);
    } catch (err) {
      const fe = (err as { errors?: Record<string, string[]> }).errors;
      setError(fe && Object.keys(fe).length ? Object.values(fe).flat().join(' ') : (err as Error).message);
    } finally { setBusy(false); }
  };
  if (apps.error) return <div className="page"><Failure error={apps.error} /></div>;
  if (!apps.data) return <div className="page"><Loading /></div>;
  const shown = apps.data.filter((a) => !filter || `${a.name} ${a.code} ${a.roles.map((r) => r.name).join(' ')}`.toLowerCase().includes(filter.toLowerCase()));
  const team = reports.data?.directReports ?? [];

  return (
    <div className="page narrow">
      <PageHead title="Request access" intro="Pick the application and the role, and say why it is needed. Your manager and the application owner decide." />
      <form className="form" onSubmit={submit}>
        {team.length > 0 && (
          <Field label="For" hint="Managers can request access for their direct reports; the manager approval is then already given.">
            <select value={beneficiaryId ?? ''} onChange={(e) => setBeneficiaryId(e.target.value ? Number(e.target.value) : null)}>
              <option value="">Myself</option>
              {team.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </select>
          </Field>
        )}
        <fieldset className="pick">
          <legend>Application</legend>
          <input type="search" className="pick-filter" placeholder="Filter applications" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter applications" />
          <div className="pick-list">
            {shown.map((a) => (
              <label key={a.id} className={`pick-item${systemId === a.id ? ' on' : ''}`}>
                <input type="radio" name="system" checked={systemId === a.id} onChange={() => { setSystemId(a.id); setRoleId(null); }} />
                <strong>{a.name}</strong>
                <span>{a.description}</span>
                {a.requiredTraining && <span className="req">Needs the training “{a.requiredTraining}”</span>}
              </label>
            ))}
          </div>
        </fieldset>
        {system && (
          <fieldset className="pick">
            <legend>Role in {system.name}</legend>
            <div className="pick-list roles">
              {system.roles.map((r) => (
                <label key={r.id} className={`pick-item${roleId === r.id ? ' on' : ''}`}>
                  <input type="radio" name="role" checked={roleId === r.id} onChange={() => setRoleId(r.id)} />
                  <strong>{r.name}{r.privileged && <span className="tag priv">privileged</span>}</strong>
                  <span>{r.description}</span>
                  <span className="req">Valid for {r.privileged ? Math.min(r.validityDays, 30) : r.validityDays} days{r.privileged ? ', needs a security officer as well' : ''}</span>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <Field label="Why is it needed?" hint={`At least 15 characters. ${justification.trim().length}/15`}>
          <textarea rows={3} value={justification} onChange={(e) => setJustification(e.target.value)} placeholder="e.g. Preparing the quarterly customer review" />
        </Field>
        {role && (
          <p className="route-preview">
            Approval route: {beneficiaryId ? 'you (as manager, already given)' : 'your manager'} → owner of {system!.name}{role.privileged ? ' → security officer' : ''} → {system!.provisioning === 'manual' ? 'manual provisioning' : 'automatic provisioning'}.
          </p>
        )}
        {error && <p className="form-error" role="alert">{error}</p>}
        <div className="form-actions">
          <a className="btn" href="#/requests">Cancel</a>
          <button type="submit" className="btn primary" disabled={busy || !roleId}>Submit request</button>
        </div>
      </form>
    </div>
  );
}
