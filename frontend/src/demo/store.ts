// In-browser database of the demo: seeded from seed/demo-data.json exactly like the API's DemoSeeder (every
// workflow is replayed through the state classes), kept in memory and - when possible - in localStorage.

import seed from '../../../seed/demo-data.json';
import {
  AccessRequest, actorOf, Actor, addDays, Assignment, assignmentAction, assignTraining, Course, createRequest, Ctx, dateOnly, Employee,
  employeeAction, hire, Incident, incidentAction, nextHistoryId, PortalRole, record, requestAction, RequestContext, Role, setHistorySeed, SYSTEM,
  TargetSystem,
} from './domain';

export interface Country { code: string; name: string; eea: boolean }
export interface Location { id: number; name: string; countryCode: string }
export interface Department { id: number; code: string; name: string; parentId: number | null }
export interface Team { id: number; name: string; departmentId: number; leadId: number | null }
export interface Grant { id: number; employeeId: number; roleId: number; requestId: number; grantedAt: string; validUntil: string; endedAt: string | null }
export interface News { id: number; title: string; body: string; publishedAt: string; authorId: number; pinned: boolean }

export interface Db {
  version: number;
  countries: Country[];
  locations: Location[];
  departments: Department[];
  teams: Team[];
  employees: Employee[];
  systems: TargetSystem[];
  roles: Role[];
  courses: Course[];
  assignments: Assignment[];
  requests: AccessRequest[];
  grants: Grant[];
  incidents: Incident[];
  news: News[];
  seq: Record<string, number>;
  historySeq: number;
  /** Demo time travel: days added to the real clock. */
  dayOffset: number;
}

const DB_VERSION = 1;
const KEY = 'aegis-demo-db';
export type SeedData = typeof seed;
export const demoData: SeedData = seed;

export function nextId(db: Db, table: string) {
  db.seq[table] = (db.seq[table] ?? 0) + 1;
  return db.seq[table];
}

export const now = (db: Db) => new Date(Date.now() + db.dayOffset * 86_400_000);

/** Context the request states need (managers, owner, role). */
export function requestContext(db: Db, r: AccessRequest): RequestContext {
  const role = db.roles.find((x) => x.id === r.roleId)!;
  const system = db.systems.find((s) => s.id === role.systemId)!;
  return { role, ownerId: system.ownerId, managerOfBeneficiary: db.employees.find((e) => e.id === r.beneficiaryId)!.managerId };
}

/** Request → Active plus the grant (what the provisioner does). */
export function provision(db: Db, r: AccessRequest, ctx: Ctx) {
  requestAction(r, 'provision', requestContext(db, r), ctx);
  db.grants.push({ id: nextId(db, 'grants'), employeeId: r.beneficiaryId, roleId: r.roleId, requestId: r.id, grantedAt: ctx.now.toISOString(), validUntil: r.validUntil!, endedAt: null });
}

export function autoProvision(db: Db, r: AccessRequest, at: Date) {
  const role = db.roles.find((x) => x.id === r.roleId)!;
  const system = db.systems.find((s) => s.id === role.systemId)!;
  if (r.status === 'Approved' && system.provisioning !== 'manual') provision(db, r, { actor: { ...SYSTEM, name: 'Provisioning' }, now: at });
}

