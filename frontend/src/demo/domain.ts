// The demo backend's domain: the same workflows and rules as Aegis.Domain (C#), ported to TypeScript so the
// GitHub Pages demo behaves like the real API. One class per state here too - see StateBase.

import { ApiError } from '../api/types';

// ------------------------------------------------------------------ errors (same codes and statuses as the API)

export const validation = (field: string, message: string) => new ApiError(400, 'validation', 'Invalid input', 'One or more fields are invalid.', { [field]: [message] });
export const forbidden = (message: string) => new ApiError(403, 'forbidden', 'Not allowed', message);
export const notFound = (entity: string, key: unknown) => new ApiError(404, 'not_found', 'Not found', `${entity} '${key}' was not found.`);
export const rule = (message: string, code: string) => new ApiError(422, code, 'Business rule violated', message);
export const conflict = (message: string) => new ApiError(409, 'concurrency', 'Changed by someone else', message);
export const invalidTransition = (entity: string, state: string, action: string) =>
  Object.assign(new ApiError(409, 'state.transition', 'Not possible in the current state', `Cannot ${action} a ${entity} that is ${state}.`), { state, action });

// ------------------------------------------------------------------ actors

export type PortalRole = 'Employee' | 'Manager' | 'Hr' | 'SecurityOfficer' | 'Admin';

export interface Actor {
  id: number;
  name: string;
  roles: PortalRole[];
}

export const SYSTEM: Actor = { id: 0, name: 'System', roles: ['Admin'] };
export const is = (a: Actor, r: PortalRole) => a.roles.includes(r);
export const isAdmin = (a: Actor) => is(a, 'Admin');

export interface StateChange {
  id: number;
  at: string;
  actorId: number;
  actorName: string;
  action: string;
  from: string;
  to: string;
  comment: string | null;
}

export interface Ctx {
  actor: Actor;
  now: Date;
  comment?: string | null;
}

/** "PendingManager" → "pending manager". */
export const humanize = (s: string) => s.replace(/(?!^)([A-Z])/g, ' $1').toLowerCase();

let historyId = 1;
export const setHistorySeed = (n: number) => (historyId = n);
export const nextHistoryId = () => historyId;

export function record(history: StateChange[], action: string, from: string, to: string, ctx: Ctx) {
  history.push({ id: historyId++, at: ctx.now.toISOString(), actorId: ctx.actor.id, actorName: ctx.actor.name, action, from, to, comment: ctx.comment ?? null });
}

/**
 * State pattern base. A workflow's abstract state class declares every action, throwing by default; a concrete
 * state overrides the actions it supports. The allowed actions are derived from which methods it overrides.
 */
export abstract class StateBase<E> {
  abstract readonly status: string;
  protected abstract readonly entity: string;
  /** Action name → method name, declared by each workflow's base class. */
  protected abstract readonly actionMethods: Record<string, string>;

  get allowedActions(): string[] {
    const base = Object.getPrototypeOf(this.constructor.prototype) as Record<string, unknown>;
    const self = this as unknown as Record<string, unknown>;
    // overridden = the method found on the instance differs from the workflow base class's default
    let root = base;
    while (Object.getPrototypeOf(root) && Object.getPrototypeOf(root) !== StateBase.prototype) root = Object.getPrototypeOf(root);
    return Object.entries(this.actionMethods)
      .filter(([, method]) => self[method] !== root[method])
      .map(([action]) => action)
      .sort();
  }

  get isFinal() {
    return this.allowedActions.length === 0;
  }

  allows(action: string) {
    return this.allowedActions.includes(action);
  }

  protected notAllowed(action: string): never {
    throw invalidTransition(this.entity, humanize(this.status), action);
  }

  /** Entry action after the status changed. */
  onEnter(_e: E, _ctx: Ctx): void {}
}

// ------------------------------------------------------------------ employees

export type EmployeeStatus = 'Preboarding' | 'Active' | 'OnLeave' | 'Offboarding' | 'Left';

