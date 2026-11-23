/**
 * Generates seed/demo-data.json: the fictional company used by the API's seeder (C#) and by the browser demo (TS).
 * Deterministic (seeded PRNG), so the file only changes when this script changes.
 * Run: npx tsx tools/generate-demo-data.ts
 */
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

let seed = 20261123;
const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 2 ** 32);
const pick = <T>(xs: readonly T[]): T => xs[Math.floor(rnd() * xs.length)];
const int = (a: number, b: number) => a + Math.floor(rnd() * (b - a + 1));

const countries = [
  ['HU', 'Hungary', true], ['AT', 'Austria', true], ['DE', 'Germany', true], ['SK', 'Slovakia', true], ['RO', 'Romania', true],
  ['PL', 'Poland', true], ['PT', 'Portugal', true], ['ES', 'Spain', true], ['IT', 'Italy', true], ['FR', 'France', true],
  ['NL', 'Netherlands', true], ['CZ', 'Czechia', true], ['HR', 'Croatia', true], ['RS', 'Serbia', false], ['UA', 'Ukraine', false],
  ['IN', 'India', false], ['GB', 'United Kingdom', false], ['US', 'United States', false], ['TR', 'Türkiye', false], ['BR', 'Brazil', false],
].map(([code, name, eea]) => ({ code, name, eea }));

const locations = [
  { name: 'Budapest', country: 'HU' }, { name: 'Debrecen', country: 'HU' }, { name: 'Vienna', country: 'AT' },
  { name: 'Berlin', country: 'DE' }, { name: 'Lisbon', country: 'PT' }, { name: 'Remote', country: '' },
];

const departments = [
  { code: 'EXE', name: 'Executive Office', teams: ['Leadership'] },
  { code: 'HR', name: 'People & Culture', teams: ['HR Operations', 'Talent Acquisition'] },
  { code: 'FIN', name: 'Finance', teams: ['Accounting', 'Controlling'] },
  { code: 'SAL', name: 'Sales', teams: ['Sales Central Europe', 'Sales Analytics', 'Customer Success'] },
  { code: 'ENG', name: 'Engineering', teams: ['Platform', 'Identity & Access', 'Web Applications', 'Data Engineering'] },
  { code: 'SEC', name: 'Information Security', teams: ['Security Operations'] },
  { code: 'ITS', name: 'IT Services', teams: ['Service Desk', 'Workplace IT'] },
];

const first = ['Anna', 'Bence', 'Csilla', 'Dániel', 'Eszter', 'Ferenc', 'Gabriella', 'Gergő', 'Hanna', 'István', 'Judit', 'Katalin', 'László',
  'Márton', 'Nóra', 'Orsolya', 'Péter', 'Réka', 'Sándor', 'Tímea', 'Viktor', 'Zsófia', 'Lukas', 'Sophie', 'Maximilian', 'Lena', 'Jonas',
  'Mia', 'Tiago', 'Inês', 'Rafael', 'Beatriz', 'Marek', 'Zuzana', 'Andrei', 'Ioana', 'Priya', 'Arjun', 'Olena', 'Emre'];
const last = ['Kovács', 'Szabó', 'Tóth', 'Varga', 'Horváth', 'Kiss', 'Molnár', 'Németh', 'Farkas', 'Balogh', 'Papp', 'Takács', 'Juhász',
  'Lakatos', 'Mészáros', 'Oláh', 'Simon', 'Rácz', 'Fekete', 'Szilágyi', 'Gruber', 'Huber', 'Wagner', 'Müller', 'Schmidt', 'Becker',
  'Silva', 'Santos', 'Ferreira', 'Novák', 'Popescu', 'Ionescu', 'Sharma', 'Patel', 'Shevchenko', 'Yılmaz'];
