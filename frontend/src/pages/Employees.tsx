import { FormEvent, useState } from 'react';
import { ApiError, EmployeeDetail, EmployeeInput } from '../api/types';
import { queryParam, useApp, useLoad } from '../app';
import {
  ActionBar, Empty, Failure, Field, fmtDate, History, human, Loading, PageHead, Pager, Person, SortHead, StateRail, Status, useDebounced,
} from '../components/ui';

export function Employees() {
  const app = useApp();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState(queryParam('status') ?? '');
  const [type, setType] = useState(queryParam('type') ?? '');
  const [teamId, setTeamId] = useState(queryParam('team') ?? '');
  const [locationId, setLocationId] = useState(queryParam('location') ?? '');
  const [sort, setSort] = useState('name');
  const [desc, setDesc] = useState(false);
  const [page, setPage] = useState(1);
  const q = useDebounced(search);
  const lookups = useLoad(() => app.api.lookups(), []);
  const list = useLoad(
    () => app.api.employees({ page, pageSize: 20, search: q, sort, desc }, { status, type, teamId: teamId ? Number(teamId) : undefined, locationId: locationId ? Number(locationId) : undefined }),
    [page, q, sort, desc, status, type, teamId, locationId],
  );
  const onSort = (f: string) => { if (f === sort) setDesc(!desc); else { setSort(f); setDesc(false); } setPage(1); };
  const reset = <T,>(set: (v: T) => void) => (v: T) => { set(v); setPage(1); };

  return (
    <div className="page">
      <PageHead
        title="Employees"
        intro="Everyone in the company directory, including externals and people joining soon."
        actions={app.has('Hr') && <a className="btn primary" href="#/employees/new">Register a new employee</a>}
      />
      <div className="filters">
        <Field label="Search"><input type="search" value={search} placeholder="Name, e-mail, number or job title" onChange={(e) => reset(setSearch)(e.target.value)} /></Field>
        <Field label="Status">
          <select value={status} onChange={(e) => reset(setStatus)(e.target.value)}>
            <option value="">Any status</option>
            {['Preboarding', 'Active', 'OnLeave', 'Offboarding', 'Left'].map((s) => <option key={s} value={s}>{human(s)}</option>)}
          </select>
        </Field>
        <Field label="Type">
          <select value={type} onChange={(e) => reset(setType)(e.target.value)}>
            <option value="">Internal and external</option>
            <option value="Internal">Internal</option>
            <option value="External">External</option>
          </select>
        </Field>
        <Field label="Team">
          <select value={teamId} onChange={(e) => reset(setTeamId)(e.target.value)}>
            <option value="">Any team</option>
            {lookups.data?.departments.map((d) => (
              <optgroup key={d.id} label={d.name}>{d.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</optgroup>
            ))}
          </select>
        </Field>
        <Field label="Location">
          <select value={locationId} onChange={(e) => reset(setLocationId)(e.target.value)}>
            <option value="">Any location</option>
            {lookups.data?.locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </select>
        </Field>
      </div>
      {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.items.length === 0 ? (
        <Empty title="Nobody matches these filters">Clear the search or pick another status.</Empty>
      ) : (
        <>
          <div className="table-wrap">
            <table className="grid">
              <thead>
                <tr>
                  <SortHead label="Name" field="name" sort={sort} desc={desc} onSort={onSort} />
                  <SortHead label="Number" field="number" sort={sort} desc={desc} onSort={onSort} />
                  <SortHead label="Job title" field="title" sort={sort} desc={desc} onSort={onSort} />
                  <th>Team</th>
                  <th>Manager</th>
                  <th>Location</th>
                  <SortHead label="Status" field="status" sort={sort} desc={desc} onSort={onSort} />
                  <SortHead label="Start" field="start" sort={sort} desc={desc} onSort={onSort} />
                </tr>
              </thead>
              <tbody>
                {list.data.items.map((e) => (
                  <tr key={e.id}>
                    <td><a href={`#/employees/${e.id}`}>{e.fullName}</a>{e.type === 'External' && <span className="tag">external</span>}<div className="sub">{e.email}</div></td>
                    <td className="num">{e.employeeNumber}</td>
                    <td>{e.jobTitle}</td>
                    <td>{e.team ?? '–'}</td>
                    <td>{e.manager ?? '–'}</td>
                    <td>{e.location ?? '–'}</td>
                    <td><Status value={e.status} /></td>
                    <td className="num">{fmtDate(e.startDate)}</td>
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

const LIFECYCLE = ['Preboarding', 'Active', 'Offboarding', 'Left'];

export function EmployeeView({ id }: { id: number }) {
  const app = useApp();
  const { data: e, error } = useLoad(() => app.api.employee(id), [id]);
  if (error) return <div className="page"><Failure error={error} /></div>;
  if (!e) return <div className="page"><Loading /></div>;
  const self = app.me?.id === e.id;
  const run = async (action: string, value: string | null) => {
    try {
      await app.api.employeeTransition(e.id, action, action === 'give-notice' ? value : null, action === 'give-notice' ? null : value);
      app.toast(`${e.firstName} ${e.lastName}: ${human(action.replace(/-/g, ''))} done.`);
      app.changed();
    } catch (err) { app.fail(err); }
  };
  const railIndex = e.status === 'OnLeave' ? 1 : LIFECYCLE.indexOf(e.status);
  return (
    <div className="page">
      <PageHead
        title={`${e.firstName} ${e.lastName}`}
        intro={<>{e.jobTitle}{e.team && <>, {e.team}</>}{e.type === 'External' && <> – external, {e.externalCompany}</>}</>}
        actions={app.has('Hr') && <a className="btn" href={`#/employees/${e.id}/edit`}>Edit details</a>}
      />
      <div className="detail-grid">
        <section className="card">
          <h2>Employment</h2>
          <StateRail steps={['Preboarding', e.status === 'OnLeave' ? 'On leave' : 'Active', 'Offboarding', 'Left']} current={railIndex}>
            <ActionBar actions={e.allowedActions} onAction={run} extra={{
              'give-notice': { title: 'Give notice', label: 'Last working day', kind: 'date', required: true, confirm: 'Give notice' },
              'start-leave': { title: 'Start leave', label: 'Comment (optional)', kind: 'text', required: false, confirm: 'Start leave' },
            }} />
          </StateRail>
          <dl className="facts">
            <div><dt>Employee number</dt><dd>{e.employeeNumber}</dd></div>
            <div><dt>E-mail</dt><dd><a href={`mailto:${e.email}`}>{e.email}</a></dd></div>
            <div><dt>Manager</dt><dd><Person p={e.manager} /></dd></div>
            <div><dt>Location</dt><dd>{e.location ?? '–'}</dd></div>
            <div><dt>Nationality</dt><dd>{e.nationalityCode ?? '–'}</dd></div>
            <div><dt>Started</dt><dd>{fmtDate(e.startDate)}</dd></div>
            <div><dt>Ends</dt><dd>{fmtDate(e.endDate)}</dd></div>
            <div><dt>Portal roles</dt><dd>{e.portalRoles.map(human).join(', ')}</dd></div>
            <div><dt>Record source</dt><dd>{e.source}</dd></div>
          </dl>
          {e.directReports.length > 0 && (
            <>
              <h3>Direct reports</h3>
              <ul className="people">{e.directReports.map((p) => <li key={p.id}><Person p={p} /></li>)}</ul>
            </>
          )}
        </section>
        <section className="card">
          <h2>Access</h2>
          {e.access.length === 0 ? <Empty title="No access through Aegis">{self && <a href="#/requests/new">Request access</a>}</Empty> : (
            <table className="grid compact">
              <thead><tr><th>Application</th><th>Role</th><th>Valid until</th></tr></thead>
              <tbody>
                {e.access.map((g) => (
                  <tr key={g.id}><td>{g.system}</td><td>{g.role}{g.privileged && <span className="tag priv">privileged</span>}</td><td className="num"><a href={`#/requests/${g.requestId}`}>{fmtDate(g.validUntil)}</a></td></tr>
                ))}
              </tbody>
            </table>
          )}
          <h2>Trainings</h2>
          {e.trainings.length === 0 ? <Empty title="No trainings assigned" /> : (
            <table className="grid compact">
              <thead><tr><th>Course</th><th>Status</th><th>Due</th><th>Valid until</th></tr></thead>
              <tbody>
                {e.trainings.map((t) => <tr key={t.id}><td>{t.course}</td><td><Status value={t.status} /></td><td className="num">{fmtDate(t.dueOn)}</td><td className="num">{fmtDate(t.validUntil)}</td></tr>)}
              </tbody>
            </table>
          )}
          <h2>History</h2>
          <History items={e.history} />
        </section>
      </div>
    </div>
  );
}

const blank: EmployeeInput = {
  employeeNumber: '', firstName: '', lastName: '', email: '', jobTitle: '', type: 'Internal', externalCompany: null, nationalityCode: 'HU',
  locationId: null, teamId: null, managerId: null, startDate: new Date().toISOString().slice(0, 10), endDate: null, portalRoles: null, version: null,
};

export function EmployeeForm({ id }: { id: number | null }) {
  const app = useApp();
  const lookups = useLoad(() => app.api.lookups(), []);
  const existing = useLoad(() => (id ? app.api.employee(id) : Promise.resolve(null as EmployeeDetail | null)), [id]);
  const [form, setForm] = useState<EmployeeInput | null>(id ? null : blank);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  if (id && existing.data && !form) {
    const e = existing.data;
    setForm({
      employeeNumber: e.employeeNumber, firstName: e.firstName, lastName: e.lastName, email: e.email, jobTitle: e.jobTitle, type: e.type,
      externalCompany: e.externalCompany, nationalityCode: e.nationalityCode, locationId: e.locationId, teamId: e.teamId, managerId: e.manager?.id ?? null,
      startDate: e.startDate, endDate: e.endDate, portalRoles: e.portalRoles, version: e.version,
    });
  }
  if (!app.has('Hr')) return <div className="page"><Failure error={new ApiError(403, 'forbidden', 'Not allowed', 'Only HR can register and edit employees.')} /></div>;
  if (!form || !lookups.data) return <div className="page"><Loading /></div>;
  const set = <K extends keyof EmployeeInput>(k: K, v: EmployeeInput[K]) => setForm({ ...form, [k]: v });
  const err = (k: string) => errors[k] ?? errors[k.charAt(0).toUpperCase() + k.slice(1)];

  const submit = async (ev: FormEvent) => {
    ev.preventDefault();
    setBusy(true);
    setErrors({});
    try {
      const saved = id ? await app.api.updateEmployee(id, form) : await app.api.createEmployee(form);
      app.toast(id ? 'Changes saved.' : `${saved.firstName} ${saved.lastName} is registered${saved.status === 'Preboarding' ? ' and starts in preboarding' : ''}.`);
      app.changed();
      app.navigate(`/employees/${saved.id}`);
    } catch (e) {
      const fe = (e as { errors?: Record<string, string[]> }).errors ?? {};
      setErrors(Object.fromEntries(Object.entries(fe).map(([k, v]) => [k, v.join(' ')])));
      app.fail(e);
    } finally { setBusy(false); }
  };
  const L = lookups.data;
  return (
    <div className="page narrow">
      <PageHead title={id ? 'Edit employee' : 'Register a new employee'} intro={id ? undefined : 'New joiners get the mandatory trainings automatically. A start date in the future means preboarding until then.'} />
      <form className="form" onSubmit={submit}>
        <div className="row">
          <Field label="First name" error={err('firstName')}><input required value={form.firstName} onChange={(e) => set('firstName', e.target.value)} /></Field>
          <Field label="Last name" error={err('lastName')}><input required value={form.lastName} onChange={(e) => set('lastName', e.target.value)} /></Field>
        </div>
        <div className="row">
          <Field label="Employee number" hint="E for internal, X for external, e.g. E10123" error={err('employeeNumber')}>
            <input required value={form.employeeNumber} disabled={!!id} onChange={(e) => set('employeeNumber', e.target.value)} />
          </Field>
          <Field label="E-mail" error={err('email')}><input required type="email" value={form.email} onChange={(e) => set('email', e.target.value)} /></Field>
        </div>
        <Field label="Job title" error={err('jobTitle')}><input required value={form.jobTitle} onChange={(e) => set('jobTitle', e.target.value)} /></Field>
        <div className="row">
          <Field label="Employment">
            <select value={form.type} onChange={(e) => set('type', e.target.value)}>
              <option value="Internal">Internal employee</option>
              <option value="External">External (contractor)</option>
            </select>
          </Field>
          {form.type === 'External' && (
            <Field label="Company" error={err('externalCompany')}><input required value={form.externalCompany ?? ''} onChange={(e) => set('externalCompany', e.target.value)} /></Field>
          )}
        </div>
        <div className="row">
          <Field label="Team">
            <select value={form.teamId ?? ''} onChange={(e) => {
              const teamId = e.target.value ? Number(e.target.value) : null;
              const lead = L.departments.flatMap((d) => d.teams).find((t) => t.id === teamId)?.lead?.id ?? null;
              setForm({ ...form, teamId, managerId: form.managerId ?? lead });
            }}>
              <option value="">No team</option>
              {L.departments.map((d) => <optgroup key={d.id} label={d.name}>{d.teams.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</optgroup>)}
            </select>
          </Field>
          <Field label="Manager" hint={form.type === 'External' ? 'Required: the sponsoring manager' : 'Defaults to the team lead'} error={err('managerId')}>
            <select value={form.managerId ?? ''} onChange={(e) => set('managerId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">No manager</option>
              {L.managers.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
            </select>
          </Field>
        </div>
        <div className="row">
          <Field label="Location">
            <select value={form.locationId ?? ''} onChange={(e) => set('locationId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">–</option>
              {L.locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
          <Field label="Nationality" error={err('nationalityCode')}>
            <select value={form.nationalityCode ?? ''} onChange={(e) => set('nationalityCode', e.target.value || null)}>
              <option value="">–</option>
              {L.countries.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
            </select>
          </Field>
        </div>
        <div className="row">
          <Field label="Start date" error={err('startDate')}><input required type="date" value={form.startDate} onChange={(e) => set('startDate', e.target.value)} /></Field>
          <Field label="End date" hint={form.type === 'External' ? 'Required: contract end' : 'Only for fixed-term contracts'} error={err('endDate')}>
            <input type="date" value={form.endDate ?? ''} onChange={(e) => set('endDate', e.target.value || null)} />
          </Field>
        </div>
        {app.has('Admin') && (
          <fieldset className="checks">
            <legend>Portal roles</legend>
            {['Manager', 'Hr', 'SecurityOfficer', 'Admin'].map((r) => (
              <label key={r}>
                <input type="checkbox" checked={!!form.portalRoles?.includes(r)} onChange={(e) => set('portalRoles', e.target.checked ? [...(form.portalRoles ?? []), r] : (form.portalRoles ?? []).filter((x) => x !== r))} />
                {human(r)}
              </label>
            ))}
          </fieldset>
        )}
        <div className="form-actions">
          <a className="btn" href={id ? `#/employees/${id}` : '#/employees'}>Cancel</a>
          <button type="submit" className="btn primary" disabled={busy}>{id ? 'Save changes' : 'Register employee'}</button>
        </div>
      </form>
    </div>
  );
}
