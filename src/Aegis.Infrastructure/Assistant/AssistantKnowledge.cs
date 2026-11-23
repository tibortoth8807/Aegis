using System.Globalization;
using Aegis.Application.Access;
using Aegis.Application.Assistant;
using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.Training;
using Aegis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Assistant;

/// <summary>The assistant's view of the database - scoped to the current user, like the pages are.</summary>
public sealed class AssistantKnowledge(AegisDbContext db, ICurrentUser user, TimeProvider time) : IAssistantKnowledge
{
    private int Me => user.Actor.EmployeeId;

    private static string Human(object status) => Domain.Common.StateBase<AccessRequestStatus>.Humanize(status.ToString()!);

    public async Task<string> ProfileAsync(CancellationToken ct)
    {
        var e = await db.Employees.AsNoTracking().Include(x => x.Team).Include(x => x.Manager).FirstAsync(x => x.Id == Me, ct);
        return $"{e.FullName} ({e.EmployeeNumber}), {e.JobTitle}, team {e.Team?.Name ?? "-"}, manager {e.Manager?.FullName ?? "-"}, roles in the portal: {e.PortalRoles}.";
    }

    public async Task<(int Count, IReadOnlyList<string> Lines)> MyOpenRequestsAsync(CancellationToken ct)
    {
        var list = await db.AccessRequests.AsNoTracking()
            .Where(r => (r.RequesterId == Me || r.BeneficiaryId == Me) && (r.Status == AccessRequestStatus.Draft || r.Status == AccessRequestStatus.PendingManager
                || r.Status == AccessRequestStatus.PendingOwner || r.Status == AccessRequestStatus.PendingSecurity || r.Status == AccessRequestStatus.Approved))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Number, System = r.Role!.System!.Name, Role = r.Role.Name, r.Status }).ToListAsync(ct);
        return (list.Count, list.Take(8).Select(r => $"{r.Number} {r.System} / {r.Role} - {Human(r.Status)}").ToList());
    }

    public async Task<(int Count, IReadOnlyList<string> Lines)> AwaitingMeAsync(CancellationToken ct)
    {
        var list = await AccessRequestService.AwaitingDecision(db.AccessRequests.AsNoTracking(), user.Actor)
            .OrderBy(r => r.SubmittedAt)
            .Select(r => new { r.Number, System = r.Role!.System!.Name, Role = r.Role.Name, Who = r.Beneficiary!.FirstName + " " + r.Beneficiary.LastName })
            .ToListAsync(ct);
        return (list.Count, list.Take(8).Select(r => $"{r.Number} {r.Who}: {r.System} / {r.Role}").ToList());
    }

    public async Task<IReadOnlyList<string>> MyTrainingsDueAsync(CancellationToken ct) =>
        (await db.Assignments.AsNoTracking()
            .Where(a => a.EmployeeId == Me && (a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.InProgress
                || a.Status == AssignmentStatus.Overdue || a.Status == AssignmentStatus.Expired))
            .OrderBy(a => a.DueOn).Select(a => new { a.Course!.Title, a.DueOn, a.Status }).ToListAsync(ct))
        .Select(a => $"{a.Title} - due {a.DueOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{(a.Status is AssignmentStatus.Overdue or AssignmentStatus.Expired ? $" ({a.Status.ToString().ToLowerInvariant()})" : "")}")
        .ToList();

    public async Task<IReadOnlyList<string>> MyAccessAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return (await db.Grants.AsNoTracking().Where(g => g.EmployeeId == Me && g.EndedAt == null && g.ValidUntil > now)
                .Select(g => new { System = g.Role!.System!.Name, Role = g.Role.Name, g.ValidUntil }).ToListAsync(ct))
            .OrderBy(g => g.System)
            .Select(g => $"{g.System} / {g.Role} until {g.ValidUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}").ToList();
    }

    public async Task<IReadOnlyList<string>> MyOpenTicketsAsync(CancellationToken ct) =>
        (await db.Incidents.AsNoTracking()
            .Where(i => i.ReporterId == Me && (i.Status == IncidentStatus.New || i.Status == IncidentStatus.InProgress || i.Status == IncidentStatus.OnHold))
            .Select(i => new { i.Number, i.Title, i.Status }).ToListAsync(ct))
        .Select(i => $"{i.Number} {i.Title} - {Human(i.Status)}").ToList();

    /// <summary>Accent- and order-insensitive name search ("kiss anna" finds "Anna Kiss" and "Kiss Ánna").</summary>
    public async Task<IReadOnlyList<PersonHit>> FindPeopleAsync(string name, CancellationToken ct)
    {
        var words = IntentRecognizer.Fold(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        var people = await db.Employees.AsNoTracking().Where(e => e.Status != EmployeeStatus.Left)
            .Select(e => new
            {
                e.Id, e.FirstName, e.LastName, e.JobTitle, e.Email, Team = e.Team == null ? null : e.Team.Name,
                Manager = e.Manager == null ? null : e.Manager.FirstName + " " + e.Manager.LastName,
            }).ToListAsync(ct);
        return people
            .Where(p =>
            {
                var hay = IntentRecognizer.Fold($"{p.FirstName} {p.LastName}").Split(' ');
                return words.All(w => hay.Any(h => h.StartsWith(w, StringComparison.Ordinal)));
            })
            .OrderBy(p => p.LastName).Take(10)
            .Select(p => new PersonHit(p.Id, $"{p.FirstName} {p.LastName}", p.JobTitle, p.Team, p.Manager, p.Email)).ToList();
    }

    public async Task<IReadOnlyList<(string System, string Owner)>> FindSystemsAsync(string name, CancellationToken ct)
    {
        var folded = IntentRecognizer.Fold(name);
        var systems = await db.Systems.AsNoTracking()
            .Join(db.Employees, s => s.OwnerId, e => e.Id, (s, e) => new { s.Code, s.Name, Owner = e.FirstName + " " + e.LastName })
            .ToListAsync(ct);
        return systems.Where(s => IntentRecognizer.Fold(s.Code) == folded || IntentRecognizer.Fold(s.Name).Contains(folded, StringComparison.Ordinal))
            .Select(s => (s.Name, s.Owner)).ToList();
    }
}