const nationalityByName: Record<string, string[]> = {
  Lukas: ['AT', 'DE'], Sophie: ['AT', 'DE'], Maximilian: ['DE'], Lena: ['DE', 'AT'], Jonas: ['DE'], Mia: ['AT'],
  Tiago: ['PT', 'BR'], Inês: ['PT'], Rafael: ['PT', 'ES'], Beatriz: ['PT', 'BR'], Marek: ['SK', 'CZ', 'PL'], Zuzana: ['SK', 'CZ'],
  Andrei: ['RO'], Ioana: ['RO'], Priya: ['IN'], Arjun: ['IN'], Olena: ['UA'], Emre: ['TR'],
};
const lastFor = (f: string) => {
  const n = nationalityByName[f]?.[0];
  if (!n) return pick(last.slice(0, 20));
  const pool: Record<string, string[]> = { AT: ['Gruber', 'Huber', 'Wagner'], DE: ['Müller', 'Schmidt', 'Becker', 'Wagner'], PT: ['Silva', 'Santos', 'Ferreira'],
    BR: ['Silva', 'Santos'], ES: ['Santos'], SK: ['Novák'], CZ: ['Novák'], PL: ['Novák'], RO: ['Popescu', 'Ionescu'], IN: ['Sharma', 'Patel'],
    UA: ['Shevchenko'], TR: ['Yılmaz'] };
  return pick(pool[n] ?? last);
};
const ascii = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/ı/g, 'i').toLowerCase();

type Emp = {
  number: string; first: string; last: string; title: string; type: 'Internal' | 'External'; company?: string; nationality: string;
  location: string; team: string; manager?: string; startDaysAgo: number; endInDays?: number; roles: string[]; status?: string;
};
const employees: Emp[] = [];
let n = 10001;
const used = new Set<string>();
const person = (team: string, title: string, manager: string | undefined, roles: string[], extra: Partial<Emp> = {}): Emp => {
  let f = '', l = '';
  do { f = pick(first); l = lastFor(f); } while (used.has(f + l));
  used.add(f + l);
  const nat = nationalityByName[f] ? pick(nationalityByName[f]) : (rnd() < 0.9 ? 'HU' : pick(['RS', 'SK', 'RO', 'UA']));
  const loc = nat === 'HU' || ['RS', 'SK', 'RO', 'UA'].includes(nat) ? (rnd() < 0.8 ? 'Budapest' : 'Debrecen')
    : nat === 'AT' ? 'Vienna' : nat === 'DE' ? 'Berlin' : ['PT', 'BR', 'ES'].includes(nat) ? 'Lisbon' : 'Remote';
  const e: Emp = { number: `E${n++}`, first: f, last: l, title, type: 'Internal', nationality: nat, location: loc, team, manager,
    startDaysAgo: int(60, 3200), roles: ['Employee', ...roles], ...extra };
  employees.push(e);
  return e;
};

// leadership and fixed demo personas (the demo user switcher uses these)
const ceo = person('Leadership', 'Chief Executive Officer', undefined, ['Manager']);
const hrLead = person('HR Operations', 'Head of People & Culture', ceo.number, ['Manager', 'Hr']);
const finLead = person('Accounting', 'Chief Financial Officer', ceo.number, ['Manager']);
const salesLead = person('Sales Central Europe', 'Head of Sales', ceo.number, ['Manager']);
const cto = person('Platform', 'Chief Technology Officer', ceo.number, ['Manager']);
const ciso = person('Security Operations', 'Chief Information Security Officer', ceo.number, ['Manager', 'SecurityOfficer']);
const itLead = person('Service Desk', 'Head of IT Services', cto.number, ['Manager', 'Admin']);

const teamLeads: Record<string, Emp> = { Leadership: ceo, 'HR Operations': hrLead, Accounting: finLead, 'Sales Central Europe': salesLead,
  Platform: cto, 'Security Operations': ciso, 'Service Desk': itLead };
