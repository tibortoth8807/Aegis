using System.Text;
using Aegis.Application.Common;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Organization;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Import;

/// <summary>One person as the corporate directory (LDAP / HR export) knows them.</summary>
public sealed record DirectoryRecord(
    string EmployeeNumber, string FirstName, string LastName, string Email, string JobTitle,
    string? ManagerNumber, string? Team, string? Location, string? Country, bool External, string? Company, bool Disabled);

/// <summary>Where directory records come from: an LDAP server, an uploaded LDIF or CSV file.</summary>
public interface IDirectorySource
{
    string Name { get; }

    Task<IReadOnlyList<DirectoryRecord>> ReadAsync(CancellationToken ct);
}

public sealed record ImportChange(string EmployeeNumber, string Name, string Kind, string Details);

public sealed record ImportReport(
    string Source, bool DryRun, int Read, int Created, int Updated, int Unchanged,
    IReadOnlyList<ImportChange> Changes, IReadOnlyList<string> Warnings);

/// <summary>
/// Synchronises employees from the directory. Matching is by employee number. New people are hired, changed
/// fields are updated, and people missing from (or disabled in) the directory are only reported - leaving the
/// company is an HR decision, not an import side effect. A dry run shows the changes without saving them.
/// </summary>
public sealed class DirectoryImportService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    public async Task<ImportReport> ImportAsync(IDirectorySource source, bool dryRun, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var records = await source.ReadAsync(ct);
        var changes = new List<ImportChange>();
        var warnings = new List<string>();
        int created = 0, updated = 0, unchanged = 0;

        var existing = await db.Employees.ToDictionaryAsync(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase, ct);
        var teams = await db.Teams.ToListAsync(ct);
        var locations = await db.Locations.ToListAsync(ct);
        var countries = await db.Countries.Select(c => c.Code).ToListAsync(ct);
        var ctx = new TransitionContext(user.Actor with { Name = $"{user.Actor.Name} (import)" }, time.GetUtcNow(), $"Imported from {source.Name}");
        var today = time.Today();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in records)
        {
            var number = r.EmployeeNumber.Trim().ToUpperInvariant();
            if (number.Length == 0 || !seen.Add(number))
            {
                warnings.Add(number.Length == 0 ? $"{r.FirstName} {r.LastName}: no employee number - skipped." : $"{number} appears twice - second entry skipped.");
                continue;
            }
            if (r.Disabled)
            {
                if (existing.TryGetValue(number, out var gone) && gone.Status is not (EmployeeStatus.Left or EmployeeStatus.Offboarding))
                    warnings.Add($"{number} {gone.FullName} is disabled in the directory but active here - check whether they left.");
                continue;
            }
            var teamId = Resolve(r.Team, teams.Select(t => (t.Id, t.Name)), number, "team", warnings);
            var location = r.Location is { Length: > 0 } ln ? FindOrCreateLocation(ln, r.Country, locations, dryRun) : null;
            var nationality = r.Country is { Length: 2 } cc && countries.Contains(cc.ToUpperInvariant()) ? cc.ToUpperInvariant() : null;

            if (!existing.TryGetValue(number, out var e))
            {
                if (r.External)
                {
                    // externals need a contract end date and a sponsor, which the directory does not know
                    warnings.Add($"{number} {r.FirstName} {r.LastName} is external - register externals manually.");
                    continue;
                }
                e = new Employee
                {
                    EmployeeNumber = number, FirstName = r.FirstName.Trim(), LastName = r.LastName.Trim(), Email = r.Email.Trim().ToLowerInvariant(),
                    JobTitle = r.JobTitle.Trim(), Type = EmploymentType.Internal, TeamId = teamId, Location = location,
                    NationalityCode = nationality, StartDate = today, Source = "ldap",
                };
                existing[number] = e;
                changes.Add(new ImportChange(number, e.FullName, "created", e.JobTitle + (r.Team is null ? "" : $", {r.Team}")));
                created++;
                if (!dryRun)
                {
                    Employee.Hire(e, today, ctx);
                    db.Employees.Add(e);
                }
                continue;
            }

            var diff = new StringBuilder();
            void Set<T>(string field, T current, T value, Action<T> apply)
            {
                if (EqualityComparer<T>.Default.Equals(current, value)) return;
                diff.Append(diff.Length > 0 ? "; " : "").Append(field).Append(": ").Append(current?.ToString() ?? "–").Append(" → ").Append(value?.ToString() ?? "–");
                if (!dryRun) apply(value);
            }
            Set("first name", e.FirstName, r.FirstName.Trim(), v => e.FirstName = v);
            Set("last name", e.LastName, r.LastName.Trim(), v => e.LastName = v);
            Set("e-mail", e.Email, r.Email.Trim().ToLowerInvariant(), v => e.Email = v);
            Set("job title", e.JobTitle, r.JobTitle.Trim(), v => e.JobTitle = v);
            if (teamId is not null) Set("team", e.TeamId, teamId, v => e.TeamId = v);
            if (location is not null)
            {
                var current = e.LocationId is int lid ? locations.FirstOrDefault(l => l.Id == lid)?.Name : null;
                Set("location", current, location.Name, _ => e.Location = location);
            }
            if (nationality is not null) Set("nationality", e.NationalityCode, nationality, v => e.NationalityCode = v);
            if (diff.Length > 0)
            {
                changes.Add(new ImportChange(number, e.FullName, "updated", diff.ToString()));
                updated++;
            }
            else unchanged++;
        }

        // second pass: reporting lines (the manager may be later in the file)
        foreach (var r in records.Where(r => !r.Disabled && r.ManagerNumber is { Length: > 0 }))
        {
            if (!existing.TryGetValue(r.EmployeeNumber.Trim(), out var e)) continue;
            if (!existing.TryGetValue(r.ManagerNumber!.Trim(), out var m))
            {
                warnings.Add($"{e.EmployeeNumber}: manager {r.ManagerNumber} is not known - reporting line not changed.");
                continue;
            }
            if (e.ManagerId == m.Id || m == e) continue;
            var line = changes.FindIndex(c => c.EmployeeNumber == e.EmployeeNumber);
            var text = $"manager → {m.FullName}";
            if (line >= 0) changes[line] = changes[line] with { Details = changes[line].Details + "; " + text };
            else
            {
                changes.Add(new ImportChange(e.EmployeeNumber, e.FullName, "updated", text));
                updated++;
                unchanged--;
            }
            if (!dryRun) e.Manager = m;
        }

        // people who came from the directory earlier but are no longer in it
        foreach (var e in existing.Values.Where(e => e.Source == "ldap" && e.Id != 0 && !seen.Contains(e.EmployeeNumber) && e.Status != EmployeeStatus.Left))
            warnings.Add($"{e.EmployeeNumber} {e.FullName} is no longer in the directory - check whether they left.");

        if (!dryRun) await db.SaveChangesAsync(ct);
        return new ImportReport(source.Name, dryRun, records.Count, created, updated, unchanged, changes, warnings);
    }

    private static int? Resolve(string? name, IEnumerable<(int Id, string Name)> items, string number, string what, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = items.FirstOrDefault(i => i.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.Id != 0) return match.Id;
        warnings.Add($"{number}: unknown {what} \"{name}\" - left empty.");
        return null;
    }

    private Location FindOrCreateLocation(string name, string? country, List<Location> locations, bool dryRun)
    {
        var l = locations.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (l is not null) return l;
        l = new Location { Name = name.Trim(), CountryCode = (country ?? "").ToUpperInvariant() };
        locations.Add(l);
        if (!dryRun) db.Locations.Add(l);
        return l;
    }
}
