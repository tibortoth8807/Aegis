// The demo backend: the API's use cases (Aegis.Application) over the in-browser store. Same endpoints, same rules,
// same error codes - so the UI cannot tell it apart from the .NET API.

import {
  AegisApi, ApiError, AssignmentDto, ChatTurn, CourseDto, EmployeeDetail, EmployeeFilter, EmployeeInput, EmployeeListItem, HistoryEntry, IncidentDetail,
  IncidentListItem, Page, PageQuery, PersonRef, RequestDetail, RequestFilter, RequestListItem, SystemDto,
} from '../api/types';
import { answer } from './assistant';
import {
  AccessRequest, Actor, actorOf, Assignment, assignmentAction, assignTraining, ASSIGNMENT_STATES, conflict, createRequest, Ctx, dateOnly,
  Employee, employeeAction, EMPLOYEE_STATES, EmployeeStatus, forbidden, fullName, hire, Incident, incidentAction, INCIDENT_STATES, is, isAdmin,
  isValidCompletion, MAX_OPEN, notFound, OPEN_REQUEST, PortalRole, record, requestAction, REQUEST_STATES, rule, SLA_HOURS, StateChange, SYSTEM, validation,
} from './domain';
import { directoryImport } from './files';
import { buildExport, EXPORTS } from './exports';
import { autoProvision, Db, demoData, loadDb, nextId, now, provision, requestContext, resetDb, saveDb } from './store';

const delay = () => new Promise((r) => setTimeout(r, 60 + Math.random() * 120));

export interface DemoApi extends AegisApi {
  /** Demo only: start over with fresh data. */
  reset(): void;
  /** Demo only: move the clock forward to see the daily jobs act. */
  travel(days: number): void;
  readonly dayOffset: number;
}

