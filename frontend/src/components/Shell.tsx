import { ReactNode, useEffect, useRef, useState } from 'react';
import { useApp } from '../app';

interface NavItem { to: string; label: string; count?: number; urgent?: boolean; show?: boolean }

/** The portal frame: the band with the shield, the menu with live counters, and the "view as" switcher. */
export function Shell({ path, children }: { path: string; children: ReactNode }) {
  const app = useApp();
  const c = app.counters;
  const staff = app.has('Hr') || app.has('SecurityOfficer');
  const agent = app.has('Hr');
  const [open, setOpen] = useState<string | null>(null);
  const [mobile, setMobile] = useState(false);
  useEffect(() => { setOpen(null); setMobile(false); }, [path]);

  const work: NavItem[] = [
    { to: '/', label: 'Home' },
    { to: '/requests', label: 'My requests', count: c?.myOpenRequests },
    { to: '/approvals', label: 'Approvals', count: c?.awaitingMyApproval, urgent: true },
    { to: '/trainings', label: 'Trainings', count: c?.myTrainingsDue },
    { to: '/tickets', label: 'Tickets', count: agent ? c?.ticketQueue : c?.myOpenTickets },
  ];
  const groups: { id: string; label: string; items: NavItem[] }[] = [
    { id: 'directory', label: 'Directory', items: [
      { to: '/employees', label: 'Employees' },
      { to: '/applications', label: 'Applications and roles' },
      { to: '/organisation', label: 'Teams, locations, nationalities' },
    ] },
    { id: 'admin', label: 'Administration', items: [
      { to: '/exports', label: 'Export lists', show: staff },
      { to: '/import', label: 'Import from directory', show: app.has('Hr') },
      { to: '/admin', label: 'News, jobs and demo tools', show: app.has('Hr') || app.has('Admin') },
    ] },
  ];
  const active = (to: string) => (to === '/' ? path === '/' : path === to || path.startsWith(to + '/'));

  return (
    <>
      <a className="skip" href="#main" onClick={(e) => { e.preventDefault(); document.getElementById('main')?.focus(); }}>Skip to content</a>
      <header className="band">
        <div className="band-inner">
          <a className="brand" href="#/" aria-label="Aegis home">
            <svg viewBox="0 0 64 64" width="34" height="34" aria-hidden="true">
              <path d="M32 4 8 13v17c0 15 10 26 24 30 14-4 24-15 24-30V13z" fill="#0d1730" />
              <path d="M32 10 14 17v13c0 11 7 20 18 24 11-4 18-13 18-24V17z" fill="none" stroke="currentColor" strokeWidth="3" />
              <path d="M23 33l6 6 13-14" fill="none" stroke="currentColor" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            <span>Aegis</span>
          </a>
          <button type="button" className="menu-toggle" aria-expanded={mobile} onClick={() => setMobile(!mobile)}>Menu</button>
          <nav className={`nav${mobile ? ' open' : ''}`} aria-label="Main">
            {work.map((i) => (
              <a key={i.to} href={`#${i.to}`} className={active(i.to) ? 'on' : ''} aria-current={active(i.to) ? 'page' : undefined}>
                {i.label}
                {i.count ? <span className={`badge${i.urgent ? ' urgent' : ''}`} aria-label={`${i.count} open`}>{i.count}</span> : null}
              </a>
            ))}
            {groups.map((g) => {
              const items = g.items.filter((i) => i.show !== false);
              if (!items.length) return null;
              return (
                <Menu key={g.id} label={g.label} open={open === g.id} onToggle={() => setOpen(open === g.id ? null : g.id)} on={items.some((i) => active(i.to))}>
                  {items.map((i) => <a key={i.to} href={`#${i.to}`} className={active(i.to) ? 'on' : ''}>{i.label}</a>)}
                </Menu>
              );
            })}
          </nav>
          <Persona />
        </div>
      </header>
      {app.demo && <DemoNote />}
      <main id="main" tabIndex={-1}>{children}</main>
    </>
  );
}

function Menu({ label, open, on, onToggle, children }: { label: string; open: boolean; on: boolean; onToggle(): void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) onToggle(); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onToggle(); };
    document.addEventListener('mousedown', close);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', esc); };
  }, [open, onToggle]);
  return (
    <div className="menu" ref={ref}>
      <button type="button" className={on ? 'on' : ''} aria-expanded={open} onClick={onToggle}>{label}</button>
      {open && <div className="menu-pop">{children}</div>}
    </div>
  );
}

/** "Viewing as": the demo sign-in. Pick a persona to see the portal through their roles. */
function Persona() {
  const app = useApp();
  return (
    <label className="persona">
      <span className="persona-label">Viewing as</span>
      <select value={app.user ?? ''} onChange={(e) => app.setUser(e.target.value)} aria-label="View the portal as">
        {app.personas.map((p) => (
          <option key={p.number} value={p.number}>
            {p.role}{app.user === p.number && app.me ? ` – ${app.me.name}` : ''}
          </option>
        ))}
        {app.user && !app.personas.some((p) => p.number === app.user) && <option value={app.user}>{app.me?.name ?? app.user}</option>}
      </select>
    </label>
  );
}

function DemoNote() {
  const app = useApp();
  const offset = app.demo?.dayOffset ?? 0;
  return (
    <div className="demo-note">
      <p>
        Demo: the API runs in your browser with a fictional company, and changes stay on this device.
        {offset > 0 && <> The clock is {offset} days ahead.</>}
        {' '}Switch persona in the top right to see approvals, HR and admin screens.
      </p>
    </div>
  );
}