export interface Employee {
  id: number;
  employeeNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  jobTitle: string;
  type: 'Internal' | 'External';
  externalCompany: string | null;
  nationalityCode: string | null;
  locationId: number | null;
  teamId: number | null;
  managerId: number | null;
  startDate: string;
  endDate: string | null;
  portalRoles: PortalRole[];
  source: string;
  status: EmployeeStatus;
  history: StateChange[];
  version: number;
}

export const fullName = (e: { firstName: string; lastName: string }) => `${e.firstName} ${e.lastName}`;
export const actorOf = (e: Employee): Actor => ({ id: e.id, name: fullName(e), roles: e.portalRoles });
const requireHr = (ctx: Ctx) => {
  if (!is(ctx.actor, 'Hr') && !isAdmin(ctx.actor)) throw forbidden('Only HR or an administrator can change the employment status.');
};

export abstract class EmployeeState extends StateBase<Employee> {
  protected readonly entity = 'employee';
  protected readonly actionMethods = { activate: 'activate', 'start-leave': 'startLeave', return: 'returnFromLeave', 'give-notice': 'giveNotice', 'withdraw-notice': 'withdrawNotice', leave: 'leave' };
  activate(_e: Employee, _c: Ctx): EmployeeState { return this.notAllowed('activate'); }
  startLeave(_e: Employee, _c: Ctx): EmployeeState { return this.notAllowed('start leave for'); }
  returnFromLeave(_e: Employee, _c: Ctx): EmployeeState { return this.notAllowed('return'); }
  giveNotice(_e: Employee, _end: string, _c: Ctx): EmployeeState { return this.notAllowed('give notice to'); }
  withdrawNotice(_e: Employee, _c: Ctx): EmployeeState { return this.notAllowed('withdraw the notice of'); }
  leave(_e: Employee, _c: Ctx): EmployeeState { return this.notAllowed('offboard'); }

  protected noticeTo(e: Employee, endDate: string, ctx: Ctx): EmployeeState {
    requireHr(ctx);
    if (endDate < e.startDate) throw validation('endDate', 'The end date is before the start date.');
    e.endDate = endDate;
    return EMPLOYEE_STATES.Offboarding;
  }
}

class PreboardingState extends EmployeeState {
  readonly status = 'Preboarding';
  override activate(_e: Employee, c: Ctx) { if (c.actor !== SYSTEM) requireHr(c); return EMPLOYEE_STATES.Active; }
  override giveNotice(e: Employee, end: string, c: Ctx) { return this.noticeTo(e, end, c); }
}
class ActiveEmployeeState extends EmployeeState {
  readonly status = 'Active';
  override startLeave(_e: Employee, c: Ctx) { requireHr(c); return EMPLOYEE_STATES.OnLeave; }
  override giveNotice(e: Employee, end: string, c: Ctx) { return this.noticeTo(e, end, c); }
}
class OnLeaveState extends EmployeeState {
  readonly status = 'OnLeave';
  override returnFromLeave(_e: Employee, c: Ctx) { requireHr(c); return EMPLOYEE_STATES.Active; }
  override giveNotice(e: Employee, end: string, c: Ctx) { return this.noticeTo(e, end, c); }
}
class OffboardingState extends EmployeeState {
  readonly status = 'Offboarding';
  override withdrawNotice(e: Employee, c: Ctx) {
    requireHr(c);
    if (e.type === 'External') throw rule('An external contract cannot be open-ended - extend the end date instead.', 'employee.external_end_date');
    e.endDate = null;
    return EMPLOYEE_STATES.Active;
  }
  override leave(_e: Employee, c: Ctx) { if (c.actor !== SYSTEM) requireHr(c); return EMPLOYEE_STATES.Left; }
}
class LeftState extends EmployeeState {
  readonly status = 'Left';
}

export const EMPLOYEE_STATES: Record<EmployeeStatus, EmployeeState> = {
  Preboarding: new PreboardingState(), Active: new ActiveEmployeeState(), OnLeave: new OnLeaveState(), Offboarding: new OffboardingState(), Left: new LeftState(),
};