const leadSpecs: [string, string, Emp, string[]][] = [
  ['Talent Acquisition', 'Talent Acquisition Lead', hrLead, ['Manager', 'Hr']],
  ['Controlling', 'Head of Controlling', finLead, ['Manager']],
  ['Sales Analytics', 'Sales Analytics Manager', salesLead, ['Manager']],
  ['Customer Success', 'Customer Success Manager', salesLead, ['Manager']],
  ['Identity & Access', 'Engineering Manager, Identity & Access', cto, ['Manager']],
  ['Web Applications', 'Engineering Manager, Web Applications', cto, ['Manager']],
  ['Data Engineering', 'Engineering Manager, Data', cto, ['Manager']],
  ['Workplace IT', 'Workplace IT Lead', itLead, ['Manager', 'Admin']],
];
for (const [team, title, boss, roles] of leadSpecs) teamLeads[team] = person(team, title, boss.number, roles);

const titles: Record<string, string[]> = {
  'HR Operations': ['HR Business Partner', 'HR Administrator', 'Payroll Specialist'],
  'Talent Acquisition': ['Recruiter', 'Senior Recruiter'],
  Accounting: ['Accountant', 'Senior Accountant'], Controlling: ['Financial Controller', 'Business Analyst'],
  'Sales Central Europe': ['Account Manager', 'Senior Account Manager', 'Sales Representative'],
  'Sales Analytics': ['Sales Analyst', 'Senior Analyst'], 'Customer Success': ['Customer Success Specialist'],
  Platform: ['Senior .NET Developer', 'DevOps Engineer', 'Site Reliability Engineer'],
  'Identity & Access': ['Senior .NET Developer', 'IAM Engineer', 'QA Engineer'],
  'Web Applications': ['Frontend Developer', 'Full-stack Developer', 'UX Designer', 'Test Automation Engineer'],
  'Data Engineering': ['Data Engineer', 'BI Developer'],
  'Security Operations': ['Security Analyst', 'Security Engineer'],
  'Service Desk': ['Service Desk Analyst', 'IT Support Specialist'], 'Workplace IT': ['Systems Administrator', 'Endpoint Engineer'],
};
for (const [team, list] of Object.entries(titles)) {
  const size = team.startsWith('Sales') || ['Platform', 'Web Applications', 'Identity & Access'].includes(team) ? 5 : 3;
  for (let i = 0; i < size; i++) {
    const roles = team === 'Security Operations' ? ['SecurityOfficer'] : team === 'Service Desk' && i === 0 ? ['Admin'] : team === 'HR Operations' ? ['Hr'] : [];
    person(team, pick(list), teamLeads[team].number, roles);
  }
}
// externals (contractors), joiners, leavers, people on leave
const ext = (team: string, title: string, company: string) => {
  const e = person(team, title, teamLeads[team].number, [], { type: 'External', company, startDaysAgo: int(30, 300), endInDays: int(20, 200) });
  e.number = 'X' + e.number.slice(1);
};
ext('Web Applications', 'Contract Frontend Developer', 'Pixelforge Kft.');
ext('Platform', 'Cloud Consultant', 'Nimbus Partners GmbH');
ext('Data Engineering', 'Data Consultant', 'Quantia Lda.');
ext('Service Desk', 'Service Desk Agent', 'HelpFirst Kft.');
person('Identity & Access', 'Junior .NET Developer', teamLeads['Identity & Access'].number, [], { startDaysAgo: -10, status: 'Preboarding' });
person('Sales Analytics', 'Sales Analyst', teamLeads['Sales Analytics'].number, [], { startDaysAgo: -21, status: 'Preboarding' });
employees.filter((e) => e.team === 'Customer Success' && !e.roles.includes('Manager'))[0].status = 'Offboarding';
employees.filter((e) => e.team === 'Customer Success' && !e.roles.includes('Manager'))[0].endInDays = 12;
employees.filter((e) => e.team === 'Accounting' && !e.roles.includes('Manager'))[0].status = 'OnLeave';

