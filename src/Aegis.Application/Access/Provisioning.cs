using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aegis.Application.Access;

/// <summary>
/// Turns an approved request into real access: calls the provisioning gateway, then moves the request to Active
/// and records the grant. If the gateway fails the request stays Approved and the next run retries it.
/// </summary>
public sealed class AccessProvisioner(IAegisDbContext db, IProvisioningGateway gateway, TimeProvider time, ILogger<AccessProvisioner> log)
{
    /// <returns>True when the access is now active.</returns>
    public async Task<bool> ProvisionAsync(AccessRequest r, TransitionContext ctx, CancellationToken ct)
    {
        var beneficiary = r.Beneficiary ?? throw new InvalidOperationException("Load the beneficiary.");
        var role = r.Role ?? throw new InvalidOperationException("Load the role.");
        try
        {
            await gateway.GrantAsync(beneficiary, role, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Provisioning {Request} failed; it stays approved and is retried", r.Number);
            return false;
        }
        r.Provision(ctx);
        db.Grants.Add(new AccessGrant
        {
            EmployeeId = r.BeneficiaryId, RoleId = r.RoleId, RequestId = r.Id, GrantedAt = ctx.Now,
            ValidUntil = r.ValidUntil ?? ctx.Now,
        });
        return true;
    }

    /// <summary>Provisions every approved request (the provisioning job).</summary>
    public async Task<int> ProvisionPendingAsync(CancellationToken ct)
    {
        var approved = await db.AccessRequests
            .Include(r => r.Beneficiary).Include(r => r.Role).ThenInclude(x => x!.System)
            .Where(r => r.Status == AccessRequestStatus.Approved).ToListAsync(ct);
        var done = 0;
        var ctx = new TransitionContext(Actor.System with { Name = "Provisioning" }, time.GetUtcNow());
        foreach (var r in approved)
            if (await ProvisionAsync(r, ctx, ct)) done++;
        return done;
    }
}

/// <summary>Ends access: in the target system (gateway), on the grant and on the request.</summary>
public sealed class AccessRevoker(IAegisDbContext db, IProvisioningGateway gateway, ILogger<AccessRevoker> log)
{
    public async Task RevokeAsync(AccessRequest r, TransitionContext ctx, CancellationToken ct, bool expire = false)
    {
        if (expire) r.Expire(ctx);
        else r.Revoke(ctx);
        await EndGrantAsync(r, ctx.Now, ct);
    }

    /// <summary>Removes everything a leaver has. Gateway failures are logged, the records are ended anyway (and reported).</summary>
    public async Task<int> RevokeAllAsync(Employee e, TransitionContext ctx, string reason, CancellationToken ct)
    {
        var active = await db.AccessRequests
            .Include(r => r.Beneficiary).Include(r => r.Role).ThenInclude(x => x!.System)
            .Where(r => r.BeneficiaryId == e.Id && r.Status == AccessRequestStatus.Active).ToListAsync(ct);
        var revokeCtx = ctx with { Actor = Actor.System with { Name = "Offboarding" }, Comment = reason };
        foreach (var r in active) await RevokeAsync(r, revokeCtx, ct);

        // open requests for a leaver make no sense any more
        var open = await db.AccessRequests.Where(r => r.BeneficiaryId == e.Id &&
            (r.Status == AccessRequestStatus.Draft || r.Status == AccessRequestStatus.PendingManager || r.Status == AccessRequestStatus.PendingOwner
             || r.Status == AccessRequestStatus.PendingSecurity)).ToListAsync(ct);
        foreach (var r in open) r.Cancel(revokeCtx);
        return active.Count;
    }

    private async Task EndGrantAsync(AccessRequest r, DateTimeOffset now, CancellationToken ct)
    {
        var grant = await db.Grants.FirstOrDefaultAsync(g => g.RequestId == r.Id && g.EndedAt == null, ct);
        if (grant is not null) grant.EndedAt = now;
        try
        {
            await gateway.RevokeAsync(r.Beneficiary ?? throw new InvalidOperationException("Load the beneficiary."),
                r.Role ?? throw new InvalidOperationException("Load the role."), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not InvalidOperationException)
        {
            log.LogError(e, "Removing {Request} in the target system failed - manual clean-up needed", r.Number);
        }
    }
}