export function hire(e: Employee, today: string, ctx: Ctx) {
  if (e.type === 'External') {
    if (!e.endDate) throw rule('An external employee needs a contract end date.', 'employee.external_end_date');
    if (e.managerId === null) throw rule('An external employee needs a sponsoring manager.', 'employee.external_sponsor');
    if (!e.externalCompany?.trim()) throw validation('ExternalCompany', 'Required for external employees.');
  }
  if (e.endDate && e.endDate < e.startDate) throw validation('EndDate', 'The end date is before the start date.');
  e.status = e.startDate > today ? 'Preboarding' : 'Active';
  record(e.history, 'hire', '', e.status, ctx);
}

export function employeeAction(e: Employee, action: string, ctx: Ctx, endDate?: string | null) {
  const s = EMPLOYEE_STATES[e.status];
  const map: Record<string, () => EmployeeState> = {
    activate: () => s.activate(e, ctx),
    'start-leave': () => s.startLeave(e, ctx),
    return: () => s.returnFromLeave(e, ctx),
    'give-notice': () => s.giveNotice(e, endDate ?? (() => { throw validation('endDate', 'Required.'); })(), ctx),
    'withdraw-notice': () => s.withdrawNotice(e, ctx),
    leave: () => s.leave(e, ctx),
  };
  const run = map[action] ?? (() => { throw validation('action', `Unknown action '${action}'.`); });
  const to = run();
  record(e.history, action, e.status, to.status, ctx);
  e.status = to.status as EmployeeStatus;
}

// ------------------------------------------------------------------ access requests

export type RequestStatus = 'Draft' | 'PendingManager' | 'PendingOwner' | 'PendingSecurity' | 'Approved' | 'Active' | 'Rejected' | 'Cancelled' | 'Expired' | 'Revoked';

export interface Role { id: number; systemId: number; name: string; description: string; privileged: boolean; validityDays: number; provisioningGroup: string | null }
export interface TargetSystem { id: number; code: string; name: string; description: string; ownerId: number; provisioning: string; requiredTrainingId: number | null }

export interface AccessRequest {
  id: number;
  number: string;
  requesterId: number;
  beneficiaryId: number;
  roleId: number;
  justification: string;
  createdAt: string;
  submittedAt: string | null;
  decidedAt: string | null;
  validUntil: string | null;
  approverIds: number[];
  status: RequestStatus;
  history: StateChange[];
}

/** What the request states need to know about the world (the C# version uses navigation properties). */
export interface RequestContext {
  managerOfBeneficiary: number | null;
  ownerId: number;
  role: Role;
}

export const PRIVILEGED_MAX_DAYS = 30;
export const MIN_JUSTIFICATION = 15;
export const MAX_OPEN = 5;
export const OPEN_REQUEST: RequestStatus[] = ['Draft', 'PendingManager', 'PendingOwner', 'PendingSecurity', 'Approved'];

const neutral = (r: AccessRequest, a: Actor) => a.id !== r.requesterId && a.id !== r.beneficiaryId && !r.approverIds.includes(a.id);

export abstract class RequestState extends StateBase<AccessRequest> {
  protected readonly entity = 'request';
  protected readonly actionMethods = { submit: 'submit', approve: 'approve', reject: 'reject', cancel: 'cancel', provision: 'provision', revoke: 'revoke', expire: 'expire' };
  submit(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('submit'); }
  approve(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('approve'); }
  reject(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('reject'); }
  cancel(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('cancel'); }
  provision(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('provision'); }
  revoke(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('revoke'); }
  expire(_r: AccessRequest, _x: RequestContext, _c: Ctx): RequestState { return this.notAllowed('expire'); }
  canDecide(_r: AccessRequest, _x: RequestContext, _a: Actor): boolean { return false; }
  /** Entry action with the request's context (the C# entity reaches it through navigation properties). */
  enter(r: AccessRequest, _x: RequestContext, c: Ctx): void { this.onEnter(r, c); }

  protected ensureCanDecide(r: AccessRequest, x: RequestContext, a: Actor) {
    if (a.id === r.requesterId) throw forbidden('You cannot approve or reject a request you created.');
    if (a.id === r.beneficiaryId) throw forbidden('You cannot approve or reject access for yourself.');
    if (r.approverIds.includes(a.id)) throw forbidden('You already approved this request - another person must do the next step.');
    if (!this.canDecide(r, x, a)) throw forbidden(`You are not an approver for a request that is ${humanize(this.status)}.`);
  }

