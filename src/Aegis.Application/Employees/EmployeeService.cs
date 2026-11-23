using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Employees;

public sealed record EmployeeListItem(
    int Id, string EmployeeNumber, string FullName, string Email, string JobTitle, string Type, string Status,
    string? Team, string? Location, string? Manager, string? Nationality, DateOnly StartDate, DateOnly? EndDate);

public sealed record EmployeeFilter(
    string? Status = null, string? Type = null, int? TeamId = null, int? LocationId = null, int? ManagerId = null, string? Nationality = null);

public sealed record PersonRef(int Id, string Name, string EmployeeNumber);

public sealed record GrantDto(int Id, string System, string Role, bool Privileged, DateTimeOffset GrantedAt, DateTimeOffset ValidUntil, int RequestId);

public sealed record AssignmentBrief(int Id, string Course, string Status, DateOnly DueOn, DateOnly? ValidUntil);

public sealed record HistoryDto(DateTimeOffset At, string Actor, string Action, string From, string To, string? Comment);

public sealed record EmployeeDetail(
    int Id, string EmployeeNumber, string FirstName, string LastName, string Email, string JobTitle, string Type, string? ExternalCompany,
    string Status, string? NationalityCode, int? LocationId, string? Location, int? TeamId, string? Team, PersonRef? Manager,
    DateOnly StartDate, DateOnly? EndDate, string[] PortalRoles, string Source, int Version,
    IReadOnlyList<PersonRef> DirectReports, IReadOnlyList<GrantDto> Access, IReadOnlyList<AssignmentBrief> Trainings,
    IReadOnlyList<HistoryDto> History, IReadOnlyList<string> AllowedActions);

/// <summary>Create or update payload. <see cref="Version"/> is required for updates (optimistic concurrency).</summary>
public sealed record EmployeeInput(
    string EmployeeNumber, string FirstName, string LastName, string Email, string JobTitle, string Type, string? ExternalCompany,
    string? NationalityCode, int? LocationId, int? TeamId, int? ManagerId, DateOnly StartDate, DateOnly? EndDate,
    string[]? PortalRoles = null, int? Version = null);

public sealed record EmployeeTransition(string Action, DateOnly? EndDate = null, string? Comment = null);

public sealed class EmployeeService(IAegisDbContext db, ICurrentUser user, TimeProvider time, AccessRevoker revoker)
{
    private static readonly Dictionary<string, Action<Employee, EmployeeTransition, TransitionContext>> Actions = new()
    {
        ["activate"] = (e, _, c) => e.Activate(c),
        ["start-leave"] = (e, _, c) => e.StartLeave(c),
        ["return"] = (e, _, c) => e.ReturnFromLeave(c),
        ["give-notice"] = (e, t, c) => e.GiveNotice(t.EndDate ?? throw ValidationFailedException.For("endDate", "Required."), c),
        ["withdraw-notice"] = (e, _, c) => e.WithdrawNotice(c),
        ["leave"] = (e, _, c) => e.Leave(c),
    };

    public async Task<Page<EmployeeListItem>> ListAsync(PageQuery q, EmployeeFilter f, CancellationToken ct)
    {
        var query = db.Employees.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(e => (e.FirstName + " " + e.LastName).ToLower().Contains(s) || e.Email.ToLower().Contains(s)
                || e.EmployeeNumber.ToLower().Contains(s) || e.JobTitle.ToLower().Contains(s));
        }
        if (Enum.TryParse<EmployeeStatus>(f.Status, true, out var status)) query = query.Where(e => e.Status == status);
        if (Enum.TryParse<EmploymentType>(f.Type, true, out var type)) query = query.Where(e => e.Type == type);
        if (f.TeamId is int team) query = query.Where(e => e.TeamId == team);
        if (f.LocationId is int loc) query = query.Where(e => e.LocationId == loc);
        if (f.ManagerId is int mgr) query = query.Where(e => e.ManagerId == mgr);
        if (!string.IsNullOrWhiteSpace(f.Nationality)) query = query.Where(e => e.NationalityCode == f.Nationality);

        query = (q.Sort?.ToLowerInvariant(), q.Desc) switch
        {
            ("number", false) => query.OrderBy(e => e.EmployeeNumber),
            ("number", true) => query.OrderByDescending(e => e.EmployeeNumber),
            ("title", false) => query.OrderBy(e => e.JobTitle),
            ("title", true) => query.OrderByDescending(e => e.JobTitle),
            ("start", false) => query.OrderBy(e => e.StartDate),
            ("start", true) => query.OrderByDescending(e => e.StartDate),
            ("status", false) => query.OrderBy(e => e.Status),
            ("status", true) => query.OrderByDescending(e => e.Status),
            (_, true) => query.OrderByDescending(e => e.LastName).ThenByDescending(e => e.FirstName),
            _ => query.OrderBy(e => e.LastName).ThenBy(e => e.FirstName),
        };