const courses = [
  { code: 'DP-101', title: 'Data protection essentials (GDPR)', description: 'Personal data, lawful processing, data subject rights, incidents.', mandatory: true, dueDays: 30, validityMonths: 12, passScore: 80 },
  { code: 'IS-101', title: 'Information security awareness', description: 'Phishing, passwords and MFA, classification, clean desk.', mandatory: true, dueDays: 30, validityMonths: 12, passScore: 80 },
  { code: 'COC-100', title: 'Code of conduct', description: 'How we work together, conflicts of interest, speaking up.', mandatory: true, dueDays: 45, validityMonths: 24, passScore: 0 },
  { code: 'FIN-210', title: 'Financial controls for ERP users', description: 'Segregation of duties, approvals, audit trail in the ERP.', mandatory: false, dueDays: 30, validityMonths: 12, passScore: 85 },
  { code: 'PRIV-300', title: 'Privileged access handling', description: 'Rules for administrator accounts: break-glass, logging, four eyes.', mandatory: false, dueDays: 14, validityMonths: 6, passScore: 90 },
  { code: 'ONB-001', title: 'Welcome to the company', description: 'Organisation, tools, where to find help.', mandatory: false, dueDays: 14, validityMonths: null, passScore: 0 },
];

const byTeam = (t: string) => employees.filter((e) => e.team === t);
const systems = [
  { code: 'CRM', name: 'Customer CRM', description: 'Accounts, opportunities and customer contacts.', owner: salesLead.number, provisioning: 'ldap-group', requiredCourse: 'DP-101',
    roles: [{ name: 'Reader', description: 'Read accounts and contacts', privileged: false, validityDays: 365, group: 'crm-readers' },
      { name: 'Sales user', description: 'Manage own accounts and opportunities', privileged: false, validityDays: 365, group: 'crm-sales' },
      { name: 'Administrator', description: 'Configuration, users, data import', privileged: true, validityDays: 90, group: 'crm-admins' }] },
  { code: 'ERP', name: 'Finance ERP', description: 'General ledger, invoices, payments.', owner: finLead.number, provisioning: 'manual', requiredCourse: 'FIN-210',
    roles: [{ name: 'Invoice clerk', description: 'Record incoming invoices', privileged: false, validityDays: 365, group: null },
      { name: 'Payment approver', description: 'Release payments (four eyes)', privileged: true, validityDays: 90, group: null },
      { name: 'Reporting', description: 'Financial reports, read only', privileged: false, validityDays: 365, group: null }] },
  { code: 'HRP', name: 'HR Portal', description: 'Personnel files, contracts, payroll data.', owner: hrLead.number, provisioning: 'api', requiredCourse: 'DP-101',
    roles: [{ name: 'HR specialist', description: 'Personnel records of assigned units', privileged: false, validityDays: 365, group: null },
      { name: 'Payroll', description: 'Salary data', privileged: true, validityDays: 180, group: null }] },
  { code: 'GIT', name: 'Source code hosting', description: 'Repositories, pull requests, CI pipelines.', owner: cto.number, provisioning: 'ldap-group', requiredCourse: null,
    roles: [{ name: 'Developer', description: 'Push to team repositories', privileged: false, validityDays: 365, group: 'git-developers' },
      { name: 'Maintainer', description: 'Merge to protected branches, manage pipelines', privileged: false, validityDays: 365, group: 'git-maintainers' },
      { name: 'Organisation owner', description: 'Full administrative rights', privileged: true, validityDays: 30, group: 'git-owners' }] },
  { code: 'CLD', name: 'Cloud subscription', description: 'Production and test cloud resources.', owner: cto.number, provisioning: 'api', requiredCourse: 'PRIV-300',
    roles: [{ name: 'Reader', description: 'View resources and logs', privileged: false, validityDays: 365, group: null },
      { name: 'Contributor (test)', description: 'Change test resources', privileged: false, validityDays: 180, group: null },
      { name: 'Owner (production)', description: 'Change production, assign roles', privileged: true, validityDays: 30, group: null }] },
  { code: 'DWH', name: 'Data warehouse', description: 'Company reporting and analytics.', owner: teamLeads['Data Engineering'].number, provisioning: 'ldap-group', requiredCourse: 'DP-101',
    roles: [{ name: 'Analyst', description: 'Query curated data marts', privileged: false, validityDays: 365, group: 'dwh-analysts' },
      { name: 'Engineer', description: 'Build pipelines, raw zone access', privileged: false, validityDays: 365, group: 'dwh-engineers' }] },
  { code: 'VPN', name: 'Remote access VPN', description: 'Access to the internal network from outside.', owner: itLead.number, provisioning: 'ldap-group', requiredCourse: 'IS-101',
    roles: [{ name: 'Standard', description: 'Office network', privileged: false, validityDays: 365, group: 'vpn-users' },
      { name: 'Admin network', description: 'Management network of servers', privileged: true, validityDays: 30, group: 'vpn-admins' }] },
  { code: 'ITSM', name: 'IT service management', description: 'Tickets, changes and the asset register.', owner: itLead.number, provisioning: 'manual', requiredCourse: null,
    roles: [{ name: 'Agent', description: 'Work on tickets', privileged: false, validityDays: 365, group: null },
      { name: 'Change manager', description: 'Approve changes', privileged: false, validityDays: 365, group: null }] },
];

