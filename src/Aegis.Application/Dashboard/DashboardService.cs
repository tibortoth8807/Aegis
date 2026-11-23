using Aegis.Application.Access;
using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Dashboard;

public sealed record Me(int Id, string Name, string EmployeeNumber, string JobTitle, string[] Roles);

/// <summary>The counters in the menu and on the start page, for the current user.</summary>
public sealed record Counters(
    int MyOpenRequests, int AwaitingMyApproval, int MyOpenTickets, int TicketQueue, int MyTrainingsDue, int TeamTrainingsOverdue,
    int ActiveEmployees, int Joiners, int Leavers, int Externals, int ActiveGrants, int PrivilegedExpiringSoon);

public sealed class DashboardService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    public async Task<Me> MeAsync(CancellationToken ct)
    {
        var id = user.Actor.EmployeeId;
        var e = (await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Employee", id);
        return new Me(e.Id, e.FullName, e.EmployeeNumber, e.JobTitle, EmployeeService.RoleNames(e.PortalRoles));
    }

    public async Task<Counters> CountersAsync(CancellationToken ct)
    {
        var me = user.Actor;
        var id = me.EmployeeId;
        var now = time.GetUtcNow();
        var staff = me.IsAdmin || me.Is(PortalRole.Hr) || me.Is(PortalRole.SecurityOfficer);
        var agent = me.IsAdmin || me.Is(PortalRole.Hr);

        var requests = db.AccessRequests.AsNoTracking();
        var myOpen = await requests.CountAsync(r => (r.RequesterId == id || r.BeneficiaryId == id) && (r.Status == AccessRequestStatus.Draft
            || r.Status == AccessRequestStatus.PendingManager || r.Status == AccessRequestStatus.PendingOwner
            || r.Status == AccessRequestStatus.PendingSecurity || r.Status == AccessRequestStatus.Approved), ct);
        var awaiting = await AccessRequestService.AwaitingDecision(requests, me).CountAsync(ct);

        var myTickets = await db.Incidents.AsNoTracking().CountAsync(i => i.ReporterId == id
            && (i.Status == IncidentStatus.New || i.Status == IncidentStatus.InProgress || i.Status == IncidentStatus.OnHold), ct);
        var queue = agent ? await db.Incidents.AsNoTracking().CountAsync(i =>
            i.Status == IncidentStatus.New || i.Status == IncidentStatus.InProgress || i.Status == IncidentStatus.OnHold, ct) : 0;

        var myTrainings = await db.Assignments.AsNoTracking().CountAsync(a => a.EmployeeId == id && (a.Status == AssignmentStatus.Assigned
            || a.Status == AssignmentStatus.InProgress || a.Status == AssignmentStatus.Overdue || a.Status == AssignmentStatus.Expired), ct);
        var teamOverdue = await db.Assignments.AsNoTracking().CountAsync(a => a.Employee!.ManagerId == id
            && (a.Status == AssignmentStatus.Overdue || a.Status == AssignmentStatus.Expired), ct);

        int active = 0, joiners = 0, leavers = 0, externals = 0, grants = 0, expiring = 0;
        if (staff)
        {
            var people = db.Employees.AsNoTracking();
            active = await people.CountAsync(e => e.Status == EmployeeStatus.Active || e.Status == EmployeeStatus.OnLeave, ct);
            joiners = await people.CountAsync(e => e.Status == EmployeeStatus.Preboarding, ct);
            leavers = await people.CountAsync(e => e.Status == EmployeeStatus.Offboarding, ct);
            externals = await people.CountAsync(e => e.Type == EmploymentType.External && e.Status != EmployeeStatus.Left, ct);
            grants = await db.Grants.AsNoTracking().CountAsync(g => g.EndedAt == null && g.ValidUntil > now, ct);
            var soon = now.AddDays(14);
            expiring = await db.Grants.AsNoTracking().CountAsync(g => g.EndedAt == null && g.ValidUntil > now && g.ValidUntil <= soon && g.Role!.Privileged, ct);
        }
        return new Counters(myOpen, awaiting, myTickets, queue, myTrainings, teamOverdue, active, joiners, leavers, externals, grants, expiring);
    }
}