        var total = await query.CountAsync(ct);
        var items = await query.Skip(q.Skip).Take(q.Size)
            .Select(e => new EmployeeListItem(
                e.Id, e.EmployeeNumber, e.FirstName + " " + e.LastName, e.Email, e.JobTitle, e.Type.ToString(), e.Status.ToString(),
                e.Team == null ? null : e.Team.Name, e.Location == null ? null : e.Location.Name,
                e.Manager == null ? null : e.Manager.FirstName + " " + e.Manager.LastName, e.NationalityCode, e.StartDate, e.EndDate))
            .ToListAsync(ct);
        return new Page<EmployeeListItem>(items, total, q.Page, q.Size);
    }

    public async Task<EmployeeDetail> GetAsync(int id, CancellationToken ct)
    {
        var e = (await db.Employees.AsNoTracking()
            .Include(x => x.Manager).Include(x => x.Team).Include(x => x.Location)
            .FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Employee", id);
        var now = time.GetUtcNow();
        var reports = await db.Employees.AsNoTracking().Where(x => x.ManagerId == id && x.Status != EmployeeStatus.Left)
            .OrderBy(x => x.LastName).Select(x => new PersonRef(x.Id, x.FirstName + " " + x.LastName, x.EmployeeNumber)).ToListAsync(ct);
        var grants = await db.Grants.AsNoTracking().Where(g => g.EmployeeId == id && g.EndedAt == null && g.ValidUntil > now)
            .OrderBy(g => g.Role!.System!.Name)
            .Select(g => new GrantDto(g.Id, g.Role!.System!.Name, g.Role.Name, g.Role.Privileged, g.GrantedAt, g.ValidUntil, g.RequestId))
            .ToListAsync(ct);
        var trainings = await db.Assignments.AsNoTracking().Where(a => a.EmployeeId == id).OrderBy(a => a.DueOn)
            .Select(a => new AssignmentBrief(a.Id, a.Course!.Title, a.Status.ToString(), a.DueOn, a.ValidUntil)).ToListAsync(ct);
        return ToDetail(e, reports, grants, trainings);
    }

    private EmployeeDetail ToDetail(Employee e, IReadOnlyList<PersonRef> reports, IReadOnlyList<GrantDto> grants, IReadOnlyList<AssignmentBrief> trainings)
    {
        var canManage = user.Actor.IsAdmin || user.Actor.Is(PortalRole.Hr);
        return new EmployeeDetail(
            e.Id, e.EmployeeNumber, e.FirstName, e.LastName, e.Email, e.JobTitle, e.Type.ToString(), e.ExternalCompany, e.Status.ToString(),
            e.NationalityCode, e.LocationId, e.Location?.Name, e.TeamId, e.Team?.Name,
            e.Manager is null ? null : new PersonRef(e.Manager.Id, e.Manager.FullName, e.Manager.EmployeeNumber),
            e.StartDate, e.EndDate, RoleNames(e.PortalRoles), e.Source, e.Version, reports, grants, trainings,
            e.History.OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Select(h => new HistoryDto(h.At, h.ActorName, h.Action, h.From, h.To, h.Comment)).ToList(),
            canManage ? e.State.AllowedActions : Array.Empty<string>());
    }

    public static string[] RoleNames(PortalRole roles) =>
        Enum.GetValues<PortalRole>().Where(r => r != PortalRole.None && roles.HasFlag(r)).Select(r => r.ToString()).ToArray();

    public async Task<EmployeeDetail> CreateAsync(EmployeeInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var number = Guard.Required(input.EmployeeNumber, nameof(input.EmployeeNumber), 20).ToUpperInvariant();
        if (await db.Employees.AnyAsync(e => e.EmployeeNumber == number, ct))
            throw new BusinessRuleException($"Employee number {number} is already in use.", "employee.duplicate_number");
        var email = Guard.Required(input.Email, nameof(input.Email)).ToLowerInvariant();
        if (await db.Employees.AnyAsync(e => e.Email == email, ct))
            throw new BusinessRuleException($"The e-mail address {email} is already in use.", "employee.duplicate_email");

        var e = new Employee { EmployeeNumber = number, Email = email };
        await ApplyAsync(e, input, ct);
        Employee.Hire(e, time.Today(), user.Context(time));
        db.Employees.Add(e);

        // every new joiner gets the mandatory courses
        var today = time.Today();
        var onboarding = new TransitionContext(Actor.System with { Name = "Onboarding" }, time.GetUtcNow());
        foreach (var course in await db.Courses.Where(c => c.Mandatory).ToListAsync(ct))
            db.Assignments.Add(TrainingAssignment.Assign(e, course, today, onboarding));

        await db.SaveChangesAsync(ct);
        return await GetAsync(e.Id, ct);
    }

    public async Task<EmployeeDetail> UpdateAsync(int id, EmployeeInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var e = (await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Employee", id);
        if (input.Version is null || input.Version != e.Version)
            throw new ConcurrencyConflictException("Somebody else changed this employee in the meantime. Reload and try again.");
        var email = Guard.Required(input.Email, nameof(input.Email)).ToLowerInvariant();
        if (await db.Employees.AnyAsync(x => x.Email == email && x.Id != id, ct))
            throw new BusinessRuleException($"The e-mail address {email} is already in use.", "employee.duplicate_email");
        e.Email = email;
        await ApplyAsync(e, input, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task ApplyAsync(Employee e, EmployeeInput input, CancellationToken ct)
    {
        e.FirstName = Guard.Required(input.FirstName, nameof(input.FirstName), 80);
        e.LastName = Guard.Required(input.LastName, nameof(input.LastName), 80);
        e.JobTitle = Guard.Required(input.JobTitle, nameof(input.JobTitle), 120);
        e.Type = Enum.TryParse<EmploymentType>(input.Type, true, out var t) ? t : throw ValidationFailedException.For(nameof(input.Type), "Internal or External.");
        e.ExternalCompany = e.Type == EmploymentType.External ? input.ExternalCompany?.Trim() : null;
        e.NationalityCode = string.IsNullOrWhiteSpace(input.NationalityCode) ? null : input.NationalityCode.Trim().ToUpperInvariant();
        if (e.NationalityCode is { } nc && !await db.Countries.AnyAsync(c => c.Code == nc, ct))
            throw ValidationFailedException.For(nameof(input.NationalityCode), $"Unknown country code {nc}.");
        e.LocationId = input.LocationId;
        e.TeamId = input.TeamId;
        if (input.ManagerId is int mgr)
        {
            if (mgr == e.Id) throw ValidationFailedException.For(nameof(input.ManagerId), "Nobody is their own manager.");
            if (!await db.Employees.AnyAsync(x => x.Id == mgr && x.Status != EmployeeStatus.Left, ct))
                throw ValidationFailedException.For(nameof(input.ManagerId), "The manager does not exist or has left.");
            if (e.Id != 0 && await IsInChainAsync(mgr, e.Id, ct))
                throw new BusinessRuleException("This would create a loop in the reporting line.", "employee.manager_loop");
        }
        e.ManagerId = input.ManagerId;
        e.StartDate = input.StartDate;
        e.EndDate = input.EndDate;
        if (input.PortalRoles is { } roles && (user.Actor.IsAdmin))
            e.PortalRoles = roles.Select(r => Enum.TryParse<PortalRole>(r, true, out var pr) ? pr : PortalRole.None).Aggregate(PortalRole.Employee, (a, b) => a | b);
    }

    /// <summary>True if <paramref name="employeeId"/> appears in the management chain above <paramref name="startId"/>.</summary>
    private async Task<bool> IsInChainAsync(int startId, int employeeId, CancellationToken ct)
    {
        int? current = startId;
        for (var depth = 0; current is int c && depth < 50; depth++)
        {
            if (c == employeeId) return true;
            current = await db.Employees.Where(x => x.Id == c).Select(x => x.ManagerId).FirstOrDefaultAsync(ct);
        }
        return false;
    }

    public async Task<EmployeeDetail> TransitionAsync(int id, EmployeeTransition t, CancellationToken ct)
    {
        var e = (await db.Employees.FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Employee", id);
        var ctx = user.Context(time, t.Comment);
        // the action name only selects the domain method; whether it is allowed is the current state's decision
        var perform = Actions.GetValueOrDefault(t.Action) ?? throw ValidationFailedException.For("action", $"Unknown action '{t.Action}'.");
        perform(e, t, ctx);
        if (e.Status == EmployeeStatus.Left) await revoker.RevokeAllAsync(e, ctx, "Employee left the company", ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }
}