  protected cancelBy(r: AccessRequest, c: Ctx): RequestState {
    if (c.actor.id !== r.requesterId && c.actor.id !== r.beneficiaryId && !isAdmin(c.actor))
      throw forbidden('Only the requester, the beneficiary or an administrator can cancel a request.');
    return REQUEST_STATES.Cancelled;
  }
}

class DraftState extends RequestState {
  readonly status = 'Draft';
  override submit(r: AccessRequest, x: RequestContext, c: Ctx) {
    if (c.actor.id !== r.requesterId && !isAdmin(c.actor)) throw forbidden('Only the requester can submit the request.');
    if (x.managerOfBeneficiary === null || x.managerOfBeneficiary === r.requesterId) {
      if (x.managerOfBeneficiary !== null) r.approverIds.push(x.managerOfBeneficiary);
      return REQUEST_STATES.PendingOwner;
    }
    return REQUEST_STATES.PendingManager;
  }
  override cancel(r: AccessRequest, _x: RequestContext, c: Ctx) { return this.cancelBy(r, c); }
}

abstract class PendingState extends RequestState {
  override reject(r: AccessRequest, x: RequestContext, c: Ctx) {
    this.ensureCanDecide(r, x, c.actor);
    if (!c.comment?.trim()) throw validation('comment', 'Give a reason for the rejection.');
    return REQUEST_STATES.Rejected;
  }
  override cancel(r: AccessRequest, _x: RequestContext, c: Ctx) { return this.cancelBy(r, c); }
  override onEnter(r: AccessRequest, c: Ctx) { r.submittedAt ??= c.now.toISOString(); }
  protected step(r: AccessRequest, x: RequestContext, c: Ctx, next: RequestState) {
    this.ensureCanDecide(r, x, c.actor);
    r.approverIds.push(c.actor.id);
    return next;
  }
}
class PendingManagerState extends PendingState {
  readonly status = 'PendingManager';
  override canDecide(r: AccessRequest, x: RequestContext, a: Actor) { return neutral(r, a) && (a.id === x.managerOfBeneficiary || isAdmin(a)); }
  override approve(r: AccessRequest, x: RequestContext, c: Ctx) { return this.step(r, x, c, REQUEST_STATES.PendingOwner); }
}
class PendingOwnerState extends PendingState {
  readonly status = 'PendingOwner';
  override canDecide(r: AccessRequest, x: RequestContext, a: Actor) { return neutral(r, a) && (a.id === x.ownerId || isAdmin(a)); }
  override approve(r: AccessRequest, x: RequestContext, c: Ctx) { return this.step(r, x, c, x.role.privileged ? REQUEST_STATES.PendingSecurity : REQUEST_STATES.Approved); }
}
class PendingSecurityState extends PendingState {
  readonly status = 'PendingSecurity';
  override canDecide(r: AccessRequest, _x: RequestContext, a: Actor) { return neutral(r, a) && is(a, 'SecurityOfficer'); }
  override approve(r: AccessRequest, x: RequestContext, c: Ctx) { return this.step(r, x, c, REQUEST_STATES.Approved); }
}
class ApprovedState extends RequestState {
  readonly status = 'Approved';
  override onEnter(r: AccessRequest, c: Ctx) { r.decidedAt = c.now.toISOString(); }
  override provision(_r: AccessRequest, _x: RequestContext, c: Ctx) {
    if (!isAdmin(c.actor)) throw forbidden('Only provisioning (an administrator or the system) can activate access.');
    return REQUEST_STATES.Active;
  }
}
class ActiveRequestState extends RequestState {
  readonly status = 'Active';
  override enter(r: AccessRequest, x: RequestContext, c: Ctx) {
    const days = x.role.privileged ? Math.min(x.role.validityDays, PRIVILEGED_MAX_DAYS) : x.role.validityDays;
    r.validUntil = new Date(c.now.getTime() + days * 86_400_000).toISOString();
  }
  override revoke(_r: AccessRequest, x: RequestContext, c: Ctx) {
    const a = c.actor;
    if (!(isAdmin(a) || is(a, 'SecurityOfficer') || a.id === x.ownerId || a.id === x.managerOfBeneficiary))
      throw forbidden('Only an administrator, a security officer, the application owner or the manager can revoke access.');
    if (!c.comment?.trim()) throw validation('comment', 'Give a reason for revoking the access.');
    return REQUEST_STATES.Revoked;
  }
  override expire(r: AccessRequest, _x: RequestContext, c: Ctx) {
    if (r.validUntil && new Date(r.validUntil) > c.now) throw rule(`The access is valid until ${r.validUntil.slice(0, 10)} and cannot expire yet.`, 'request.not_expired');
    return REQUEST_STATES.Expired;
  }
}
class FinalRequestState extends RequestState {
  constructor(readonly status: RequestStatus) { super(); }
  override onEnter(r: AccessRequest, c: Ctx) { r.decidedAt ??= c.now.toISOString(); }
}

