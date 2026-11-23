using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Training;

public sealed record CourseDto(
    int Id, string Code, string Title, string Description, bool Mandatory, int DueDays, int? ValidityMonths, int PassScore,
    int Assigned, int Completed, int Overdue);

public sealed record AssignmentDto(
    int Id, PersonRef Employee, int CourseId, string Course, string Status, DateOnly AssignedOn, DateOnly DueOn, DateOnly? CompletedOn,
    DateOnly? ValidUntil, int? Score, int Attempts, IReadOnlyList<string> AllowedActions);

public sealed record AssignInput(int CourseId, int[] EmployeeIds);

public sealed record AssignmentTransition(string Action, int? Score = null, string? Comment = null);

public sealed record CourseInput(string Code, string Title, string? Description, bool Mandatory, int DueDays, int? ValidityMonths, int PassScore);

public sealed class TrainingService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    private static readonly Dictionary<string, Action<TrainingAssignment, AssignmentTransition, TransitionContext>> Actions = new()
    {
        ["start"] = (a, _, c) => a.Start(c),
        ["complete"] = (a, t, c) => a.Complete(t.Score ?? throw ValidationFailedException.For("score", "Required."), c),
        ["waive"] = (a, _, c) => a.Waive(c),
    };

    public async Task<IReadOnlyList<CourseDto>> CoursesAsync(CancellationToken ct)
    {
        var stats = await db.Assignments.AsNoTracking().GroupBy(a => a.CourseId).Select(g => new
        {
            g.Key,
            Assigned = g.Count(a => a.Status == AssignmentStatus.Assigned || a.Status == AssignmentStatus.InProgress),
            Completed = g.Count(a => a.Status == AssignmentStatus.Completed),
            Overdue = g.Count(a => a.Status == AssignmentStatus.Overdue || a.Status == AssignmentStatus.Expired),
        }).ToDictionaryAsync(x => x.Key, ct);
        return (await db.Courses.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct)).Select(c =>
        {
            var s = stats.GetValueOrDefault(c.Id);
            return new CourseDto(c.Id, c.Code, c.Title, c.Description, c.Mandatory, c.DueDays, c.ValidityMonths, c.PassScore,
                s?.Assigned ?? 0, s?.Completed ?? 0, s?.Overdue ?? 0);
        }).ToList();
    }

    public async Task<CourseDto> SaveCourseAsync(int? id, CourseInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var code = Guard.Required(input.Code, nameof(input.Code), 20).ToUpperInvariant();
        if (await db.Courses.AnyAsync(c => c.Code == code && c.Id != (id ?? 0), ct))
            throw new BusinessRuleException($"The course code {code} is already used.", "course.duplicate_code");
        if (input.PassScore is < 0 or > 100) throw ValidationFailedException.For(nameof(input.PassScore), "0-100.");
        if (input.DueDays is < 1 or > 365) throw ValidationFailedException.For(nameof(input.DueDays), "1-365 days.");
        var c = id is int cid ? (await db.Courses.FirstOrDefaultAsync(x => x.Id == cid, ct)).OrNotFound("Course", cid) : new Course();
        c.Code = code;
        c.Title = Guard.Required(input.Title, nameof(input.Title), 150);
        c.Description = input.Description?.Trim() ?? "";
        c.Mandatory = input.Mandatory;
        c.DueDays = input.DueDays;
        c.ValidityMonths = input.ValidityMonths;
        c.PassScore = input.PassScore;
        if (id is null) db.Courses.Add(c);
        await db.SaveChangesAsync(ct);
        return (await CoursesAsync(ct)).First(x => x.Id == c.Id);
    }

    /// <summary>"mine" for the current user; "all" (HR) with an optional status / course filter.</summary>
    public async Task<Page<AssignmentDto>> AssignmentsAsync(PageQuery q, string scope, string? status, int? courseId, CancellationToken ct)
    {
        var me = user.Actor;
        var query = db.Assignments.AsNoTracking().Include(a => a.Employee).Include(a => a.Course).AsQueryable();
        if (scope == "all") user.RequireAny(PortalRole.Hr);
        else if (scope == "team") query = query.Where(a => a.Employee!.ManagerId == me.EmployeeId);
        else query = query.Where(a => a.EmployeeId == me.EmployeeId);
        if (Enum.TryParse<AssignmentStatus>(status, true, out var st)) query = query.Where(a => a.Status == st);
        if (courseId is int cid) query = query.Where(a => a.CourseId == cid);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(a => (a.Employee!.FirstName + " " + a.Employee.LastName).ToLower().Contains(s) || a.Course!.Title.ToLower().Contains(s));
        }
        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(a => a.Status == AssignmentStatus.Completed || a.Status == AssignmentStatus.Waived).ThenBy(a => a.DueOn)
            .Skip(q.Skip).Take(q.Size).ToListAsync(ct);
        return new Page<AssignmentDto>(page.Select(ToDto).ToList(), total, q.Page, q.Size);
    }

    private AssignmentDto ToDto(TrainingAssignment a)
    {
        var me = user.Actor;
        var mine = a.EmployeeId == me.EmployeeId;
        var hr = me.IsAdmin || me.Is(PortalRole.Hr);
        var actions = a.State.AllowedActions.Where(x => x switch
        {
            "start" or "complete" => mine || hr,
            "waive" => hr,
            _ => false,
        }).ToList();
        return new AssignmentDto(
            a.Id, new PersonRef(a.EmployeeId, a.Employee!.FullName, a.Employee.EmployeeNumber), a.CourseId, a.Course!.Title, a.Status.ToString(),
            a.AssignedOn, a.DueOn, a.CompletedOn, a.ValidUntil, a.Score, a.Attempts, actions);
    }

    /// <summary>Assigns a course to people; those who already have an open or valid assignment are skipped.</summary>
    public async Task<int> AssignAsync(AssignInput input, CancellationToken ct)
    {
        var me = user.Actor;
        var course = (await db.Courses.FirstOrDefaultAsync(c => c.Id == input.CourseId, ct)).OrNotFound("Course", input.CourseId);
        var people = await db.Employees.Where(e => input.EmployeeIds.Contains(e.Id) && e.Status != EmployeeStatus.Left).ToListAsync(ct);
        if (!me.IsAdmin && !me.Is(PortalRole.Hr) && people.Any(p => p.ManagerId != me.EmployeeId))
            throw new ForbiddenException("Managers can assign courses to their direct reports only.");
        var busy = await db.Assignments.Where(a => a.CourseId == course.Id && input.EmployeeIds.Contains(a.EmployeeId) &&
                a.Status != AssignmentStatus.Expired && a.Status != AssignmentStatus.Waived)
            .Select(a => a.EmployeeId).ToListAsync(ct);
        var ctx = user.Context(time);
        var count = 0;
        foreach (var p in people.Where(p => !busy.Contains(p.Id)))
        {
            db.Assignments.Add(TrainingAssignment.Assign(p, course, time.Today(), ctx));
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    public async Task<AssignmentDto> TransitionAsync(int id, AssignmentTransition t, CancellationToken ct)
    {
        var a = (await db.Assignments.Include(x => x.Employee).Include(x => x.Course).FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Assignment", id);
        var perform = Actions.GetValueOrDefault(t.Action) ?? throw ValidationFailedException.For("action", $"Unknown action '{t.Action}'.");
        perform(a, t, user.Context(time, t.Comment));
        await db.SaveChangesAsync(ct);
        return ToDto(a);
    }
}
