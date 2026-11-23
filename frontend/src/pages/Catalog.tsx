import { FormEvent, useState } from 'react';
import { useApp, useLoad } from '../app';
import { Empty, Failure, Field, Loading, PageHead, Person, Tabs, useDebounced } from '../components/ui';

export function Applications() {
  const app = useApp();
  const [search, setSearch] = useState('');
  const q = useDebounced(search);
  const list = useLoad(() => app.api.applications(q), [q]);
  return (
    <div className="page">
      <PageHead title="Applications and roles" intro="Every application whose access is managed here, who owns it, and the roles you can request." actions={<a className="btn primary" href="#/requests/new">Request access</a>} />
      <div className="filters"><Field label="Search"><input type="search" value={search} placeholder="Application or role" onChange={(e) => setSearch(e.target.value)} /></Field></div>
      {list.error ? <Failure error={list.error} /> : !list.data ? <Loading /> : list.data.length === 0 ? <Empty title="No application matches" /> : (
        <div className="systems">
          {list.data.map((s) => (
            <section key={s.id} className="system">
              <header>
                <h2>{s.name} <span className="code">{s.code}</span></h2>
                <p>{s.description}</p>
                <p className="meta">Owner: <Person p={s.owner} />. Provisioning: {({ manual: 'by hand', 'ldap-group': 'LDAP group, automatic', api: 'API, automatic' } as Record<string, string>)[s.provisioning] ?? s.provisioning}.
                  {s.requiredTraining && <> Requires the training “{s.requiredTraining}”.</>}</p>
              </header>
              <table className="grid compact">
                <thead><tr><th>Role</th><th>What it allows</th><th className="num">Valid for</th><th className="num">People</th></tr></thead>
                <tbody>
                  {s.roles.map((r) => (
                    <tr key={r.id}>
                      <td>{r.name}{r.privileged && <span className="tag priv">privileged</span>}</td>
                      <td>{r.description}{r.provisioningGroup && <div className="sub">group {r.provisioningGroup}</div>}</td>
                      <td className="num">{r.privileged ? Math.min(r.validityDays, 30) : r.validityDays} days</td>
                      <td className="num">{r.activeGrants}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

/** Reference data: departments and teams, locations, the nationalities list. */
export function Organisation() {
  const app = useApp();
  const [tab, setTab] = useState('teams');
  const data = useLoad(() => app.api.lookups(), []);
  const hr = app.has('Hr');
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [eea, setEea] = useState(false);
  const [teamName, setTeamName] = useState('');
  const [teamDept, setTeamDept] = useState('');
  const [teamLead, setTeamLead] = useState('');

  const saveCountry = async (e: FormEvent) => {
    e.preventDefault();
    try {
      const c = await app.api.saveCountry(code, name, eea);
      app.toast(`${c.name} saved.`);
      setCode(''); setName(''); setEea(false);
      app.changed();
    } catch (err) { app.fail(err); }
  };
  const removeCountry = async (c: string) => {
    try { await app.api.deleteCountry(c); app.toast('Deleted.'); app.changed(); } catch (err) { app.fail(err); }
  };
  const addTeam = async (e: FormEvent) => {
    e.preventDefault();
    try {
      const t = await app.api.saveTeam(null, teamName, Number(teamDept), teamLead ? Number(teamLead) : null);
      app.toast(`Team ${t.name} added to ${t.department}.`);
      setTeamName('');
      app.changed();
    } catch (err) { app.fail(err); }
  };

  if (data.error) return <div className="page"><Failure error={data.error} /></div>;
  if (!data.data) return <div className="page"><Loading /></div>;
  const L = data.data;
  return (
    <div className="page">
      <PageHead title="Teams, locations, nationalities" intro="The reference data the employee records use." />
      <Tabs value={tab} onChange={setTab} options={[['teams', 'Departments and teams'], ['locations', 'Locations'], ['countries', 'Nationalities']]} />
      {tab === 'teams' && (
        <>
          <div className="departments">
            {L.departments.map((d) => (
              <section key={d.id} className="department">
                <h2>{d.name} <span className="code">{d.code}</span></h2>
                <ul>
                  {d.teams.map((t) => (
                    <li key={t.id}>
                      <a href={`#/employees?team=${t.id}`}>{t.name}</a>
                      <span className="sub">{t.members} {t.members === 1 ? 'person' : 'people'}{t.lead && <>, led by <Person p={t.lead} /></>}</span>
                    </li>
                  ))}
                </ul>
              </section>
            ))}
          </div>
          {hr && (
            <form className="inline-form" onSubmit={addTeam}>
              <h2>Add a team</h2>
              <Field label="Name"><input required value={teamName} onChange={(e) => setTeamName(e.target.value)} /></Field>
              <Field label="Department">
                <select required value={teamDept} onChange={(e) => setTeamDept(e.target.value)}>
                  <option value="">Choose</option>
                  {L.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </Field>
              <Field label="Lead">
                <select value={teamLead} onChange={(e) => setTeamLead(e.target.value)}>
                  <option value="">None yet</option>
                  {L.managers.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
                </select>
              </Field>
              <button type="submit" className="btn primary">Add team</button>
            </form>
          )}
        </>
      )}
      {tab === 'locations' && (
        <table className="grid">
          <thead><tr><th>Location</th><th>Country</th><th className="num">People</th></tr></thead>
          <tbody>{L.locations.map((l) => <tr key={l.id}><td><a href={`#/employees?location=${l.id}`}>{l.name}</a></td><td>{l.countryCode || '–'}</td><td className="num">{l.employees}</td></tr>)}</tbody>
        </table>
      )}
      {tab === 'countries' && (
        <>
          <table className="grid">
            <thead><tr><th>Code</th><th>Country</th><th>EU / EEA</th><th className="num">People</th>{hr && <th><span className="sr">Actions</span></th>}</tr></thead>
            <tbody>
              {L.countries.map((c) => (
                <tr key={c.code}>
                  <td className="num">{c.code}</td><td>{c.name}</td><td>{c.eea ? 'yes' : 'no'}</td><td className="num">{c.employees}</td>
                  {hr && <td className="row-actions">
                    <button type="button" className="btn small" onClick={() => { setCode(c.code); setName(c.name); setEea(c.eea); }}>Edit</button>
                    <button type="button" className="btn small danger" disabled={c.employees > 0} title={c.employees > 0 ? 'In use by employees' : undefined} onClick={() => removeCountry(c.code)}>Delete</button>
                  </td>}
                </tr>
              ))}
            </tbody>
          </table>
          {hr && (
            <form className="inline-form" onSubmit={saveCountry}>
              <h2>Add or change a country</h2>
              <Field label="ISO code"><input required maxLength={2} size={3} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} /></Field>
              <Field label="Name"><input required value={name} onChange={(e) => setName(e.target.value)} /></Field>
              <label className="check"><input type="checkbox" checked={eea} onChange={(e) => setEea(e.target.checked)} /> EU / EEA member</label>
              <button type="submit" className="btn primary">Save country</button>
            </form>
          )}
        </>
      )}
    </div>
  );
}