export const REQUEST_STATES = {
  Draft: new DraftState() as RequestState,
  PendingManager: new PendingManagerState() as RequestState,
  PendingOwner: new PendingOwnerState() as RequestState,
  PendingSecurity: new PendingSecurityState() as RequestState,
  Approved: new ApprovedState() as RequestState,
  Active: new ActiveRequestState() as RequestState,
  Rejected: new FinalRequestState('Rejected') as RequestState,
  Cancelled: new FinalRequestState('Cancelled') as RequestState,
  Expired: new FinalRequestState('Expired') as RequestState,
  Revoked: new FinalRequestState('Revoked') as RequestState,
} satisfies Record<RequestStatus, RequestState>;

export function createRequest(requester: Employee, beneficiary: Employee, role: Role, justification: string, ctx: Ctx, id: number): AccessRequest {
  const j = justification.trim();
  if (j.length < MIN_JUSTIFICATION) throw validation('Justification', `Explain in at least ${MIN_JUSTIFICATION} characters why the access is needed.`);
  if (beneficiary.status === 'Left' || beneficiary.status === 'Offboarding')
    throw rule(`${fullName(beneficiary)} is leaving the company - no new access can be requested.`, 'request.beneficiary_leaving');
  const r: AccessRequest = {
    id, number: `REQ-${String(id).padStart(6, '0')}`, requesterId: requester.id, beneficiaryId: beneficiary.id, roleId: role.id, justification: j,
    createdAt: ctx.now.toISOString(), submittedAt: null, decidedAt: null, validUntil: null, approverIds: [], status: 'Draft', history: [],
  };
  record(r.history, 'create', '', 'Draft', ctx);
  return r;
}

export function requestAction(r: AccessRequest, action: string, x: RequestContext, ctx: Ctx) {
  const s = REQUEST_STATES[r.status];
  const calls: Record<string, () => RequestState> = {
    submit: () => s.submit(r, x, ctx), approve: () => s.approve(r, x, ctx), reject: () => s.reject(r, x, ctx), cancel: () => s.cancel(r, x, ctx),
    provision: () => s.provision(r, x, ctx), revoke: () => s.revoke(r, x, ctx), expire: () => s.expire(r, x, ctx),
  };
  const run = calls[action] ?? (() => { throw validation('action', `Unknown action '${action}'.`); });
  const to = run();
  record(r.history, action, r.status, to.status, ctx);
  r.status = to.status as RequestStatus;
  to.enter(r, x, ctx);
}

// ------------------------------------------------------------------ trainings

export type AssignmentStatus = 'Assigned' | 'InProgress' | 'Completed' | 'Overdue' | 'Expired' | 'Waived';

export interface Course { id: number; code: string; title: string; description: string; mandatory: boolean; dueDays: number; validityMonths: number | null; passScore: number }

export interface Assignment {
  id: number;
  employeeId: number;
  courseId: number;
  assignedOn: string;
  dueOn: string;
  completedOn: string | null;
  validUntil: string | null;
  score: number | null;
  attempts: number;
  status: AssignmentStatus;
  history: StateChange[];
}