/** Builds the demo company, relative to the given moment. */
export function createSeededDb(at: Date = new Date(), data: SeedData = seed): Db {
  setHistorySeed(1);
  const db: Db = {
    version: DB_VERSION, countries: [], locations: [], departments: [], teams: [], employees: [], systems: [], roles: [], courses: [], assignments: [],
    requests: [], grants: [], incidents: [], news: [], seq: {}, historySeq: 1, dayOffset: 0,
  };
  const today = dateOnly(at);
  const daysAgo = (d: number) => new Date(at.getTime() - d * 86_400_000);

  db.countries = data.countries.map((c) => ({ code: c.code as string, name: c.name as string, eea: c.eea as boolean }));
  const locByName = new Map<string, Location>();
  for (const l of data.locations) {
    const loc = { id: nextId(db, 'locations'), name: l.name, countryCode: l.country };
    db.locations.push(loc);
    locByName.set(l.name, loc);
  }
  const teamByName = new Map<string, Team>();
  for (const d of data.departments) {
    const dept = { id: nextId(db, 'departments'), code: d.code, name: d.name, parentId: null };
    db.departments.push(dept);
    for (const t of d.teams) {
      const team = { id: nextId(db, 'teams'), name: t.name, departmentId: dept.id, leadId: null };
      db.teams.push(team);
      teamByName.set(t.name, team);
    }
  }

  const people = new Map<string, Employee>();
  for (const p of data.employees) {
    const e: Employee = {
      id: nextId(db, 'employees'), employeeNumber: p.number, firstName: p.first, lastName: p.last, email: p.email, jobTitle: p.title,
      type: p.type as 'Internal' | 'External', externalCompany: (p as { company?: string }).company ?? null, nationalityCode: p.nationality,
      locationId: locByName.get(p.location)?.id ?? null, teamId: teamByName.get(p.team)!.id,
      managerId: p.manager ? people.get(p.manager)!.id : null,
      startDate: addDays(today, -p.startDaysAgo), endDate: (p as { endInDays?: number }).endInDays !== undefined ? addDays(today, (p as { endInDays: number }).endInDays) : null,
      portalRoles: p.roles as PortalRole[], source: 'seed', status: 'Active', history: [], version: 1,
    };
    hire(e, today, { actor: { ...SYSTEM, name: 'HR import' }, now: daysAgo(Math.max(p.startDaysAgo, 0) + 14) });
    db.employees.push(e);
    people.set(p.number, e);
  }
  for (const d of data.departments) for (const t of d.teams) if (t.lead) teamByName.get(t.name)!.leadId = people.get(t.lead)!.id;
  const actor = (n: string): Actor => (n === 'SYSTEM' ? SYSTEM : actorOf(people.get(n)!));

  const courseByCode = new Map<string, Course>();
  for (const c of data.courses) {
    const course: Course = { id: nextId(db, 'courses'), code: c.code, title: c.title, description: c.description, mandatory: c.mandatory, dueDays: c.dueDays, validityMonths: c.validityMonths, passScore: c.passScore };
    db.courses.push(course);
    courseByCode.set(c.code, course);
  }

  const roleByKey = new Map<string, Role>();
  for (const s of data.systems) {
    const system: TargetSystem = {
      id: nextId(db, 'systems'), code: s.code, name: s.name, description: s.description, ownerId: people.get(s.owner)!.id, provisioning: s.provisioning,
      requiredTrainingId: s.requiredCourse ? courseByCode.get(s.requiredCourse)!.id : null,
    };
    db.systems.push(system);
    for (const r of s.roles) {
      const role: Role = { id: nextId(db, 'roles'), systemId: system.id, name: r.name, description: r.description, privileged: r.privileged, validityDays: r.validityDays, provisioningGroup: r.group };
      db.roles.push(role);
      roleByKey.set(`${s.code}/${r.name}`, role);
    }
  }

  for (const a of data.assignments) {
    const assignedAt = daysAgo(a.assignedDaysAgo);
    const course = courseByCode.get(a.course)!;
    const x = assignTraining(people.get(a.employee)!, course, dateOnly(assignedAt), { actor: { ...SYSTEM, name: 'Onboarding' }, now: assignedAt }, nextId(db, 'assignments'));
    a.actions.forEach((act, i) => {
      const ctx = { actor: actor(String(act[1])), now: new Date(assignedAt.getTime() + (i + 1) * 3 * 86_400_000) };
      assignmentAction(x, course, String(act[0]), ctx, act[0] === 'complete' ? Number(act[2]) : null);
    });
    db.assignments.push(x);
  }

  for (const r of data.requests) {
    const created = daysAgo(r.createdDaysAgo);
    const role = roleByKey.get(`${r.system}/${r.role}`)!;
    const req = createRequest(people.get(r.requester)!, people.get(r.beneficiary)!, role, r.justification, { actor: actor(r.requester), now: created }, nextId(db, 'requests'));
    db.requests.push(req);
    r.actions.forEach((act, i) => {
      const at = new Date(created.getTime() + (i + 1) * 20 * 3_600_000);
      const ctx: Ctx = { actor: actor(String(act[1])), now: at, comment: act.length > 2 ? String(act[2]) : null };
      if (act[0] === 'provision') provision(db, req, ctx);
      else {
        requestAction(req, String(act[0]), requestContext(db, req), ctx);
        if (act[0] === 'revoke') for (const g of db.grants.filter((g) => g.requestId === req.id)) g.endedAt = at.toISOString();
      }
      autoProvision(db, req, at);
    });
  }

  for (const t of data.incidents) {
    const created = new Date(daysAgo(t.createdDaysAgo).getTime() - 3 * 3_600_000);
    const id = nextId(db, 'incidents');
    const reporter = people.get(t.reporter)!;
    const i: Incident = {
      id, number: `INC-${String(id).padStart(6, '0')}`, title: t.title, description: t.description, priority: t.priority, category: t.category,
      reporterId: reporter.id, assigneeId: null, createdAt: created.toISOString(), resolvedAt: null, resolution: null, status: 'New', history: [],
    };
    record(i.history, 'open', '', 'New', { actor: actorOf(reporter), now: created });
    t.actions.forEach((act, k) => incidentAction(i, String(act[0]), { actor: actor(String(act[1])), now: new Date(created.getTime() + (k + 1) * 3_600_000), comment: act.length > 2 ? String(act[2]) : null }));
    db.incidents.push(i);
  }

  for (const n of data.news)
    db.news.push({ id: nextId(db, 'news'), title: n.title, body: n.body, publishedAt: daysAgo(n.daysAgo).toISOString(), authorId: people.get(n.author)!.id, pinned: n.pinned });

  for (const p of data.employees) {
    const status = (p as { status?: string }).status;
    if (!status || status === 'Preboarding') continue;
    const e = people.get(p.number)!;
    const ctx = { actor: { ...SYSTEM, name: 'HR' }, now: daysAgo(3), comment: 'Recorded by HR' };
    if (status === 'OnLeave') employeeAction(e, 'start-leave', ctx);
    if (status === 'Offboarding') employeeAction(e, 'give-notice', ctx, e.endDate ?? addDays(today, 30));
  }
  db.historySeq = nextHistoryId();
  return db;
}

// ------------------------------------------------------------------ persistence (best effort: storage can be unavailable)

export function loadDb(): Db {
  try {
    const raw = globalThis.localStorage?.getItem(KEY);
    if (raw) {
      const db = JSON.parse(raw) as Db;
      if (db.version === DB_VERSION) {
        setHistorySeed(db.historySeq);
        return db;
      }
    }
  } catch {
    /* private mode, blocked storage, broken JSON: start fresh */
  }
  return createSeededDb();
}

export function saveDb(db: Db) {
  db.historySeq = nextHistoryId();
  try {
    globalThis.localStorage?.setItem(KEY, JSON.stringify(db));
  } catch {
    /* the demo still works, it just forgets on reload */
  }
}

export function resetDb(): Db {
  try { globalThis.localStorage?.removeItem(KEY); } catch { /* ignore */ }
  return createSeededDb();
}
