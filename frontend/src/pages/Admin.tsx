import { FormEvent, useState } from 'react';
import { ImportReport, JobReport } from '../api/types';
import { useApp, useLoad } from '../app';
import { download, Empty, Failure, Field, fmtDate, Loading, PageHead } from '../components/ui';

export function Exports() {
  const app = useApp();
  const list = useLoad(() => app.api.exports(), []);
  const [busy, setBusy] = useState<string | null>(null);
  const get = async (key: string, format: 'xlsx' | 'csv') => {
    setBusy(key + format);
    try {
      const f = await app.api.download(key, format);
      download(f.fileName, f.contentType, f.bytes);
    } catch (e) { app.fail(e); } finally { setBusy(null); }
  };
  return (
    <div className="page">
      <PageHead title="Export lists" intro="Formatted lists straight from the database: Excel with a frozen, filterable header, or CSV for other tools. You see the lists your roles allow." />
      {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.length === 0 ? <Empty title="No exports for your roles" /> : (
        <ul className="exports">
          {list.data.map((x) => (
            <li key={x.key}>
              <div>
                <h2>{x.title}</h2>
                <p>{x.description}</p>
              </div>
              <div className="export-actions">
                <button type="button" className="btn primary" disabled={busy !== null} onClick={() => get(x.key, 'xlsx')}>Download Excel</button>
                <button type="button" className="btn" disabled={busy !== null} onClick={() => get(x.key, 'csv')}>CSV</button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

const SAMPLE_CSV = `employeeNumber;firstName;lastName;email;jobTitle;team;location;country;managerNumber;type
E30001;Lilla;Bodnár;lilla.bodnar@aegis.example;Data Analyst;Sales Analytics;Budapest;HU;E10010;internal
E30002;Tamás;Vass;tamas.vass@aegis.example;Platform Engineer;Platform;Debrecen;HU;E10005;internal
X30003;Erik;Lund;erik.lund@partner.example;Consultant;Platform;Remote;;E10005;external
`;

export function Import() {
  const app = useApp();
  const [fileName, setFileName] = useState('people.csv');
  const [content, setContent] = useState('');
  const [report, setReport] = useState<ImportReport | null>(null);
  const [busy, setBusy] = useState(false);
  const run = async (dryRun: boolean) => {
    setBusy(true);
    try {
      const r = await app.api.importFile(fileName, content, dryRun);
      setReport(r);
      if (!dryRun) { app.toast(`Imported: ${r.created} new, ${r.updated} updated.`); app.changed(); }
    } catch (e) { app.fail(e); } finally { setBusy(false); }
  };
  const pick = async (e: FormEvent<HTMLInputElement>) => {
    const f = (e.currentTarget as HTMLInputElement).files?.[0];
    if (!f) return;
    setFileName(f.name);
    setContent(await f.text());
    setReport(null);
  };
  return (
    <div className="page">
      <PageHead title="Import from the directory" intro="Synchronise employees from an LDAP export (LDIF) or an HR system's CSV file. Preview first: nothing is saved until you apply it. People missing from the directory are only reported - leaving is HR's decision." />
      <div className="import-grid">
        <section className="card">
          <Field label="File" hint="LDIF (inetOrgPerson) or CSV with a header row"><input type="file" accept=".ldif,.csv,.txt" onChange={pick} /></Field>
          <Field label="Content"><textarea rows={10} className="code-area" value={content} onChange={(e) => { setContent(e.target.value); setReport(null); }} spellCheck={false} /></Field>
          <div className="form-actions start">
            <button type="button" className="btn" onClick={() => { setFileName('people.csv'); setContent(SAMPLE_CSV); setReport(null); }}>Use a sample file</button>
            <button type="button" className="btn primary" disabled={!content || busy} onClick={() => run(true)}>Preview changes</button>
            <button type="button" className="btn" disabled={!report?.dryRun || busy} onClick={() => run(false)}>Apply import</button>
          </div>
        </section>
        <section className="card">
          <h2>{report ? (report.dryRun ? 'Preview' : 'Imported') : 'Changes'}</h2>
          {!report ? <p className="muted">Load a file and preview it to see what would change.</p> : (
            <>
              <p className="summary">{report.read} records read: <strong>{report.created}</strong> new, <strong>{report.updated}</strong> changed, {report.unchanged} unchanged.</p>
              {report.changes.length > 0 && (
                <table className="grid compact">
                  <thead><tr><th>Number</th><th>Name</th><th>Change</th></tr></thead>
                  <tbody>{report.changes.map((c) => <tr key={c.employeeNumber}><td className="num">{c.employeeNumber}</td><td>{c.name}</td><td><span className={`tag ${c.kind}`}>{c.kind}</span> {c.details}</td></tr>)}</tbody>
                </table>
              )}
              {report.warnings.length > 0 && <><h3>Check these</h3><ul className="warnings">{report.warnings.map((w) => <li key={w}>{w}</li>)}</ul></>}
            </>
          )}
        </section>
      </div>
    </div>
  );
}

export function AdminTools() {
  const app = useApp();
  const news = useLoad(() => app.api.news(20), []);
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [pinned, setPinned] = useState(false);
  const [jobs, setJobs] = useState<JobReport | null>(null);
  const publish = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await app.api.publishNews(title, body, pinned);
      app.toast('Published.');
      setTitle(''); setBody(''); setPinned(false);
      app.changed();
    } catch (err) { app.fail(err); }
  };
  const runJobs = async () => {
    try { setJobs(await app.api.runJobs()); app.changed(); } catch (e) { app.fail(e); }
  };
  return (
    <div className="page">
      <PageHead title="News, jobs and demo tools" />
      <div className="detail-grid">
        {app.has('Hr') && (
          <section className="card">
            <h2>Publish news</h2>
            <form className="form" onSubmit={publish}>
              <Field label="Title"><input required value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
              <Field label="Text" hint="Blank lines separate paragraphs"><textarea required rows={5} value={body} onChange={(e) => setBody(e.target.value)} /></Field>
              <label className="check"><input type="checkbox" checked={pinned} onChange={(e) => setPinned(e.target.checked)} /> Pin to the top</label>
              <div className="form-actions start"><button type="submit" className="btn primary">Publish</button></div>
            </form>
            <h3>Published</h3>
            <ul className="plain">
              {news.data?.map((n) => (
                <li key={n.id}>
                  {n.title} <span className="sub">{fmtDate(n.publishedAt)}</span>
                  <button type="button" className="btn small danger" onClick={async () => { try { await app.api.deleteNews(n.id); app.changed(); } catch (e) { app.fail(e); } }}>Delete</button>
                </li>
              ))}
            </ul>
          </section>
        )}
        {app.has('Admin') && (
          <section className="card">
            <h2>Daily jobs</h2>
            <p>The jobs move time-driven states on: joiners become active on their first day, leavers are offboarded and lose their access, access and trainings expire, resolved tickets close. They run every hour; run them now to see the effect.</p>
            <button type="button" className="btn primary" onClick={runJobs}>Run the daily jobs</button>
            {jobs && (
              <dl className="facts">
                <div><dt>Joiners activated</dt><dd>{jobs.activated}</dd></div>
                <div><dt>Leavers offboarded</dt><dd>{jobs.left}</dd></div>
                <div><dt>Access expired</dt><dd>{jobs.accessExpired}</dd></div>
                <div><dt>Access provisioned</dt><dd>{jobs.provisioned}</dd></div>
                <div><dt>Trainings overdue</dt><dd>{jobs.trainingsOverdue}</dd></div>
                <div><dt>Trainings expired</dt><dd>{jobs.trainingsExpired}</dd></div>
                <div><dt>Tickets closed</dt><dd>{jobs.ticketsClosed}</dd></div>
              </dl>
            )}
            {app.demo && (
              <>
                <h3>Demo tools</h3>
                <p>Move the demo clock forward and run the jobs to see joiners start, the leaver go and trainings fall due.</p>
                <div className="form-actions start">
                  <button type="button" className="btn" onClick={async () => { app.demo!.travel(30); await runJobs(); app.toast('The clock moved 30 days ahead.'); }}>Jump 30 days ahead</button>
                  <button type="button" className="btn danger" onClick={() => { app.demo!.reset(); setJobs(null); app.changed(); app.toast('Demo data reset.'); }}>Reset demo data</button>
                </div>
              </>
            )}
          </section>
        )}
      </div>
    </div>
  );
}