export const dateOnly = (d: Date) => d.toISOString().slice(0, 10);
export const addDays = (day: string, n: number) => dateOnly(new Date(Date.parse(day + 'T00:00:00Z') + n * 86_400_000));
export const addMonths = (day: string, n: number) => {
  const d = new Date(day + 'T00:00:00Z');
  const target = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + n, 1));
  const last = new Date(Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0)).getUTCDate();
  target.setUTCDate(Math.min(d.getUTCDate(), last));
  return dateOnly(target);
};

const selfOrHr = (a: Assignment, c: Ctx) => {
  if (c.actor.id !== a.employeeId && !is(c.actor, 'Hr') && !isAdmin(c.actor)) throw forbidden('Only the employee or HR can record progress on a training.');
};

export abstract class AssignmentState extends StateBase<Assignment> {
  protected readonly entity = 'training assignment';
  protected readonly actionMethods = { start: 'start', complete: 'complete', 'mark-overdue': 'markOverdue', expire: 'expire', waive: 'waive' };
  start(_a: Assignment, _course: Course, _c: Ctx): AssignmentState { return this.notAllowed('start'); }
  complete(_a: Assignment, _course: Course, _score: number, _c: Ctx): AssignmentState { return this.notAllowed('complete'); }
  markOverdue(_a: Assignment, _c: Ctx): AssignmentState { return this.notAllowed('mark as overdue'); }
  expire(_a: Assignment, _c: Ctx): AssignmentState { return this.notAllowed('expire'); }
  waive(_a: Assignment, _c: Ctx): AssignmentState { return this.notAllowed('waive'); }

  protected attempt(a: Assignment, course: Course, score: number, c: Ctx): AssignmentState {
    selfOrHr(a, c);
    if (!(score >= 0 && score <= 100)) throw validation('score', 'The score is a percentage between 0 and 100.');
    a.attempts++;
    a.score = score;
    if (score < course.passScore) return ASSIGNMENT_STATES.InProgress;
    const today = dateOnly(c.now);
    a.completedOn = today;
    a.validUntil = course.validityMonths ? addMonths(today, course.validityMonths) : null;
    return ASSIGNMENT_STATES.Completed;
  }
  protected waiveBy(c: Ctx): AssignmentState {
    if (!is(c.actor, 'Hr') && !isAdmin(c.actor)) throw forbidden('Only HR can waive a training.');
    if (!c.comment?.trim()) throw validation('comment', 'Give a reason for the waiver.');
    return ASSIGNMENT_STATES.Waived;
  }
  protected overdueIfDue(a: Assignment, c: Ctx): AssignmentState {
    if (dateOnly(c.now) > a.dueOn) return ASSIGNMENT_STATES.Overdue;
    throw rule('The training is not due yet.', 'training.not_due');
  }
}

class AssignedState extends AssignmentState {
  readonly status = 'Assigned';
  override start(a: Assignment, _course: Course, c: Ctx) { selfOrHr(a, c); return ASSIGNMENT_STATES.InProgress; }
  override complete(a: Assignment, course: Course, score: number, c: Ctx) { return this.attempt(a, course, score, c); }
  override markOverdue(a: Assignment, c: Ctx) { return this.overdueIfDue(a, c); }
  override waive(_a: Assignment, c: Ctx) { return this.waiveBy(c); }
}
class InProgressState extends AssignmentState {
  readonly status = 'InProgress';
  override complete(a: Assignment, course: Course, score: number, c: Ctx) { return this.attempt(a, course, score, c); }
  override markOverdue(a: Assignment, c: Ctx) { return this.overdueIfDue(a, c); }
  override waive(_a: Assignment, c: Ctx) { return this.waiveBy(c); }
}
class OverdueState extends AssignmentState {
  readonly status = 'Overdue';
  override start(a: Assignment, _course: Course, c: Ctx) { selfOrHr(a, c); return ASSIGNMENT_STATES.InProgress; }
  override complete(a: Assignment, course: Course, score: number, c: Ctx) {
    const next = this.attempt(a, course, score, c);
    return next === ASSIGNMENT_STATES.InProgress ? ASSIGNMENT_STATES.Overdue : next;
  }
  override waive(_a: Assignment, c: Ctx) { return this.waiveBy(c); }
}
class CompletedState extends AssignmentState {
  readonly status = 'Completed';
  override expire(a: Assignment, c: Ctx) {
    if (a.validUntil && a.validUntil < dateOnly(c.now)) return ASSIGNMENT_STATES.Expired;
    throw rule('The completion is still valid.', 'training.still_valid');
  }
}
class ExpiredAssignmentState extends AssignmentState {
  readonly status = 'Expired';
  override start(a: Assignment, course: Course, c: Ctx) {
    selfOrHr(a, c);
    a.dueOn = addDays(dateOnly(c.now), course.dueDays);
    return ASSIGNMENT_STATES.InProgress;
  }
}
class WaivedState extends AssignmentState {
  readonly status = 'Waived';
}

