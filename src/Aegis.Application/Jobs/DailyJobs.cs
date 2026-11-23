using Aegis.Application.Access;
using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Jobs;

public sealed record JobReport(
    int Activated, int Left, int AccessExpired, int Provisioned, int TrainingsOverdue, int TrainingsExpired, int TicketsClosed)
{
    public int Total => Activated + Left + AccessExpired + Provisioned + TrainingsOverdue + TrainingsExpired + TicketsClosed;
}

/// <summary>
/// The time-driven transitions. Each step finds the entities whose date has come and asks their state to move on;
/// the states still check the rules, so running the job twice changes nothing the second time.
/// </summary>
public sealed class DailyJobs(
    IAegisDbContext db, TimeProvider time, AccessProvisioner provisioner, AccessRevoker revoker, ILogger<DailyJobs> log)
{
    public async Task<JobReport> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = time.Today();
        var ctx = new TransitionContext(Actor.System, now);

        var joiners = await db.Employees.Where(e => e.Status == EmployeeStatus.Preboarding && e.StartDate <= today).ToListAsync(ct);
        foreach (var e in joiners) e.Activate(ctx);

        var leavers = await db.Employees.Where(e => e.Status == EmployeeStatus.Offboarding && e.EndDate < today).ToListAsync(ct);
        foreach (var e in leavers)
        {
            e.Leave(ctx);
            await revoker.RevokeAllAsync(e, ctx, "Employee left the company", ct);
        }

        var expiring = await db.AccessRequests.Include(r => r.Beneficiary).Include(r => r.Role).ThenInclude(r => r!.System)
            .Where(r => r.Status == AccessRequestStatus.Active && r.ValidUntil <= now).ToListAsync(ct);
        foreach (var r in expiring) await revoker.RevokeAsync(r, ctx with { Comment = "Validity ended" }, ct, expire: true);

        await db.SaveChangesAsync(ct);
        var provisioned = await provisioner.ProvisionPendingAsync(ct);

        var overdue = await db.Assignments.Where(a => (a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.InProgress) && a.DueOn < today)
            .ToListAsync(ct);
        foreach (var a in overdue) a.MarkOverdue(ctx);

        var lapsed = await db.Assignments.Where(a => a.Status == AssignmentStatus.Completed && a.ValidUntil < today).ToListAsync(ct);
        foreach (var a in lapsed) a.Expire(ctx);

        var closeBefore = now.AddDays(-Incident.AutoCloseDays);
        var resolved = await db.Incidents.Where(i => i.Status == IncidentStatus.Resolved && i.ResolvedAt <= closeBefore).ToListAsync(ct);
        foreach (var i in resolved) i.Close(ctx with { Comment = $"Closed automatically {Incident.AutoCloseDays} days after resolution" });

        await db.SaveChangesAsync(ct);
        var report = new JobReport(joiners.Count, leavers.Count, expiring.Count, provisioned, overdue.Count, lapsed.Count, resolved.Count);
        log.LogInformation("Daily jobs: {@Report}", report);
        return report;
    }
}
