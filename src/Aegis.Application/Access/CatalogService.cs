using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Access;

public sealed record RoleDto(int Id, string Name, string Description, bool Privileged, int ValidityDays, string? ProvisioningGroup, int ActiveGrants);

public sealed record SystemDto(
    int Id, string Code, string Name, string Description, PersonRef? Owner, string Provisioning, int? RequiredTrainingId, string? RequiredTraining,
    IReadOnlyList<RoleDto> Roles);

public sealed record SystemInput(string Code, string Name, string? Description, int OwnerId, string Provisioning, int? RequiredTrainingId);

public sealed record RoleInput(string Name, string? Description, bool Privileged, int ValidityDays, string? ProvisioningGroup);

/// <summary>The catalog of applications and their roles ("Applications" in the UI).</summary>
public sealed class CatalogService(IAegisDbContext db, ICurrentUser user, TimeProvider time)
{
    private static readonly string[] ProvisioningModes = ["manual", "ldap-group", "api"];

    public async Task<IReadOnlyList<SystemDto>> ListAsync(string? search, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var q = db.Systems.AsNoTracking().Include(s => s.Roles).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(x => x.Name.ToLower().Contains(s) || x.Code.ToLower().Contains(s) || x.Roles.Any(r => r.Name.ToLower().Contains(s)));
        }
        var systems = await q.OrderBy(x => x.Name).ToListAsync(ct);
        var ownerIds = systems.Select(s => s.OwnerId).Distinct().ToList();
        var owners = await db.Employees.AsNoTracking().Where(e => ownerIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new PersonRef(e.Id, e.FirstName + " " + e.LastName, e.EmployeeNumber), ct);
        var grantCounts = await db.Grants.AsNoTracking().Where(g => g.EndedAt == null && g.ValidUntil > now)
            .GroupBy(g => g.RoleId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var courses = await db.Courses.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Title, ct);
        return systems.Select(s => new SystemDto(
            s.Id, s.Code, s.Name, s.Description, owners.GetValueOrDefault(s.OwnerId), s.Provisioning, s.RequiredTrainingId,
            s.RequiredTrainingId is int c ? courses.GetValueOrDefault(c) : null,
            s.Roles.OrderBy(r => r.Privileged).ThenBy(r => r.Name)
                .Select(r => new RoleDto(r.Id, r.Name, r.Description, r.Privileged, r.ValidityDays, r.ProvisioningGroup, grantCounts.GetValueOrDefault(r.Id)))
                .ToList())).ToList();
    }

    public async Task<SystemDto> SaveSystemAsync(int? id, SystemInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Admin);
        var code = Guard.Required(input.Code, nameof(input.Code), 12).ToUpperInvariant();
        if (await db.Systems.AnyAsync(s => s.Code == code && s.Id != (id ?? 0), ct))
            throw new BusinessRuleException($"The code {code} is already used.", "system.duplicate_code");
        if (!await db.Employees.AnyAsync(e => e.Id == input.OwnerId, ct)) throw ValidationFailedException.For(nameof(input.OwnerId), "Unknown employee.");
        if (!ProvisioningModes.Contains(input.Provisioning))
            throw ValidationFailedException.For(nameof(input.Provisioning), $"One of: {string.Join(", ", ProvisioningModes)}.");
        var s = id is int existing ? (await db.Systems.FirstOrDefaultAsync(x => x.Id == existing, ct)).OrNotFound("Application", existing) : new TargetSystem();
        s.Code = code;
        s.Name = Guard.Required(input.Name, nameof(input.Name), 100);
        s.Description = input.Description?.Trim() ?? "";
        s.OwnerId = input.OwnerId;
        s.Provisioning = input.Provisioning;
        s.RequiredTrainingId = input.RequiredTrainingId;
        if (id is null) db.Systems.Add(s);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(null, ct)).First(x => x.Id == s.Id);
    }

    public async Task<SystemDto> SaveRoleAsync(int systemId, int? roleId, RoleInput input, CancellationToken ct)
    {
        user.RequireAny(PortalRole.Admin);
        var system = (await db.Systems.Include(s => s.Roles).FirstOrDefaultAsync(s => s.Id == systemId, ct)).OrNotFound("Application", systemId);
        var name = Guard.Required(input.Name, nameof(input.Name), 100);
        if (system.Roles.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.Id != (roleId ?? 0)))
            throw new BusinessRuleException($"{system.Name} already has a role called {name}.", "role.duplicate");
        if (input.ValidityDays is < 1 or > 730) throw ValidationFailedException.For(nameof(input.ValidityDays), "Between 1 and 730 days.");
        var role = roleId is int rid ? system.Roles.FirstOrDefault(r => r.Id == rid).OrNotFound("Role", rid) : new SystemRole();
        role.Name = name;
        role.Description = input.Description?.Trim() ?? "";
        role.Privileged = input.Privileged;
        role.ValidityDays = input.ValidityDays;
        role.ProvisioningGroup = string.IsNullOrWhiteSpace(input.ProvisioningGroup) ? null : input.ProvisioningGroup.Trim();
        if (roleId is null) system.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(null, ct)).First(x => x.Id == systemId);
    }
}