export const ASSIGNMENT_STATES = {
  Assigned: new AssignedState() as AssignmentState,
  InProgress: new InProgressState() as AssignmentState,
  Completed: new CompletedState() as AssignmentState,
  Overdue: new OverdueState() as AssignmentState,
  Expired: new ExpiredAssignmentState() as AssignmentState,
  Waived: new WaivedState() as AssignmentState,
} satisfies Record<AssignmentStatus, AssignmentState>;

export function assignTraining(employee: Employee, course: Course, today: string, ctx: Ctx, id: number): Assignment {
  const a: Assignment = {
    id, employeeId: employee.id, courseId: course.id, assignedOn: today, dueOn: addDays(today, course.dueDays), completedOn: null, validUntil: null,
    score: null, attempts: 0, status: 'Assigned', history: [],
  };
  record(a.history, 'assign', '', 'Assigned', ctx);
  return a;
}

export function assignmentAction(a: Assignment, course: Course, action: string, ctx: Ctx, score?: number | null) {
  const s = ASSIGNMENT_STATES[a.status];
  const calls: Record<string, () => AssignmentState> = {
    start: () => s.start(a, course, ctx),
    complete: () => s.complete(a, course, score ?? (() => { throw validation('score', 'Required.'); })(), ctx),
    'mark-overdue': () => s.markOverdue(a, ctx),
    expire: () => s.expire(a, ctx),
    waive: () => s.waive(a, ctx),
  };
  const run = calls[action] ?? (() => { throw validation('action', `Unknown action '${action}'.`); });
  const to = run();
  record(a.history, action, a.status, to.status, ctx);
  a.status = to.status as AssignmentStatus;
}

export const isValidCompletion = (a: Assignment, today: string) => a.status === 'Completed' && (a.validUntil === null || a.validUntil >= today);

// ------------------------------------------------------------------ tickets

export type IncidentStatus = 'New' | 'InProgress' | 'OnHold' | 'Resolved' | 'Closed' | 'Cancelled';
export const AUTO_CLOSE_DAYS = 5;
export const SLA_HOURS: Record<string, number> = { P1: 4, P2: 8, P3: 24, P4: 72 };

export interface Incident {
  id: number;
  number: string;
  title: string;
  description: string;
  priority: string;
  category: string;
  reporterId: number;
  assigneeId: number | null;
  createdAt: string;
  resolvedAt: string | null;
  resolution: string | null;
  status: IncidentStatus;
  history: StateChange[];
}

const agent = (c: Ctx) => {
  if (!isAdmin(c.actor) && !is(c.actor, 'Hr')) throw forbidden('Only the support team (administrators, HR) works on tickets.');
};