// trainings: mandatory ones for everyone who already works here, mostly completed
type Act = (string | number)[];
const assignments: { employee: string; course: string; assignedDaysAgo: number; actions: Act[] }[] = [];
for (const e of employees.filter((x) => x.status !== 'Preboarding')) {
  for (const c of courses.filter((x) => x.mandatory)) {
    const r = rnd();
    const assigned = Math.min(e.startDaysAgo, int(20, 330));
    const actions: Act[] = r < 0.72 ? [['complete', e.number, Math.max(c.passScore, int(80, 100))]]
      : r < 0.84 ? [['start', e.number]] : r < 0.92 ? [] : [['start', e.number], ['complete', e.number, Math.max(0, c.passScore - int(5, 30))]];
    assignments.push({ employee: e.number, course: c.code, assignedDaysAgo: assigned, actions });
  }
}
// joiners in preboarding already have their mandatory courses (assigned when HR registered them)
for (const e of employees.filter((x) => x.status === 'Preboarding'))
  for (const c of courses.filter((x) => x.mandatory)) assignments.push({ employee: e.number, course: c.code, assignedDaysAgo: 14, actions: [] });
for (const e of [...byTeam('Accounting'), ...byTeam('Controlling')])
  assignments.push({ employee: e.number, course: 'FIN-210', assignedDaysAgo: int(30, 200), actions: [['complete', e.number, int(86, 100)]] });
for (const e of [...byTeam('Platform'), ...byTeam('Identity & Access'), ...byTeam('Service Desk')].slice(0, 8))
  assignments.push({ employee: e.number, course: 'PRIV-300', assignedDaysAgo: int(5, 60), actions: rnd() < 0.6 ? [['complete', e.number, int(90, 100)]] : [] });

