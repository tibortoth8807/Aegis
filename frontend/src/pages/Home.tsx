import { useApp, useLoad } from '../app';
import { Empty, fmtDate, human, Loading, PageHead } from '../components/ui';

/** Start page: what needs the user's attention, then the news. */
export function Home() {
  const app = useApp();
  const c = app.counters;
  const news = useLoad(() => app.api.news(6), []);
  const first = app.me?.name.split(' ')[0];
  const staff = app.has('Hr') || app.has('SecurityOfficer');

  const todo = c ? [
    { n: c.awaitingMyApproval, text: c.awaitingMyApproval === 1 ? 'request waits for your approval' : 'requests wait for your approval', to: '/approvals', urgent: true },
    { n: c.myTrainingsDue, text: c.myTrainingsDue === 1 ? 'training to complete' : 'trainings to complete', to: '/trainings' },
    { n: c.myOpenRequests, text: c.myOpenRequests === 1 ? 'of your requests is in progress' : 'of your requests are in progress', to: '/requests' },
    { n: c.myOpenTickets, text: c.myOpenTickets === 1 ? 'open ticket you reported' : 'open tickets you reported', to: '/tickets' },
    { n: c.teamTrainingsOverdue, text: 'overdue trainings in your team', to: '/trainings?scope=team', urgent: true },
    { n: c.ticketQueue, text: 'tickets in the support queue', to: '/tickets' },
  ].filter((x) => x.n > 0) : [];

  return (
    <div className="page home">
      <PageHead
        title={first ? `Good to see you, ${first}` : 'Aegis'}
        intro={app.me ? `${app.me.jobTitle}. ${todo.length ? 'This is what needs your attention.' : 'Nothing needs your attention right now.'}` : undefined}
      />
      <div className="home-grid">
        <section aria-labelledby="todo-h">
          <h2 id="todo-h">Your work</h2>
          {!c ? <Loading /> : todo.length === 0 ? (
            <Empty title="All clear">No approvals, trainings or tickets are waiting for you.</Empty>
          ) : (
            <ul className="todo">
              {todo.map((t) => (
                <li key={t.to + t.text}>
                  <a href={`#${t.to}`} className={t.urgent ? 'urgent' : ''}>
                    <span className="n">{t.n}</span>
                    <span>{t.text}</span>
                  </a>
                </li>
              ))}
            </ul>
          )}
          <div className="quick">
            <a className="btn primary" href="#/requests/new">Request access</a>
            <a className="btn" href="#/tickets/new">Report a problem</a>
            <a className="btn" href={`#/employees/${app.me?.id ?? ''}`}>My profile</a>
          </div>

          {staff && c && (
            <>
              <h2>Company at a glance</h2>
              <dl className="glance">
                <div><dt>Employees</dt><dd>{c.activeEmployees}</dd></div>
                <div><dt>Joining soon</dt><dd><a href="#/employees?status=Preboarding">{c.joiners}</a></dd></div>
                <div><dt>Leaving</dt><dd><a href="#/employees?status=Offboarding">{c.leavers}</a></dd></div>
                <div><dt>Externals</dt><dd><a href="#/employees?type=External">{c.externals}</a></dd></div>
                <div><dt>Active access grants</dt><dd>{c.activeGrants}</dd></div>
                <div className={c.privilegedExpiringSoon ? 'warn' : ''}><dt>Privileged access ending in 14 days</dt><dd>{c.privilegedExpiringSoon}</dd></div>
              </dl>
            </>
          )}
        </section>

        <section aria-labelledby="news-h" className="news">
          <h2 id="news-h">News</h2>
          {news.loading && !news.data ? <Loading /> : news.data?.length === 0 ? <Empty title="No news yet" /> : (
            news.data?.map((n) => (
              <article key={n.id} className={n.pinned ? 'pinned' : ''}>
                <h3>{n.title}</h3>
                <p className="meta">{n.pinned ? 'Pinned, ' : ''}{fmtDate(n.publishedAt)}, {n.author}</p>
                {n.body.split(/\n\n+/).map((para, i) => <p key={i}>{para}</p>)}
              </article>
            ))
          )}
          {app.me && <p className="roles-note">Your roles in the portal: {app.me.roles.map(human).join(', ')}.</p>}
        </section>
      </div>
    </div>
  );
}
