using Aegis.Application.Common;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Organization;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Employees;

public sealed record CountryDto(string Code, string Name, bool Eea, int Employees);

public sealed record LocationDto(int Id, string Name, string CountryCode, int Employees);

public sealed record TeamDto(int Id, string Name, int DepartmentId, string Department, PersonRef? Lead, int Members);

public sealed record DepartmentDto(int Id, string Code, string Name, int? ParentId, IReadOnlyList<TeamDto> Teams);

public sealed record Lookups(
    IReadOnlyList<CountryDto> Countries, IReadOnlyList<LocationDto> Locations, IReadOnlyList<DepartmentDto> Departments, IReadOnlyList<PersonRef> Managers);

public sealed record TeamInput(string Name, int DepartmentId, int? LeadId);

/// <summary>Reference data: departments and teams, locations, countries ("Nationalities").</summary>
public sealed class OrganizationService(IAegisDbContext db, ICurrentUser user)
{
    public async Task<Lookups> LookupsAsync(CancellationToken ct)
    {
        var active = db.Employees.AsNoTracking().Where(e => e.Status != EmployeeStatus.Left);
        var byCountry = await active.Where(e => e.NationalityCode != null).GroupBy(e => e.NationalityCode!)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var byLocation = await active.Where(e => e.LocationId != null).GroupBy(e => e.LocationId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var byTeam = await active.Where(e => e.TeamId != null).GroupBy(e => e.TeamId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var countries = (await db.Countries.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
            .Select(c => new CountryDto(c.Code, c.Name, c.Eea, byCountry.GetValueOrDefault(c.Code))).ToList();
        var locations = (await db.Locations.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct))
            .Select(l => new LocationDto(l.Id, l.Name, l.CountryCode, byLocation.GetValueOrDefault(l.Id))).ToList();
        var managers = await active.Where(e => (e.PortalRoles & PortalRole.Manager) == PortalRole.Manager).OrderBy(e => e.LastName)
            .Select(e => new PersonRef(e.Id, e.FirstName + " " + e.LastName, e.EmployeeNumber)).ToListAsync(ct);
        var leads = managers.ToDictionary(m => m.Id);
        var departments = (await db.Departments.AsNoTracking().Include(d => d.Teams).OrderBy(d => d.Name).ToListAsync(ct))
            .Select(d => new DepartmentDto(d.Id, d.Code, d.Name, d.ParentId, d.Teams.OrderBy(t => t.Name)
                .Select(t => new TeamDto(t.Id, t.Name, d.Id, d.Name, t.LeadId is int l ? leads.GetValueOrDefault(l) : null, byTeam.GetValueOrDefault(t.Id)))
                .ToList()))
            .ToList();
        return new Lookups(countries, locations, departments, managers);
    }

    public async Task<CountryDto> SaveCountryAsync(string code, string name, bool eea, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        code = Guard.Required(code, nameof(code), 2).ToUpperInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper)) throw ValidationFailedException.For(nameof(code), "Two-letter ISO code, e.g. HU.");
        var c = await db.Countries.FirstOrDefaultAsync(x => x.Code == code, ct);
        if (c is null) db.Countries.Add(c = new Country { Code = code });
        c.Name = Guard.Required(name, nameof(name), 80);
        c.Eea = eea;
        await db.SaveChangesAsync(ct);
        return new CountryDto(c.Code, c.Name, c.Eea, await db.Employees.CountAsync(e => e.NationalityCode == code && e.Status != EmployeeStatus.Left, ct));
    }

    public async Task DeleteCountryAsync(string code, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var c = (await db.Countries.FirstOrDefaultAsync(x => x.Code == code, ct)).OrNotFound("Country", code);
        if (await db.Employees.AnyAsync(e => e.NationalityCode == code, ct))
            throw new BusinessRuleException($"{c.Name} is the nationality of employees and cannot be deleted.", "country.in_use");
        db.Countries.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TeamDto> SaveTeamAsync(int? id, TeamInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Hr);
        var dept = (await db.Departments.FirstOrDefaultAsync(d => d.Id == input.DepartmentId, ct)).OrNotFound("Department", input.DepartmentId);
        var team = id is int tid ? (await db.Teams.FirstOrDefaultAsync(t => t.Id == tid, ct)).OrNotFound("Team", tid) : new Team();
        team.Name = Guard.Required(input.Name, nameof(input.Name), 80);
        team.DepartmentId = dept.Id;
        team.LeadId = input.LeadId;
        if (id is null) db.Teams.Add(team);
        await db.SaveChangesAsync(ct);
        var lead = input.LeadId is int l
            ? await db.Employees.Where(e => e.Id == l).Select(e => new PersonRef(e.Id, e.FirstName + " " + e.LastName, e.EmployeeNumber)).FirstOrDefaultAsync(ct)
            : null;
        return new TeamDto(team.Id, team.Name, dept.Id, dept.Name, lead, await db.Employees.CountAsync(e => e.TeamId == team.Id, ct));
    }
}