// access requests in every state; the seeder replays the actions through the domain, one day apart
const requests: { requester: string; beneficiary: string; system: string; role: string; justification: string; createdDaysAgo: number; actions: Act[] }[] = [];
const ownerOf = (code: string) => systems.find((s) => s.code === code)!.owner;
const managerOf = (num: string) => employees.find((e) => e.number === num)!.manager!;
const secOfficers = employees.filter((e) => e.roles.includes('SecurityOfficer')).map((e) => e.number);
const reasons: Record<string, string[]> = {
  CRM: ['Need to follow up on my accounts in the CRM', 'Preparing the quarterly customer review'],
  ERP: ['Recording supplier invoices for the Vienna office', 'Monthly closing and reporting'],
  HRP: ['Maintaining personnel records for my business units'],
  GIT: ['Working on the identity platform repositories', 'Joining the web applications team'],
  CLD: ['Investigating production incidents with the platform team', 'Deploying the new test environment'],
  DWH: ['Building the sales dashboard for the leadership team', 'Data pipelines for the HR analytics project'],
  VPN: ['Working from home two days a week', 'On-call duty needs remote access to servers'],
  ITSM: ['Joining the service desk rotation'],
};
const want: [string, string, number][] = [
  ['Sales Central Europe', 'CRM', 1], ['Sales Analytics', 'CRM', 0], ['Sales Analytics', 'DWH', 0], ['Customer Success', 'CRM', 1],
  ['Accounting', 'ERP', 0], ['Controlling', 'ERP', 2], ['HR Operations', 'HRP', 0], ['Platform', 'GIT', 1], ['Identity & Access', 'GIT', 0],
  ['Web Applications', 'GIT', 0], ['Platform', 'CLD', 1], ['Platform', 'CLD', 2], ['Data Engineering', 'DWH', 1], ['Service Desk', 'ITSM', 0],
  ['Platform', 'VPN', 1], ['Web Applications', 'VPN', 0], ['Identity & Access', 'CLD', 0], ['Security Operations', 'VPN', 1],
];
const outcomes = ['active', 'active', 'active', 'active', 'pendingManager', 'pendingOwner', 'pendingSecurity', 'approved', 'rejected', 'cancelled', 'revoked', 'draft'];
let k = 0;
for (const [team, sys, roleIx] of want) {
  for (const e of byTeam(team).filter((x) => !x.roles.includes('Manager') && x.status !== 'Preboarding').slice(0, 3)) {
    const s = systems.find((x) => x.code === sys)!;
    const role = s.roles[Math.min(roleIx, s.roles.length - 1)];
    let outcome = outcomes[k++ % outcomes.length];
    if (outcome === 'pendingSecurity' && !role.privileged) outcome = 'pendingOwner';
    const mgr = managerOf(e.number);
    const owner = ownerOf(sys);
    // four eyes: every step by a different person; an administrator stands in when the owner is already involved
    const admins = employees.filter((x) => x.roles.includes('Admin')).map((x) => x.number);
    const approveOwner = owner === mgr || owner === e.number ? admins.find((x) => x !== mgr && x !== e.number)! : owner;
    const sec = secOfficers.find((x) => x !== e.number && x !== mgr && x !== approveOwner)!;
    const a: Act[] = [];
    if (outcome !== 'draft') a.push(['submit', e.number]);
    if (['pendingOwner', 'pendingSecurity', 'approved', 'active', 'revoked'].includes(outcome)) a.push(['approve', mgr]);
    if (['pendingSecurity', 'approved', 'active', 'revoked'].includes(outcome)) a.push(['approve', approveOwner]);
    if (role.privileged && ['approved', 'active', 'revoked'].includes(outcome)) a.push(['approve', sec]);
    if (['active', 'revoked'].includes(outcome) && s.provisioning === 'manual') a.push(['provision', 'SYSTEM']);
    if (outcome === 'rejected') a.push(['reject', mgr, 'Please use the shared team role instead']);
    if (outcome === 'cancelled') a.push(['cancel', e.number, 'Not needed any more']);
    if (outcome === 'revoked') a.push(['revoke', mgr, 'Moved to a different project']);
    // approved but not provisioned only exists for manual systems
    if (outcome === 'approved' && s.provisioning !== 'manual') a.pop();
    requests.push({ requester: e.number, beneficiary: e.number, system: sys, role: role.name, justification: pick(reasons[sys]),
      createdDaysAgo: outcome === 'active' ? int(20, 300) : int(1, 15), actions: a });
  }
}
// the demo personas: the employee's own manager is the "Manager" persona, so approvals have something to show
const personaEmployee = byTeam('Sales Analytics').find((x) => !x.roles.includes('Manager'))!;
const personaManager = teamLeads['Sales Analytics'];
for (const e of byTeam('Sales Analytics').filter((x) => !x.roles.includes('Manager') && x !== personaEmployee && x.status !== 'Preboarding').slice(0, 2))
  requests.push({ requester: e.number, beneficiary: e.number, system: 'VPN', role: 'Standard', justification: 'Working from home two days a week', createdDaysAgo: int(1, 3), actions: [['submit', e.number]] });

