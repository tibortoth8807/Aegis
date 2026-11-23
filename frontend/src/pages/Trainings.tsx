import { FormEvent, useState } from 'react';
import { queryParam, useApp, useLoad } from '../app';
import { ActionBar, Empty, Failure, Field, fmtDate, human, Loading, PageHead, Pager, Person, Status, Tabs, useDebounced } from '../components/ui';

export function Trainings() {
  const app = useApp();
  const [tab, setTab] = useState(queryParam('scope') ?? 'mine');
  const [status, setStatus] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const q = useDebounced(search);
  const list = useLoad(() => (tab === 'courses' ? Promise.resolve(null) : app.api.assignments({ page, pageSize: 20, search: q }, tab, status)), [tab, status, q, page]);
  const courses = useLoad(() => app.api.courses(), []);

  const run = (id: number) => async (action: string, value: string | null) => {
    try {
      const a = await app.api.assignmentTransition(id, action, action === 'complete' ? Number(value) : null, action === 'complete' ? null : value);
      app.toast(action === 'complete' ? (a.status === 'Completed' ? `${a.course}: passed with ${a.score}%.` : `${a.score}% is below the pass mark - try again.`) : `${a.course}: ${human(a.status).toLowerCase()}.`);
      app.changed();
    } catch (e) { app.fail(e); }
  };

  return (
    <div className="page">
      <PageHead title="Trainings" intro="Mandatory courses are assigned on the first day; some applications need a valid completion before access is granted." />
      <Tabs value={tab} onChange={(v) => { setTab(v); setPage(1); }} options={[
        ['mine', 'My trainings', app.counters?.myTrainingsDue],
        ...(app.has('Manager') ? [['team', 'My team', app.counters?.teamTrainingsOverdue] as [string, string, number?]] : []),
        ...(app.has('Hr') ? [['all', 'Everyone'] as [string, string]] : []),
        ['courses', 'Courses'],
      ]} />
      {tab === 'courses' ? <Courses /> : (
        <>
          <div className="filters">
            {tab !== 'mine' && <Field label="Search"><input type="search" value={search} placeholder="Person or course" onChange={(e) => { setSearch(e.target.value); setPage(1); }} /></Field>}
            <Field label="Status">
              <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
                <option value="">Any status</option>
                {['Assigned', 'InProgress', 'Overdue', 'Completed', 'Expired', 'Waived'].map((s) => <option key={s} value={s}>{human(s)}</option>)}
              </select>
            </Field>
          </div>
          {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.items.length === 0 ? <Empty title="No trainings here" /> : (
            <>
              <div className="table-wrap">
                <table className="grid">
                  <thead><tr>{tab !== 'mine' && <th>Person</th>}<th>Course</th><th>Status</th><th>Due</th><th>Result</th><th>Valid until</th><th><span className="sr">Actions</span></th></tr></thead>
                  <tbody>
                    {list.data.items.map((a) => (
                      <tr key={a.id}>
                        {tab !== 'mine' && <td><Person p={a.employee} /></td>}
                        <td>{a.course}</td>
                        <td><Status value={a.status} /></td>
                        <td className="num">{fmtDate(a.dueOn)}</td>
                        <td className="num">{a.score === null ? '–' : `${a.score}%`}{a.attempts > 1 && <span className="sub"> {a.attempts} attempts</span>}</td>
                        <td className="num">{fmtDate(a.validUntil)}</td>
                        <td>
                          <ActionBar actions={a.allowedActions} onAction={run(a.id)} extra={{
                            complete: { title: `Result of ${a.course}`, label: 'Score in percent', kind: 'number', required: true, confirm: 'Record result' },
                          }} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <Pager page={page} size={20} total={list.data.total} onPage={setPage} />
            </>
          )}
        </>
      )}
      {courses.error && <Failure error={courses.error} />}
    </div>
  );
}

function Courses() {
  const app = useApp();
  const courses = useLoad(() => app.api.courses(), []);
  const team = useLoad(() => (app.me && app.has('Manager') ? app.api.employee(app.me.id) : Promise.resolve(null)), [app.me?.id]);
  const [course, setCourse] = useState('');
  const [who, setWho] = useState<number[]>([]);
  const assign = async (e: FormEvent) => {
    e.preventDefault();
    try {
      const n = await app.api.assign(Number(course), who);
      app.toast(n === 0 ? 'Everyone selected already has this course.' : `Assigned to ${n} ${n === 1 ? 'person' : 'people'}.`);
      setWho([]);
      app.changed();
    } catch (err) { app.fail(err); }
  };
  if (!courses.data) return <Loading />;
  const reports = team.data?.directReports ?? [];
  return (
    <>
      <table className="grid">
        <thead><tr><th>Course</th><th>Mandatory</th><th className="num">Pass mark</th><th className="num">Valid for</th><th className="num">Open</th><th className="num">Completed</th><th className="num">Overdue</th></tr></thead>
        <tbody>
          {courses.data.map((c) => (
            <tr key={c.id}>
              <td><strong>{c.title}</strong> <span className="code">{c.code}</span><div className="sub">{c.description}</div></td>
              <td>{c.mandatory ? 'yes' : 'no'}</td>
              <td className="num">{c.passScore ? `${c.passScore}%` : 'attendance'}</td>
              <td className="num">{c.validityMonths ? `${c.validityMonths} months` : 'no expiry'}</td>
              <td className="num">{c.assigned}</td><td className="num">{c.completed}</td><td className={`num${c.overdue ? ' bad' : ''}`}>{c.overdue}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {reports.length > 0 && (
        <form className="inline-form" onSubmit={assign}>
          <h2>Assign a course to your team</h2>
          <Field label="Course">
            <select required value={course} onChange={(e) => setCourse(e.target.value)}>
              <option value="">Choose</option>
              {courses.data.map((c) => <option key={c.id} value={c.id}>{c.title}</option>)}
            </select>
          </Field>
          <fieldset className="checks">
            <legend>People</legend>
            {reports.map((p) => (
              <label key={p.id}><input type="checkbox" checked={who.includes(p.id)} onChange={(e) => setWho(e.target.checked ? [...who, p.id] : who.filter((x) => x !== p.id))} /> {p.name}</label>
            ))}
          </fieldset>
          <button type="submit" className="btn primary" disabled={!course || who.length === 0}>Assign course</button>
        </form>
      )}
    </>
  );
}
