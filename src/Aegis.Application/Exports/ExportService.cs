using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Exports;

public sealed record ExportInfo(string Key, string Title, string Description, string[] Roles);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Formatted list exports ("Export lists" in the UI). Each export is a query that produces a <see cref="Table"/>;
/// the table is then written as Excel or CSV - adding an export means adding one entry here.
/// </summary>
public sealed class ExportService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    private sealed record Definition(ExportInfo Info, Func<ExportService, CancellationToken, Task<Table>> Build);

    private static readonly Definition[] Definitions =
    [
        new(new("employees", "Employees", "Everyone who has not left, with team, manager and location", ["Hr", "Admin"]), (s, ct) => s.EmployeesAsync(ct)),
        new(new("managers", "Managers", "People managers with the number of direct reports", ["Hr", "Admin"]), (s, ct) => s.ManagersAsync(ct)),
        new(new("admins", "Privileged access", "Who holds a privileged role in which application, and until when", ["SecurityOfficer", "Admin"]), (s, ct) => s.PrivilegedAsync(ct)),
        new(new("access", "Access report", "All active access grants", ["SecurityOfficer", "Admin"]), (s, ct) => s.AccessAsync(ct)),
        new(new("training", "Training compliance", "Every assignment with status, due date and validity", ["Hr", "Admin"]), (s, ct) => s.TrainingAsync(ct)),
    ];

    public IReadOnlyList<ExportInfo> Available() =>
        Definitions.Select(d => d.Info).Where(i => user.Actor.IsAdmin || i.Roles.Any(r => Enum.TryParse<PortalRole>(r, out var pr) && user.Actor.Is(pr))).ToList();

    public async Task<ExportFile> ExportAsync(string key, string format, CancellationToken ct)
    {
        var def = Definitions.FirstOrDefault(d => d.Info.Key == key) ?? throw new NotFoundException("Export", key);
        if (!Available().Contains(def.Info)) throw new ForbiddenException($"The {def.Info.Title} export needs one of the roles: {string.Join(", ", def.Info.Roles)}.");
        var table = await def.Build(this, ct);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        return format switch
        {
            "csv" => new ExportFile($"aegis-{key}-{stamp}.csv", "text/csv; charset=utf-8", CsvWriter.Write(table)),
            "xlsx" => new ExportFile($"aegis-{key}-{stamp}.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", XlsxWriter.Write(table)),
            _ => throw ValidationFailedException.For("format", "csv or xlsx."),
        };
    }

    private async Task<Table> EmployeesAsync(CancellationToken ct)
    {
        var rows = await db.Employees.AsNoTracking().Where(e => e.Status != EmployeeStatus.Left).OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => new object?[]
            {
                e.EmployeeNumber, e.LastName, e.FirstName, e.Email, e.JobTitle, e.Type.ToString(), e.Status.ToString(),
                e.Team == null ? null : e.Team.Name, e.Manager == null ? null : e.Manager.FirstName + " " + e.Manager.LastName,
                e.Location == null ? null : e.Location.Name, e.NationalityCode, e.StartDate, e.EndDate,
            }).ToListAsync(ct);
        return new Table("Employees", ["Number", "Last name", "First name", "E-mail", "Job title", "Type", "Status", "Team", "Manager", "Location", "Nationality", "Start", "End"], rows);
    }

    private async Task<Table> ManagersAsync(CancellationToken ct)
    {
        var rows = await db.Employees.AsNoTracking()
            .Where(m => m.Status != EmployeeStatus.Left && db.Employees.Any(e => e.ManagerId == m.Id && e.Status != EmployeeStatus.Left))
            .OrderBy(m => m.LastName)
            .Select(m => new object?[]
            {
                m.EmployeeNumber, m.FirstName + " " + m.LastName, m.Email, m.JobTitle, m.Team == null ? null : m.Team.Name,
                db.Employees.Count(e => e.ManagerId == m.Id && e.Status != EmployeeStatus.Left),
            }).ToListAsync(ct);
        return new Table("Managers", ["Number", "Name", "E-mail", "Job title", "Team", "Direct reports"], rows);
    }

    private Task<Table> PrivilegedAsync(CancellationToken ct) => GrantsAsync("Privileged access", privilegedOnly: true, ct);

    private Task<Table> AccessAsync(CancellationToken ct) => GrantsAsync("Access report", privilegedOnly: false, ct);

    private async Task<Table> GrantsAsync(string title, bool privilegedOnly, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var q = db.Grants.AsNoTracking().Where(g => g.EndedAt == null && g.ValidUntil > now);
        if (privilegedOnly) q = q.Where(g => g.Role!.Privileged);
        var rows = await q
            .Join(db.Employees, g => g.EmployeeId, e => e.Id, (g, e) => new { g, e })
            .OrderBy(x => x.g.Role!.System!.Name).ThenBy(x => x.e.LastName)
            .Select(x => new object?[]
            {
                x.g.Role!.System!.Name, x.g.Role.Name, x.g.Role.Privileged, x.e.EmployeeNumber, x.e.FirstName + " " + x.e.LastName,
                x.e.Status.ToString(), x.g.GrantedAt, x.g.ValidUntil, x.g.RequestId,
            }).ToListAsync(ct);
        foreach (var r in rows) r[8] = $"REQ-{(int)r[8]!:000000}";
        return new Table(title, ["Application", "Role", "Privileged", "Number", "Name", "Employee status", "Granted", "Valid until", "Request"], rows);
    }

    private async Task<Table> TrainingAsync(CancellationToken ct)
    {
        var today = time.Today();
        var list = await db.Assignments.AsNoTracking().Include(a => a.Employee).Include(a => a.Course)
            .OrderBy(a => a.Course!.Code).ThenBy(a => a.Employee!.LastName).ToListAsync(ct);
        var rows = list.Select(a => new object?[]
        {
            a.Course!.Code, a.Course.Title, a.Employee!.EmployeeNumber, a.Employee.FullName, a.Status.ToString(),
            a.DueOn, a.CompletedOn, a.ValidUntil, a.Score, a.IsValid(today) ? "compliant" : a.Status == AssignmentStatus.Waived ? "waived" : "not compliant",
        }).ToList();
        return new Table("Training compliance", ["Course", "Title", "Number", "Name", "Status", "Due", "Completed", "Valid until", "Score", "Compliance"], rows);
    }
}
