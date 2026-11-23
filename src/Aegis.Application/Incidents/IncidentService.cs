using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Domain.Common;
using Aegis.Domain.Incidents;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Incidents;

public sealed record IncidentListItem(
    int Id, string Number, string Title, string Priority, string Category, string Status, string Reporter, string? Assignee,
    DateTimeOffset CreatedAt, DateTimeOffset DueAt, bool Breached);

public sealed record IncidentDetail(
    int Id, string Number, string Title, string Description, string Priority, string Category, string Status, PersonRef Reporter,
    PersonRef? Assignee, DateTimeOffset CreatedAt, DateTimeOffset DueAt, DateTimeOffset? ResolvedAt, string? Resolution,
    IReadOnlyList<HistoryDto> History, IReadOnlyList<string> AllowedActions);

public sealed record NewIncident(string Title, string? Description, string Priority, string Category);

public sealed record IncidentTransition(string Action, string? Comment = null);

public sealed class IncidentService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    public static readonly string[] Categories = ["access", "employee-data", "training", "other"];

    private static readonly Dictionary<string, Action<Incident, TransitionContext>> Actions = new()
    {
        ["take"] = (i, c) => i.Take(c),
        ["hold"] = (i, c) => i.Hold(c),
        ["resume"] = (i, c) => i.Resume(c),
        ["resolve"] = (i, c) => i.Resolve(c),
        ["reopen"] = (i, c) => i.Reopen(c),
        ["close"] = (i, c) => i.Close(c),
        ["cancel"] = (i, c) => i.Cancel(c),
    };

    private bool IsAgent => user.Actor.IsAdmin || user.Actor.Is(PortalRole.Hr);

    /// <summary>"mine" = reported by me; "queue" = open tickets for the support team (agents only).</summary>
    public async Task<Page<IncidentListItem>> ListAsync(PageQuery q, string scope, string? status, CancellationToken ct)
    {
        var me = user.Actor.EmployeeId;
        var query = db.Incidents.AsNoTracking().AsQueryable();
        if (scope == "queue")
        {
            if (!IsAgent) throw new ForbiddenException("The ticket queue is for the support team.");
        }
        else query = query.Where(i => i.ReporterId == me);
        if (status == "open") query = query.Where(i => i.Status == IncidentStatus.New || i.Status == IncidentStatus.InProgress || i.Status == IncidentStatus.OnHold);
        else if (Enum.TryParse<IncidentStatus>(status, true, out var st)) query = query.Where(i => i.Status == st);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(i => i.Number.ToLower().Contains(s) || i.Title.ToLower().Contains(s));
        }
        var total = await query.CountAsync(ct);
        var list = await query.OrderBy(i => i.Priority).ThenBy(i => i.CreatedAt).Skip(q.Skip).Take(q.Size)
            .Select(i => new
            {
                i.Id, i.Number, i.Title, i.Priority, i.Category, i.Status, i.CreatedAt,
                Reporter = i.Reporter!.FirstName + " " + i.Reporter.LastName,
                Assignee = i.Assignee == null ? null : i.Assignee.FirstName + " " + i.Assignee.LastName,
            }).ToListAsync(ct);
        var now = time.GetUtcNow();
        var items = list.Select(i =>
        {
            var due = i.CreatedAt.AddHours(Incident.SlaHours(i.Priority));
            var open = i.Status is IncidentStatus.New or IncidentStatus.InProgress or IncidentStatus.OnHold;
            return new IncidentListItem(i.Id, i.Number, i.Title, i.Priority.ToString(), i.Category, i.Status.ToString(), i.Reporter, i.Assignee,
                i.CreatedAt, due, open && due < now);
        }).ToList();
        return new Page<IncidentListItem>(items, total, q.Page, q.Size);
    }

    public async Task<IncidentDetail> GetAsync(int id, CancellationToken ct)
    {
        var i = (await db.Incidents.AsNoTracking().Include(x => x.Reporter).Include(x => x.Assignee).FirstOrDefaultAsync(x => x.Id == id, ct))
            .OrNotFound("Ticket", id);
        if (i.ReporterId != user.Actor.EmployeeId && !IsAgent) throw new ForbiddenException("You can only see your own tickets.");
        var me = user.Actor;
        var actions = i.State.AllowedActions.Where(a => a switch
        {
            "take" or "hold" or "resume" or "resolve" => IsAgent,
            "reopen" or "close" or "cancel" => i.ReporterId == me.EmployeeId || me.IsAdmin,
            _ => false,
        }).ToList();
        return new IncidentDetail(
            i.Id, i.Number, i.Title, i.Description, i.Priority.ToString(), i.Category, i.Status.ToString(),
            new PersonRef(i.ReporterId, i.Reporter!.FullName, i.Reporter.EmployeeNumber),
            i.Assignee is null ? null : new PersonRef(i.Assignee.Id, i.Assignee.FullName, i.Assignee.EmployeeNumber),
            i.CreatedAt, i.DueAt, i.ResolvedAt, i.Resolution,
            i.History.OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Select(h => new HistoryDto(h.At, h.ActorName, h.Action, h.From, h.To, h.Comment)).ToList(),
            actions);
    }

    public async Task<IncidentDetail> CreateAsync(NewIncident input, CancellationToken ct)
    {
        var reporter = (await db.Employees.FirstOrDefaultAsync(e => e.Id == user.Actor.EmployeeId, ct)).OrNotFound("Employee", user.Actor.EmployeeId);
        if (!Enum.TryParse<IncidentPriority>(input.Priority, true, out var priority)) throw ValidationFailedException.For(nameof(input.Priority), "P1-P4.");
        if (!Categories.Contains(input.Category)) throw ValidationFailedException.For(nameof(input.Category), $"One of: {string.Join(", ", Categories)}.");
        var i = Incident.Open(reporter, Guard.Required(input.Title, nameof(input.Title), 150), input.Description ?? "", priority, input.Category, user.Context(time));
        db.Incidents.Add(i);
        await db.SaveChangesAsync(ct);
        i.Number = $"INC-{i.Id:000000}";
        await db.SaveChangesAsync(ct);
        return await GetAsync(i.Id, ct);
    }

    public async Task<IncidentDetail> TransitionAsync(int id, IncidentTransition t, CancellationToken ct)
    {
        var i = (await db.Incidents.FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Ticket", id);
        var perform = Actions.GetValueOrDefault(t.Action) ?? throw ValidationFailedException.For("action", $"Unknown action '{t.Action}'.");
        perform(i, user.Context(time, t.Comment));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }
}