// a manager requesting for a report (manager step implicit)
const webMgr = teamLeads['Web Applications'];
const webDev = byTeam('Web Applications').find((x) => !x.roles.includes('Manager'))!;
requests.push({ requester: webMgr.number, beneficiary: webDev.number, system: 'DWH', role: 'Analyst', justification: 'Needs usage statistics for the redesign', createdDaysAgo: 2, actions: [['submit', webMgr.number]] });

const incidents = [
  { reporter: byTeam('Sales Analytics')[1].number, title: 'CRM shows "access denied" on the reports page', description: 'Since Monday I cannot open the pipeline report.', priority: 'P2', category: 'access', createdDaysAgo: 1, actions: [] as Act[] },
  { reporter: byTeam('Web Applications')[2].number, title: 'Wrong manager in my employee record', description: 'My manager changed in October, the portal still shows the old one.', priority: 'P4', category: 'employee-data', createdDaysAgo: 3, actions: [['take', hrLead.number]] },
  { reporter: byTeam('Accounting')[1].number, title: 'ERP payment approval button missing', description: 'The approve button is greyed out for invoice INV-2291.', priority: 'P1', category: 'access', createdDaysAgo: 0, actions: [] as Act[] },
  { reporter: byTeam('Platform')[2].number, title: 'VPN disconnects every 10 minutes', description: 'Only from home, office Wi-Fi is fine.', priority: 'P3', category: 'other', createdDaysAgo: 4, actions: [['take', itLead.number], ['hold', itLead.number, 'Waiting for the VPN client logs']] },
  { reporter: byTeam('HR Operations')[1].number, title: 'Training DP-101 shows as overdue although completed', description: 'Completed it last week with 92%.', priority: 'P3', category: 'training', createdDaysAgo: 9, actions: [['take', hrLead.number], ['resolve', hrLead.number, 'The completion was recorded on the wrong course version - fixed.']] },
  { reporter: byTeam('Customer Success')[1].number, title: 'Cannot request access for my new colleague', description: 'The request form says I can only request for my direct reports.', priority: 'P4', category: 'access', createdDaysAgo: 12, actions: [['take', itLead.number], ['resolve', itLead.number, 'Only managers can request for others - your manager has submitted it.'], ['close', byTeam('Customer Success')[1].number]] },
];

const news = [
  { title: 'Annual access review starts on 1 December', body: 'Every application owner will receive the list of people with access to their application. Please confirm or remove each entry by 15 December.\n\nPrivileged roles are reviewed by the security team as well.', author: ciso.number, daysAgo: 2, pinned: true },
  { title: 'New: ask the assistant', body: 'The chat in the bottom-right corner answers questions about your requests, approvals, trainings and colleagues - in English or Hungarian.', author: itLead.number, daysAgo: 5, pinned: false },
  { title: 'Data protection training renewed', body: 'DP-101 has a new version with updated examples. Everyone whose completion expires this year will get the new version automatically.', author: hrLead.number, daysAgo: 16, pinned: false },
  { title: 'Welcome to our new colleagues', body: 'Two new colleagues join the Identity & Access and Sales Analytics teams this month. Managers: please request their access in advance.', author: hrLead.number, daysAgo: 21, pinned: false },
];

const data = {
  generated: 'tools/generate-demo-data.ts',
  personas: { employee: personaEmployee.number, manager: personaManager.number,
    owner: salesLead.number, hr: hrLead.number, security: ciso.number, admin: itLead.number },
  countries, locations,
  departments: departments.map((d) => ({ code: d.code, name: d.name, teams: d.teams.map((t) => ({ name: t, lead: teamLeads[t]?.number ?? null })) })),
  employees: employees.map((e) => ({ ...e, email: `${ascii(e.first)}.${ascii(e.last)}@${e.type === 'External' ? 'partner.' : ''}aegis.example` })),
  courses, systems, assignments, requests, incidents, news,
};
writeFileSync(join(import.meta.dirname, '..', 'seed', 'demo-data.json'), JSON.stringify(data, null, 1) + '\n');
console.log(`${employees.length} employees, ${requests.length} requests, ${assignments.length} assignments, ${incidents.length} tickets`);