export abstract class IncidentState extends StateBase<Incident> {
  protected readonly entity = 'ticket';
  protected readonly actionMethods = { take: 'take', hold: 'hold', resume: 'resume', resolve: 'resolve', reopen: 'reopen', close: 'close', cancel: 'cancel' };
  take(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('take'); }
  hold(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('put on hold'); }
  resume(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('resume'); }
  resolve(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('resolve'); }
  reopen(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('reopen'); }
  close(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('close'); }
  cancel(_i: Incident, _c: Ctx): IncidentState { return this.notAllowed('cancel'); }
  protected cancelBy(i: Incident, c: Ctx) {
    if (c.actor.id !== i.reporterId && !isAdmin(c.actor)) throw forbidden('Only the reporter can cancel a ticket.');
    return INCIDENT_STATES.Cancelled;
  }
  protected resolveBy(i: Incident, c: Ctx) {
    agent(c);
    if (!c.comment?.trim()) throw validation('comment', 'Describe the resolution.');
    i.resolution = c.comment.trim();
    i.resolvedAt = c.now.toISOString();
    return INCIDENT_STATES.Resolved;
  }
}
class NewIncidentState extends IncidentState {
  readonly status = 'New';
  override take(i: Incident, c: Ctx) { agent(c); i.assigneeId = c.actor.id; return INCIDENT_STATES.InProgress; }
  override cancel(i: Incident, c: Ctx) { return this.cancelBy(i, c); }
}
class InProgressIncidentState extends IncidentState {
  readonly status = 'InProgress';
  override hold(_i: Incident, c: Ctx) {
    agent(c);
    if (!c.comment?.trim()) throw validation('comment', 'Say what the ticket is waiting for.');
    return INCIDENT_STATES.OnHold;
  }
  override resolve(i: Incident, c: Ctx) { return this.resolveBy(i, c); }
  override cancel(i: Incident, c: Ctx) { return this.cancelBy(i, c); }
}
class OnHoldState extends IncidentState {
  readonly status = 'OnHold';
  override resume(_i: Incident, c: Ctx) { agent(c); return INCIDENT_STATES.InProgress; }
  override resolve(i: Incident, c: Ctx) { return this.resolveBy(i, c); }
  override cancel(i: Incident, c: Ctx) { return this.cancelBy(i, c); }
}
class ResolvedState extends IncidentState {
  readonly status = 'Resolved';
  override reopen(i: Incident, c: Ctx) {
    if (c.actor.id !== i.reporterId && !isAdmin(c.actor)) throw forbidden('Only the reporter can reopen a ticket.');
    i.resolvedAt = null;
    i.resolution = null;
    return INCIDENT_STATES.InProgress;
  }
  override close(i: Incident, c: Ctx) {
    if (c.actor === SYSTEM && i.resolvedAt && Date.parse(i.resolvedAt) + AUTO_CLOSE_DAYS * 86_400_000 > c.now.getTime())
      throw rule('The ticket was resolved too recently to close automatically.', 'incident.too_recent');
    if (c.actor !== SYSTEM && c.actor.id !== i.reporterId && !isAdmin(c.actor)) throw forbidden('Only the reporter can confirm and close a ticket.');
    return INCIDENT_STATES.Closed;
  }
}
class EndIncidentState extends IncidentState {
  constructor(readonly status: IncidentStatus) { super(); }
}

export const INCIDENT_STATES = {
  New: new NewIncidentState() as IncidentState,
  InProgress: new InProgressIncidentState() as IncidentState,
  OnHold: new OnHoldState() as IncidentState,
  Resolved: new ResolvedState() as IncidentState,
  Closed: new EndIncidentState('Closed') as IncidentState,
  Cancelled: new EndIncidentState('Cancelled') as IncidentState,
} satisfies Record<IncidentStatus, IncidentState>;

export function incidentAction(i: Incident, action: string, ctx: Ctx) {
  const s = INCIDENT_STATES[i.status];
  const calls: Record<string, () => IncidentState> = {
    take: () => s.take(i, ctx), hold: () => s.hold(i, ctx), resume: () => s.resume(i, ctx), resolve: () => s.resolve(i, ctx),
    reopen: () => s.reopen(i, ctx), close: () => s.close(i, ctx), cancel: () => s.cancel(i, ctx),
  };
  const run = calls[action] ?? (() => { throw validation('action', `Unknown action '${action}'.`); });
  const to = run();
  record(i.history, action, i.status, to.status, ctx);
  i.status = to.status as IncidentStatus;
}