export function demoApi(user: () => string | null, initial?: Db): DemoApi {
  let db = initial ?? loadDb();

  // ------------------------------------------------------------------ helpers
  const emp = (id: number | null | undefined) => (id == null ? undefined : db.employees.find((e) => e.id === id));
  const ref = (e: Employee): PersonRef => ({ id: e.id, name: fullName(e), employeeNumber: e.employeeNumber });
  const me = (): Actor => {
    const number = user();
    const e = db.employees.find((x) => x.employeeNumber === number && x.status !== 'Left');
    if (!e) throw new ApiError(401, 'unauthorized', 'Sign in first', `Unknown or inactive employee ${number}.`);
    return actorOf(e);
  };
  const ctx = (comment?: string | null): Ctx => ({ actor: me(), now: now(db), comment: comment ?? null });
  const today = () => dateOnly(now(db));
  const requireAny = (...roles: PortalRole[]) => {
    const a = me();
    if (isAdmin(a) || roles.some((r) => is(a, r))) return;
    throw forbidden(`This needs one of the roles: ${roles.join(', ')}.`);
  };
  const history = (h: StateChange[]): HistoryEntry[] =>
    [...h].sort((a, b) => b.at.localeCompare(a.at) || b.id - a.id).map((x) => ({ at: x.at, actor: x.actorName, action: x.action, from: x.from, to: x.to, comment: x.comment }));
  const paged = <T>(all: T[], q: PageQuery): Page<T> => {
    const size = Math.min(Math.max(q.pageSize ?? 25, 1), 200);
    const page = Math.max(q.page ?? 1, 1);
    return { items: all.slice((page - 1) * size, page * size), total: all.length, pageNumber: page, pageSize: size };
  };
  const has = (text: string, s: string) => text.toLowerCase().includes(s);
  const role = (id: number) => db.roles.find((r) => r.id === id)!;
  const systemOf = (roleId: number) => db.systems.find((s) => s.id === role(roleId).systemId)!;

  /** Runs a mutation; on success the database is saved, on error it is thrown away (like a transaction). */
  async function tx<T>(work: () => T): Promise<T> {
    await delay();
    const snapshot = JSON.stringify(db);
    try {
      const result = work();
      saveDb(db);
      return result;
    } catch (e) {
      db = JSON.parse(snapshot) as Db;
      throw e;
    }
  }
  async function read<T>(work: () => T): Promise<T> {
    await delay();
    return work();
  }

  // ------------------------------------------------------------------ employees
  const employeeDetail = (e: Employee): EmployeeDetail => {
    const a = me();
    const n = now(db).toISOString();
    return {
      id: e.id, employeeNumber: e.employeeNumber, firstName: e.firstName, lastName: e.lastName, email: e.email, jobTitle: e.jobTitle, type: e.type,
      externalCompany: e.externalCompany, status: e.status, nationalityCode: e.nationalityCode, locationId: e.locationId,
      location: db.locations.find((l) => l.id === e.locationId)?.name ?? null, teamId: e.teamId, team: db.teams.find((t) => t.id === e.teamId)?.name ?? null,
      manager: emp(e.managerId) ? ref(emp(e.managerId)!) : null, startDate: e.startDate, endDate: e.endDate, portalRoles: e.portalRoles,
      source: e.source, version: e.version,
      directReports: db.employees.filter((x) => x.managerId === e.id && x.status !== 'Left').sort((x, y) => x.lastName.localeCompare(y.lastName)).map(ref),
      access: db.grants.filter((g) => g.employeeId === e.id && !g.endedAt && g.validUntil > n).map((g) => ({
        id: g.id, system: systemOf(g.roleId).name, role: role(g.roleId).name, privileged: role(g.roleId).privileged, grantedAt: g.grantedAt, validUntil: g.validUntil, requestId: g.requestId,
      })).sort((x, y) => x.system.localeCompare(y.system)),
      trainings: db.assignments.filter((x) => x.employeeId === e.id).sort((x, y) => x.dueOn.localeCompare(y.dueOn))
        .map((x) => ({ id: x.id, course: db.courses.find((c) => c.id === x.courseId)!.title, status: x.status, dueOn: x.dueOn, validUntil: x.validUntil })),
      history: history(e.history),
      allowedActions: isAdmin(a) || is(a, 'Hr') ? EMPLOYEE_STATES[e.status].allowedActions : [],
    };
  };

  const applyInput = (e: Employee, input: EmployeeInput) => {
    const req = (v: string | null | undefined, field: string, max = 200) => {
      const t = (v ?? '').trim();
      if (!t) throw validation(field, 'Required.');
      if (t.length > max) throw validation(field, `At most ${max} characters.`);
      return t;
    };
    e.firstName = req(input.firstName, 'FirstName', 80);
    e.lastName = req(input.lastName, 'LastName', 80);
    e.jobTitle = req(input.jobTitle, 'JobTitle', 120);
    if (input.type !== 'Internal' && input.type !== 'External') throw validation('Type', 'Internal or External.');
    e.type = input.type;
    e.externalCompany = e.type === 'External' ? input.externalCompany?.trim() || null : null;
    e.nationalityCode = input.nationalityCode?.trim().toUpperCase() || null;
    if (e.nationalityCode && !db.countries.some((c) => c.code === e.nationalityCode)) throw validation('NationalityCode', `Unknown country code ${e.nationalityCode}.`);
    e.locationId = input.locationId ?? null;
    e.teamId = input.teamId ?? null;
    if (input.managerId != null) {
      if (input.managerId === e.id) throw validation('ManagerId', 'Nobody is their own manager.');
      if (!db.employees.some((x) => x.id === input.managerId && x.status !== 'Left')) throw validation('ManagerId', 'The manager does not exist or has left.');
      for (let c: number | null = input.managerId, depth = 0; c !== null && depth < 50; depth++) {
        if (c === e.id && e.id !== 0) throw rule('This would create a loop in the reporting line.', 'employee.manager_loop');
        c = emp(c)?.managerId ?? null;
      }
    }
    e.managerId = input.managerId ?? null;
    e.startDate = input.startDate;
    e.endDate = input.endDate || null;
    if (input.portalRoles && isAdmin(me())) e.portalRoles = ['Employee', ...(input.portalRoles.filter((r) => r !== 'Employee') as PortalRole[])];
  };

  // ------------------------------------------------------------------ requests
  const awaitsMe = (r: AccessRequest, a: Actor) => {
    if (r.requesterId === a.id || r.beneficiaryId === a.id || r.approverIds.includes(a.id)) return false;
    const x = requestContext(db, r);
    return REQUEST_STATES[r.status].canDecide(r, x, a) && ['PendingManager', 'PendingOwner', 'PendingSecurity'].includes(r.status);
  };
  const requestActions = (r: AccessRequest, a: Actor) => {
    const x = requestContext(db, r);
    const s = REQUEST_STATES[r.status];
    return s.allowedActions.filter((act) => ({
      submit: a.id === r.requesterId || isAdmin(a),
      approve: s.canDecide(r, x, a),
      reject: s.canDecide(r, x, a),
      cancel: a.id === r.requesterId || a.id === r.beneficiaryId || isAdmin(a),
      provision: isAdmin(a),
      revoke: isAdmin(a) || is(a, 'SecurityOfficer') || a.id === x.ownerId || a.id === x.managerOfBeneficiary,
    } as Record<string, boolean>)[act] ?? false);
  };
  const requestDetail = (r: AccessRequest): RequestDetail => {
    const rl = role(r.roleId);
    const steps = ['Draft', 'Manager', 'Application owner', ...(rl.privileged ? ['Security officer'] : []), 'Provisioning', 'Active'];
    const index: Record<string, number> = { Draft: 0, PendingManager: 1, PendingOwner: 2, PendingSecurity: 3, Approved: steps.length - 2, Active: steps.length - 1 };
    return {
      id: r.id, number: r.number, status: r.status, requester: ref(emp(r.requesterId)!), beneficiary: ref(emp(r.beneficiaryId)!), systemId: rl.systemId,
      system: systemOf(r.roleId).name, roleId: rl.id, role: rl.name, privileged: rl.privileged, justification: r.justification, createdAt: r.createdAt,
      submittedAt: r.submittedAt, decidedAt: r.decidedAt, validUntil: r.validUntil, steps, currentStep: index[r.status] ?? -1, history: history(r.history),
      allowedActions: requestActions(r, me()),
    };
  };
  const revokeGrant = (r: AccessRequest, at: Date) => {
    for (const g of db.grants.filter((g) => g.requestId === r.id && !g.endedAt)) g.endedAt = at.toISOString();
  };
  const revokeAll = (e: Employee, c: Ctx) => {
    const sys = { actor: { ...SYSTEM, name: 'Offboarding' }, now: c.now, comment: 'Employee left the company' };
    for (const r of db.requests.filter((r) => r.beneficiaryId === e.id && r.status === 'Active')) {
      requestAction(r, 'revoke', requestContext(db, r), sys);
      revokeGrant(r, c.now);
    }
    for (const r of db.requests.filter((r) => r.beneficiaryId === e.id && ['Draft', 'PendingManager', 'PendingOwner', 'PendingSecurity'].includes(r.status)))
      requestAction(r, 'cancel', requestContext(db, r), sys);
  };

  // ------------------------------------------------------------------ trainings, tickets
  const assignmentDto = (a: Assignment): AssignmentDto => {
    const m = me();
    const mine = a.employeeId === m.id;
    const hr = isAdmin(m) || is(m, 'Hr');
    const e = emp(a.employeeId)!;
    return {
      id: a.id, employee: ref(e), courseId: a.courseId, course: db.courses.find((c) => c.id === a.courseId)!.title, status: a.status, assignedOn: a.assignedOn,
      dueOn: a.dueOn, completedOn: a.completedOn, validUntil: a.validUntil, score: a.score, attempts: a.attempts,
      allowedActions: ASSIGNMENT_STATES[a.status].allowedActions.filter((x) => (x === 'start' || x === 'complete' ? mine || hr : x === 'waive' ? hr : false)),
    };
  };
  const isAgent = () => isAdmin(me()) || is(me(), 'Hr');
  const ticketDetail = (i: Incident): IncidentDetail => {
    const m = me();
    if (i.reporterId !== m.id && !isAgent()) throw forbidden('You can only see your own tickets.');
    return {
      id: i.id, number: i.number, title: i.title, description: i.description, priority: i.priority, category: i.category, status: i.status,
      reporter: ref(emp(i.reporterId)!), assignee: emp(i.assigneeId) ? ref(emp(i.assigneeId)!) : null, createdAt: i.createdAt,
      dueAt: new Date(Date.parse(i.createdAt) + SLA_HOURS[i.priority] * 3_600_000).toISOString(), resolvedAt: i.resolvedAt, resolution: i.resolution,
      history: history(i.history),
      allowedActions: INCIDENT_STATES[i.status].allowedActions.filter((a) =>
        ['take', 'hold', 'resume', 'resolve'].includes(a) ? isAgent() : ['reopen', 'close', 'cancel'].includes(a) ? i.reporterId === m.id || isAdmin(m) : false),
    };
  };

  // ------------------------------------------------------------------ the daily jobs
  const runJobs = () => {
    const at = now(db);
    const t = dateOnly(at);
    const sys: Ctx = { actor: SYSTEM, now: at };
    const report = { activated: 0, left: 0, accessExpired: 0, provisioned: 0, trainingsOverdue: 0, trainingsExpired: 0, ticketsClosed: 0 };
    for (const e of db.employees.filter((e) => e.status === 'Preboarding' && e.startDate <= t)) { employeeAction(e, 'activate', sys); report.activated++; }
    for (const e of db.employees.filter((e) => e.status === 'Offboarding' && e.endDate! < t)) { employeeAction(e, 'leave', sys); revokeAll(e, sys); report.left++; }
    for (const r of db.requests.filter((r) => r.status === 'Active' && r.validUntil! <= at.toISOString())) {
      requestAction(r, 'expire', requestContext(db, r), { ...sys, comment: 'Validity ended' });
      revokeGrant(r, at);
      report.accessExpired++;
    }
    for (const r of db.requests.filter((r) => r.status === 'Approved')) { provision(db, r, { actor: { ...SYSTEM, name: 'Provisioning' }, now: at }); report.provisioned++; }
    for (const a of db.assignments.filter((a) => (a.status === 'Assigned' || a.status === 'InProgress') && a.dueOn < t)) {
      assignmentAction(a, db.courses.find((c) => c.id === a.courseId)!, 'mark-overdue', sys);
      report.trainingsOverdue++;
    }
    for (const a of db.assignments.filter((a) => a.status === 'Completed' && a.validUntil !== null && a.validUntil < t)) {
      assignmentAction(a, db.courses.find((c) => c.id === a.courseId)!, 'expire', sys);
      report.trainingsExpired++;
    }
    for (const i of db.incidents.filter((i) => i.status === 'Resolved' && i.resolvedAt && Date.parse(i.resolvedAt) <= at.getTime() - 5 * 86_400_000)) {
      incidentAction(i, 'close', { ...sys, comment: 'Closed automatically 5 days after resolution' });
      report.ticketsClosed++;
    }
    return report;
  };
  // like the API's hosted service: the jobs run at start
  runJobs();
  saveDb(db);

  const api: DemoApi = {
    mode: 'demo',
    get dayOffset() { return db.dayOffset; },
    reset() { db = resetDb(); runJobs(); saveDb(db); },
    travel(days) { db.dayOffset += days; saveDb(db); },

    personas: async () => {
      const p = demoData.personas;
      return [
        { role: 'Employee', number: p.employee }, { role: 'Manager', number: p.manager }, { role: 'Application owner', number: p.owner },
        { role: 'HR', number: p.hr }, { role: 'Security officer', number: p.security }, { role: 'Administrator', number: p.admin },
      ];
    },
    me: () => read(() => {
      const e = emp(me().id)!;
      return { id: e.id, name: fullName(e), employeeNumber: e.employeeNumber, jobTitle: e.jobTitle, roles: e.portalRoles };
    }),
    counters: () => read(() => {
      const a = me();
      const n = now(db).toISOString();
      const staff = isAdmin(a) || is(a, 'Hr') || is(a, 'SecurityOfficer');
      const live = db.employees.filter((e) => e.status !== 'Left');
      const soon = new Date(now(db).getTime() + 14 * 86_400_000).toISOString();
      const active = db.grants.filter((g) => !g.endedAt && g.validUntil > n);
      return {
        myOpenRequests: db.requests.filter((r) => (r.requesterId === a.id || r.beneficiaryId === a.id) && OPEN_REQUEST.includes(r.status)).length,
        awaitingMyApproval: db.requests.filter((r) => awaitsMe(r, a)).length,
        myOpenTickets: db.incidents.filter((i) => i.reporterId === a.id && ['New', 'InProgress', 'OnHold'].includes(i.status)).length,
        ticketQueue: isAdmin(a) || is(a, 'Hr') ? db.incidents.filter((i) => ['New', 'InProgress', 'OnHold'].includes(i.status)).length : 0,
        myTrainingsDue: db.assignments.filter((x) => x.employeeId === a.id && ['Assigned', 'InProgress', 'Overdue', 'Expired'].includes(x.status)).length,
        teamTrainingsOverdue: db.assignments.filter((x) => emp(x.employeeId)?.managerId === a.id && ['Overdue', 'Expired'].includes(x.status)).length,
        activeEmployees: staff ? live.filter((e) => e.status === 'Active' || e.status === 'OnLeave').length : 0,
        joiners: staff ? live.filter((e) => e.status === 'Preboarding').length : 0,
        leavers: staff ? live.filter((e) => e.status === 'Offboarding').length : 0,
        externals: staff ? live.filter((e) => e.type === 'External').length : 0,
        activeGrants: staff ? active.length : 0,
        privilegedExpiringSoon: staff ? active.filter((g) => g.validUntil <= soon && role(g.roleId).privileged).length : 0,
      };
    }),

    employees: (q, f: EmployeeFilter) => read(() => {
      me();
      let list = db.employees;
      const s = q.search?.trim().toLowerCase();
      if (s) list = list.filter((e) => has(fullName(e), s) || has(e.email, s) || has(e.employeeNumber, s) || has(e.jobTitle, s));
      if (f.status) list = list.filter((e) => e.status.toLowerCase() === f.status!.toLowerCase());
      if (f.type) list = list.filter((e) => e.type.toLowerCase() === f.type!.toLowerCase());
      if (f.teamId) list = list.filter((e) => e.teamId === Number(f.teamId));
      if (f.locationId) list = list.filter((e) => e.locationId === Number(f.locationId));
      if (f.managerId) list = list.filter((e) => e.managerId === Number(f.managerId));
      if (f.nationality) list = list.filter((e) => e.nationalityCode === f.nationality);
      const key: Record<string, (e: Employee) => string> = {
        number: (e) => e.employeeNumber, title: (e) => e.jobTitle, start: (e) => e.startDate, status: (e) => e.status, name: (e) => `${e.lastName} ${e.firstName}`,
      };
      const by = key[q.sort ?? 'name'] ?? key.name;
      const sorted = [...list].sort((a, b) => by(a).localeCompare(by(b), 'hu') * (q.desc ? -1 : 1));
      return paged<EmployeeListItem>(sorted.map((e) => ({
        id: e.id, employeeNumber: e.employeeNumber, fullName: fullName(e), email: e.email, jobTitle: e.jobTitle, type: e.type, status: e.status,
        team: db.teams.find((t) => t.id === e.teamId)?.name ?? null, location: db.locations.find((l) => l.id === e.locationId)?.name ?? null,
        manager: emp(e.managerId) ? fullName(emp(e.managerId)!) : null, nationality: e.nationalityCode, startDate: e.startDate, endDate: e.endDate,
      })), q);
    }),
    employee: (id) => read(() => employeeDetail(emp(id) ?? (() => { throw notFound('Employee', id); })())),
    createEmployee: (input) => tx(() => {
      requireAny('Hr');
      const number = (input.employeeNumber ?? '').trim().toUpperCase();
      if (!number) throw validation('EmployeeNumber', 'Required.');
      if (db.employees.some((e) => e.employeeNumber === number)) throw rule(`Employee number ${number} is already in use.`, 'employee.duplicate_number');
      const email = (input.email ?? '').trim().toLowerCase();
      if (!email) throw validation('Email', 'Required.');
      if (db.employees.some((e) => e.email === email)) throw rule(`The e-mail address ${email} is already in use.`, 'employee.duplicate_email');
      const e: Employee = {
        id: 0, employeeNumber: number, email, firstName: '', lastName: '', jobTitle: '', type: 'Internal', externalCompany: null, nationalityCode: null,
        locationId: null, teamId: null, managerId: null, startDate: input.startDate, endDate: null, portalRoles: ['Employee'], source: 'manual', status: 'Active', history: [], version: 1,
      };
      applyInput(e, input);
      hire(e, today(), ctx());
      e.id = nextId(db, 'employees');
      db.employees.push(e);
      const onboarding = { actor: { ...SYSTEM, name: 'Onboarding' }, now: now(db) };
      for (const c of db.courses.filter((c) => c.mandatory)) db.assignments.push(assignTraining(e, c, today(), onboarding, nextId(db, 'assignments')));
      return employeeDetail(e);
    }),
    updateEmployee: (id, input) => tx(() => {
      requireAny('Hr');
      const e = emp(id) ?? (() => { throw notFound('Employee', id); })();
      if (input.version !== e.version) throw conflict('Somebody else changed this employee in the meantime. Reload and try again.');
      const email = (input.email ?? '').trim().toLowerCase();
      if (!email) throw validation('Email', 'Required.');
      if (db.employees.some((x) => x.email === email && x.id !== id)) throw rule(`The e-mail address ${email} is already in use.`, 'employee.duplicate_email');
      e.email = email;
      applyInput(e, input);
      e.version++;
      return employeeDetail(e);
    }),
    employeeTransition: (id, action, endDate, comment) => tx(() => {
      const e = emp(id) ?? (() => { throw notFound('Employee', id); })();
      const c = ctx(comment);
      employeeAction(e, action, c, endDate);
      e.version++;
      if ((e.status as EmployeeStatus) === 'Left') revokeAll(e, c);
      return employeeDetail(e);
    }),

    lookups: () => read(() => {
      const live = db.employees.filter((e) => e.status !== 'Left');
      const managers = live.filter((e) => e.portalRoles.includes('Manager')).sort((a, b) => a.lastName.localeCompare(b.lastName)).map(ref);
      return {
        countries: [...db.countries].sort((a, b) => a.name.localeCompare(b.name)).map((c) => ({ ...c, employees: live.filter((e) => e.nationalityCode === c.code).length })),
        locations: [...db.locations].sort((a, b) => a.name.localeCompare(b.name)).map((l) => ({ ...l, employees: live.filter((e) => e.locationId === l.id).length })),
        departments: [...db.departments].sort((a, b) => a.name.localeCompare(b.name)).map((d) => ({
          ...d,
          teams: db.teams.filter((t) => t.departmentId === d.id).sort((a, b) => a.name.localeCompare(b.name)).map((t) => ({
            id: t.id, name: t.name, departmentId: d.id, department: d.name, lead: managers.find((m) => m.id === t.leadId) ?? null, members: live.filter((e) => e.teamId === t.id).length,
          })),
        })),
        managers,
      };
    }),
    saveCountry: (code, name, eea) => tx(() => {
      requireAny('Hr');
      const c = code.trim().toUpperCase();
      if (!/^[A-Z]{2}$/.test(c)) throw validation('code', 'Two-letter ISO code, e.g. HU.');
      if (!name.trim()) throw validation('name', 'Required.');
      let country = db.countries.find((x) => x.code === c);
      if (!country) db.countries.push((country = { code: c, name: '', eea: false }));
      country.name = name.trim();
      country.eea = eea;
      return { ...country, employees: db.employees.filter((e) => e.nationalityCode === c && e.status !== 'Left').length };
    }),
    deleteCountry: (code) => tx(() => {
      requireAny('Hr');
      const c = db.countries.find((x) => x.code === code) ?? (() => { throw notFound('Country', code); })();
      if (db.employees.some((e) => e.nationalityCode === code)) throw rule(`${c.name} is the nationality of employees and cannot be deleted.`, 'country.in_use');
      db.countries = db.countries.filter((x) => x !== c);
    }),
    saveTeam: (id, name, departmentId, leadId) => tx(() => {
      requireAny('Hr');
      const dept = db.departments.find((d) => d.id === departmentId) ?? (() => { throw notFound('Department', departmentId); })();
      if (!name.trim()) throw validation('Name', 'Required.');
      let team = id === null ? undefined : db.teams.find((t) => t.id === id);
      if (id !== null && !team) throw notFound('Team', id);
      if (!team) db.teams.push((team = { id: nextId(db, 'teams'), name: '', departmentId, leadId: null }));
      team.name = name.trim();
      team.departmentId = dept.id;
      team.leadId = leadId;
      const lead = emp(leadId);
      return { id: team.id, name: team.name, departmentId: dept.id, department: dept.name, lead: lead ? ref(lead) : null, members: db.employees.filter((e) => e.teamId === team!.id).length };
    }),

    applications: (search) => read((): SystemDto[] => {
      me();
      const n = now(db).toISOString();
      const s = search?.trim().toLowerCase();
      return db.systems
        .filter((x) => !s || has(x.name, s) || has(x.code, s) || db.roles.some((r) => r.systemId === x.id && has(r.name, s)))
        .sort((a, b) => a.name.localeCompare(b.name))
        .map((x) => ({
          id: x.id, code: x.code, name: x.name, description: x.description, owner: emp(x.ownerId) ? ref(emp(x.ownerId)!) : null, provisioning: x.provisioning,
          requiredTrainingId: x.requiredTrainingId, requiredTraining: db.courses.find((c) => c.id === x.requiredTrainingId)?.title ?? null,
          roles: db.roles.filter((r) => r.systemId === x.id).sort((a, b) => Number(a.privileged) - Number(b.privileged) || a.name.localeCompare(b.name))
            .map((r) => ({ ...r, activeGrants: db.grants.filter((g) => g.roleId === r.id && !g.endedAt && g.validUntil > n).length })),
        }));
    }),

    requests: (q, f: RequestFilter) => read(() => {
      const a = me();
      let list = db.requests;
      const scope = f.scope ?? 'mine';
      if (scope === 'approvals') list = list.filter((r) => awaitsMe(r, a));
      else if (scope === 'all') {
        if (!isAdmin(a) && !is(a, 'SecurityOfficer') && !is(a, 'Hr')) throw forbidden('Only administrators, HR and security officers see all requests.');
      } else if (scope === 'team') list = list.filter((r) => emp(r.beneficiaryId)?.managerId === a.id);
      else list = list.filter((r) => r.requesterId === a.id || r.beneficiaryId === a.id);
      if (f.status === 'open') list = list.filter((r) => OPEN_REQUEST.includes(r.status));
      else if (f.status) list = list.filter((r) => r.status.toLowerCase() === f.status!.toLowerCase());
      if (f.systemId) list = list.filter((r) => role(r.roleId).systemId === Number(f.systemId));
      const s = q.search?.trim().toLowerCase();
      if (s) list = list.filter((r) => has(r.number, s) || has(role(r.roleId).name, s) || has(systemOf(r.roleId).name, s) || has(fullName(emp(r.beneficiaryId)!), s));
      const sorted = [...list].sort((x, y) => (q.sort && !q.desc ? 1 : -1) * x.createdAt.localeCompare(y.createdAt));
      return paged<RequestListItem>(sorted.map((r) => ({
        id: r.id, number: r.number, status: r.status, system: systemOf(r.roleId).name, role: role(r.roleId).name, privileged: role(r.roleId).privileged,
        requester: fullName(emp(r.requesterId)!), beneficiary: fullName(emp(r.beneficiaryId)!), createdAt: r.createdAt, validUntil: r.validUntil, awaitsMe: awaitsMe(r, a),
      })), q);
    }),
    request: (id) => read(() => {
      const r = db.requests.find((x) => x.id === id) ?? (() => { throw notFound('Request', id); })();
      const a = me();
      const x = requestContext(db, r);
      const involved = [r.requesterId, r.beneficiaryId, x.managerOfBeneficiary, x.ownerId, ...r.approverIds].includes(a.id) || isAdmin(a) || is(a, 'SecurityOfficer') || is(a, 'Hr');
      if (!involved) throw forbidden('You are not involved in this request.');
      return requestDetail(r);
    }),
    createRequest: (input) => tx(() => {
      const a = me();
      const requester = emp(a.id)!;
      const beneficiary = emp(input.beneficiaryId ?? a.id) ?? (() => { throw notFound('Employee', input.beneficiaryId); })();
      if (beneficiary.id !== a.id && beneficiary.managerId !== a.id && !isAdmin(a) && !is(a, 'Hr'))
        throw forbidden('You can request access for yourself and for your direct reports.');
      const rl = db.roles.find((r) => r.id === input.roleId) ?? (() => { throw notFound('Role', input.roleId); })();
      const system = systemOf(rl.id);
      const n = now(db).toISOString();
      if (db.grants.some((g) => g.employeeId === beneficiary.id && g.roleId === rl.id && !g.endedAt && g.validUntil > n))
        throw rule(`${fullName(beneficiary)} already has ${system.name} / ${rl.name}.`, 'request.already_granted');
      const open = db.requests.filter((r) => r.beneficiaryId === beneficiary.id && OPEN_REQUEST.includes(r.status));
      const dup = open.find((r) => r.roleId === rl.id);
      if (dup) throw rule(`There is already an open request for this role: ${dup.number}.`, 'request.duplicate');
      if (open.length >= MAX_OPEN) throw rule(`${fullName(beneficiary)} already has ${open.length} open requests - wait until some are decided.`, 'request.too_many_open');
      if (system.requiredTrainingId !== null && !db.assignments.some((x) => x.employeeId === beneficiary.id && x.courseId === system.requiredTrainingId && isValidCompletion(x, today()))) {
        const course = db.courses.find((c) => c.id === system.requiredTrainingId)!;
        throw rule(`${system.name} requires the training "${course.title}" first.`, 'request.training_required');
      }
      const r = createRequest(requester, beneficiary, rl, input.justification ?? '', ctx(), nextId(db, 'requests'));
      db.requests.push(r);
      if (input.submit !== false) requestAction(r, 'submit', requestContext(db, r), ctx());
      return requestDetail(r);
    }),
    requestTransition: (id, action, comment) => tx(() => {
      const r = db.requests.find((x) => x.id === id) ?? (() => { throw notFound('Request', id); })();
      const c = ctx(comment);
      if (action === 'provision') {
        if (!REQUEST_STATES[r.status].allows('provision')) requestAction(r, 'provision', requestContext(db, r), c);
        requireAny('Admin');
        provision(db, r, c);
      } else {
        requestAction(r, action, requestContext(db, r), c);
        if (action === 'revoke') revokeGrant(r, c.now);
      }
      autoProvision(db, r, c.now);
      return requestDetail(r);
    }),

    courses: () => read((): CourseDto[] => db.courses.map((c) => {
      const mine = db.assignments.filter((a) => a.courseId === c.id);
      return {
        ...c, assigned: mine.filter((a) => a.status === 'Assigned' || a.status === 'InProgress').length,
        completed: mine.filter((a) => a.status === 'Completed').length, overdue: mine.filter((a) => a.status === 'Overdue' || a.status === 'Expired').length,
      };
    }).sort((a, b) => a.code.localeCompare(b.code))),
    assignments: (q, scope, status, courseId) => read(() => {
      const a = me();
      let list = db.assignments;
      if (scope === 'all') requireAny('Hr');
      else if (scope === 'team') list = list.filter((x) => emp(x.employeeId)?.managerId === a.id);
      else list = list.filter((x) => x.employeeId === a.id);
      if (status) list = list.filter((x) => x.status.toLowerCase() === status.toLowerCase());
      if (courseId) list = list.filter((x) => x.courseId === Number(courseId));
      const s = q.search?.trim().toLowerCase();
      if (s) list = list.filter((x) => has(fullName(emp(x.employeeId)!), s) || has(db.courses.find((c) => c.id === x.courseId)!.title, s));
      const done = (x: Assignment) => (x.status === 'Completed' || x.status === 'Waived' ? 1 : 0);
      return paged(list.slice().sort((x, y) => done(x) - done(y) || x.dueOn.localeCompare(y.dueOn)).map(assignmentDto), q);
    }),
    assign: (courseId, employeeIds) => tx(() => {
      const a = me();
      const course = db.courses.find((c) => c.id === courseId) ?? (() => { throw notFound('Course', courseId); })();
      const people = db.employees.filter((e) => employeeIds.includes(e.id) && e.status !== 'Left');
      if (!isAdmin(a) && !is(a, 'Hr') && people.some((p) => p.managerId !== a.id)) throw forbidden('Managers can assign courses to their direct reports only.');
      let n = 0;
      for (const p of people) {
        if (db.assignments.some((x) => x.courseId === courseId && x.employeeId === p.id && x.status !== 'Expired' && x.status !== 'Waived')) continue;
        db.assignments.push(assignTraining(p, course, today(), ctx(), nextId(db, 'assignments')));
        n++;
      }
      return n;
    }),
    assignmentTransition: (id, action, score, comment) => tx(() => {
      const x = db.assignments.find((a) => a.id === id) ?? (() => { throw notFound('Assignment', id); })();
      assignmentAction(x, db.courses.find((c) => c.id === x.courseId)!, action, ctx(comment), score);
      return assignmentDto(x);
    }),

    tickets: (q, scope, status) => read(() => {
      const a = me();
      let list = db.incidents;
      if (scope === 'queue') { if (!isAgent()) throw forbidden('The ticket queue is for the support team.'); }
      else list = list.filter((i) => i.reporterId === a.id);
      if (status === 'open') list = list.filter((i) => ['New', 'InProgress', 'OnHold'].includes(i.status));
      else if (status) list = list.filter((i) => i.status.toLowerCase() === status.toLowerCase());
      const s = q.search?.trim().toLowerCase();
      if (s) list = list.filter((i) => has(i.number, s) || has(i.title, s));
      const n = now(db).getTime();
      return paged<IncidentListItem>([...list].sort((x, y) => x.priority.localeCompare(y.priority) || x.createdAt.localeCompare(y.createdAt)).map((i) => {
        const due = Date.parse(i.createdAt) + SLA_HOURS[i.priority] * 3_600_000;
        return {
          id: i.id, number: i.number, title: i.title, priority: i.priority, category: i.category, status: i.status, reporter: fullName(emp(i.reporterId)!),
          assignee: emp(i.assigneeId) ? fullName(emp(i.assigneeId)!) : null, createdAt: i.createdAt, dueAt: new Date(due).toISOString(),
          breached: ['New', 'InProgress', 'OnHold'].includes(i.status) && due < n,
        };
      }), q);
    }),
    ticket: (id) => read(() => ticketDetail(db.incidents.find((i) => i.id === id) ?? (() => { throw notFound('Ticket', id); })())),
    createTicket: (input) => tx(() => {
      const a = me();
      if (!['P1', 'P2', 'P3', 'P4'].includes(input.priority)) throw validation('Priority', 'P1-P4.');
      if (!['access', 'employee-data', 'training', 'other'].includes(input.category)) throw validation('Category', 'One of: access, employee-data, training, other.');
      if (!input.title?.trim()) throw validation('Title', 'Required.');
      const id = nextId(db, 'incidents');
      const i: Incident = {
        id, number: `INC-${String(id).padStart(6, '0')}`, title: input.title.trim(), description: (input.description ?? '').trim(), priority: input.priority,
        category: input.category, reporterId: a.id, assigneeId: null, createdAt: now(db).toISOString(), resolvedAt: null, resolution: null, status: 'New', history: [],
      };
      record(i.history, 'open', '', 'New', ctx());
      db.incidents.push(i);
      return ticketDetail(i);
    }),
    ticketTransition: (id, action, comment) => tx(() => {
      const i = db.incidents.find((x) => x.id === id) ?? (() => { throw notFound('Ticket', id); })();
      incidentAction(i, action, ctx(comment));
      return ticketDetail(i);
    }),

    news: (take = 10) => read(() => [...db.news]
      .sort((a, b) => Number(b.pinned) - Number(a.pinned) || b.publishedAt.localeCompare(a.publishedAt)).slice(0, take)
      .map((n) => ({ id: n.id, title: n.title, body: n.body, publishedAt: n.publishedAt, author: fullName(emp(n.authorId)!), pinned: n.pinned }))),
    publishNews: (title, body, pinned) => tx(() => {
      requireAny('Hr');
      if (!title.trim()) throw validation('Title', 'Required.');
      if (!body.trim()) throw validation('Body', 'Required.');
      const n = { id: nextId(db, 'news'), title: title.trim(), body: body.trim(), pinned, publishedAt: now(db).toISOString(), authorId: me().id };
      db.news.push(n);
      return { ...n, author: me().name };
    }),
    deleteNews: (id) => tx(() => {
      requireAny('Hr');
      if (!db.news.some((n) => n.id === id)) throw notFound('News', id);
      db.news = db.news.filter((n) => n.id !== id);
    }),

    exports: () => read(() => {
      const a = me();
      return EXPORTS.filter((x) => isAdmin(a) || x.roles.some((r) => is(a, r as PortalRole)));
    }),
    download: (key, format) => read(() => {
      const a = me();
      const def = EXPORTS.find((x) => x.key === key) ?? (() => { throw notFound('Export', key); })();
      if (!isAdmin(a) && !def.roles.some((r) => is(a, r as PortalRole))) throw forbidden(`The ${def.title} export needs one of the roles: ${def.roles.join(', ')}.`);
      return buildExport(db, key, format, now(db));
    }),
    importFile: (fileName, content, dryRun) => (dryRun ? read : tx)(() => {
      requireAny('Hr');
      return directoryImport(db, fileName, content, dryRun, ctx(), today());
    }),
    runJobs: () => tx(() => {
      requireAny('Admin');
      return runJobs();
    }),
    ask: (message: string, h: ChatTurn[]) => read(() => answer(db, me(), message, h, now(db))),
  };
  return api;
}

