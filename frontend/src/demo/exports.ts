import { DownloadedFile, ExportInfo } from '../api/types';
import { dateOnly, fullName, isValidCompletion } from './domain';
import { csv, Table, xlsx } from './files';
import { Db } from './store';

export const EXPORTS: ExportInfo[] = [
  { key: 'employees', title: 'Employees', description: 'Everyone who has not left, with team, manager and location', roles: ['Hr', 'Admin'] },
  { key: 'managers', title: 'Managers', description: 'People managers with the number of direct reports', roles: ['Hr', 'Admin'] },
  { key: 'admins', title: 'Privileged access', description: 'Who holds a privileged role in which application, and until when', roles: ['SecurityOfficer', 'Admin'] },
  { key: 'access', title: 'Access report', description: 'All active access grants', roles: ['SecurityOfficer', 'Admin'] },
  { key: 'training', title: 'Training compliance', description: 'Every assignment with status, due date and validity', roles: ['Hr', 'Admin'] },
];

function table(db: Db, key: string, now: Date): Table {
  const emp = (id: number | null) => db.employees.find((e) => e.id === id);
  const live = db.employees.filter((e) => e.status !== 'Left');
  const byName = (a: { lastName: string; firstName: string }, b: { lastName: string; firstName: string }) => a.lastName.localeCompare(b.lastName, 'hu') || a.firstName.localeCompare(b.firstName, 'hu');
  switch (key) {
    case 'employees':
      return {
        title: 'Employees',
        columns: ['Number', 'Last name', 'First name', 'E-mail', 'Job title', 'Type', 'Status', 'Team', 'Manager', 'Location', 'Nationality', 'Start', 'End'],
        rows: [...live].sort(byName).map((e) => [e.employeeNumber, e.lastName, e.firstName, e.email, e.jobTitle, e.type, e.status,
          db.teams.find((t) => t.id === e.teamId)?.name, emp(e.managerId) ? fullName(emp(e.managerId)!) : null,
          db.locations.find((l) => l.id === e.locationId)?.name, e.nationalityCode, e.startDate, e.endDate]),
      };
    case 'managers':
      return {
        title: 'Managers',
        columns: ['Number', 'Name', 'E-mail', 'Job title', 'Team', 'Direct reports'],
        rows: live.filter((m) => live.some((e) => e.managerId === m.id)).sort(byName)
          .map((m) => [m.employeeNumber, fullName(m), m.email, m.jobTitle, db.teams.find((t) => t.id === m.teamId)?.name, live.filter((e) => e.managerId === m.id).length]),
      };
    case 'admins':
    case 'access': {
      const n = now.toISOString();
      const grants = db.grants.filter((g) => !g.endedAt && g.validUntil > n).map((g) => {
        const role = db.roles.find((r) => r.id === g.roleId)!;
        return { g, role, system: db.systems.find((s) => s.id === role.systemId)!, e: emp(g.employeeId)! };
      }).filter((x) => key === 'access' || x.role.privileged)
        .sort((a, b) => a.system.name.localeCompare(b.system.name) || a.e.lastName.localeCompare(b.e.lastName, 'hu'));
      return {
        title: key === 'access' ? 'Access report' : 'Privileged access',
        columns: ['Application', 'Role', 'Privileged', 'Number', 'Name', 'Employee status', 'Granted', 'Valid until', 'Request'],
        rows: grants.map((x) => [x.system.name, x.role.name, x.role.privileged, x.e.employeeNumber, fullName(x.e), x.e.status,
          x.g.grantedAt.slice(0, 16).replace('T', ' '), x.g.validUntil.slice(0, 16).replace('T', ' '), `REQ-${String(x.g.requestId).padStart(6, '0')}`]),
      };
    }
    default: {
      const today = dateOnly(now);
      const rows = db.assignments.map((a) => ({ a, c: db.courses.find((c) => c.id === a.courseId)!, e: emp(a.employeeId)! }))
        .sort((x, y) => x.c.code.localeCompare(y.c.code) || x.e.lastName.localeCompare(y.e.lastName, 'hu'));
      return {
        title: 'Training compliance',
        columns: ['Course', 'Title', 'Number', 'Name', 'Status', 'Due', 'Completed', 'Valid until', 'Score', 'Compliance'],
        rows: rows.map(({ a, c, e }) => [c.code, c.title, e.employeeNumber, fullName(e), a.status, a.dueOn, a.completedOn, a.validUntil, a.score,
          isValidCompletion(a, today) ? 'compliant' : a.status === 'Waived' ? 'waived' : 'not compliant']),
      };
    }
  }
}

export function buildExport(db: Db, key: string, format: 'xlsx' | 'csv', now: Date): DownloadedFile {
  const t = table(db, key, now);
  const stamp = now.toISOString().slice(0, 16).replace(/[-:]/g, '').replace('T', '-');
  return format === 'csv'
    ? { fileName: `aegis-${key}-${stamp}.csv`, contentType: 'text/csv; charset=utf-8', bytes: csv(t) }
    : { fileName: `aegis-${key}-${stamp}.xlsx`, contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', bytes: xlsx(t) };
}
