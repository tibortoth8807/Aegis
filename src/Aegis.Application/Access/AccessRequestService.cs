using Aegis.Application.Common;
using Aegis.Application.Employees;
using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Access;

public sealed record RequestListItem(
    int Id, string Number, string Status, string System, string Role, bool Privileged, string Requester, string Beneficiary,
    DateTimeOffset CreatedAt, DateTimeOffset? ValidUntil, bool AwaitsMe);

public sealed record RequestDetail(
    int Id, string Number, string Status, PersonRef Requester, PersonRef Beneficiary, int SystemId, string System, int RoleId, string Role,
    bool Privileged, string Justification, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? DecidedAt,
    DateTimeOffset? ValidUntil, IReadOnlyList<string> Steps, int CurrentStep, IReadOnlyList<HistoryDto> History, IReadOnlyList<string> AllowedActions);

public sealed record NewRequest(int RoleId, string Justification, int? BeneficiaryId = null, bool Submit = true);

public sealed record RequestTransition(string Action, string? Comment = null);

/// <summary>"mine" = I requested it or it is for me, "approvals" = waiting for my decision, "all" = admins and security.</summary>
public sealed record RequestFilter(string Scope = "mine", string? Status = null, int? SystemId = null);

public sealed class AccessRequestService(
    IAegisDbContext db, ICurrentUser user, TimeProvider time, AccessProvisioner provisioner, AccessRevoker revoker)
{
    /// <summary>HTTP action name → domain call. Whether the action is allowed is decided by the request's current state.</summary>
    private Dictionary<string, Func<AccessRequest, TransitionContext, CancellationToken, Task>> Actions => new()
    {
        ["submit"] = (r, c, _) => Sync(() => r.Submit(c)),
        ["approve"] = (r, c, _) => Sync(() => r.Approve(c)),
        ["reject"] = (r, c, _) => Sync(() => r.Reject(c)),
        ["cancel"] = (r, c, _) => Sync(() => r.Cancel(c)),
        ["provision"] = ProvisionManuallyAsync,
        ["revoke"] = (r, c, ct) => revoker.RevokeAsync(r, c, ct),
    };

    private static Task Sync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private async Task ProvisionManuallyAsync(AccessRequest r, TransitionContext ctx, CancellationToken ct)
    {
        if (!r.State.Allows("provision")) r.Provision(ctx);   // the state reports the invalid transition
        user.RequireAny(PortalRole.Admin);
        if (!await provisioner.ProvisionAsync(r, ctx, ct))
            throw new BusinessRuleException("The target system did not accept the change. The request stays approved; try again later.", "provisioning.failed");
    }

    private IQueryable<AccessRequest> Full => db.AccessRequests
        .Include(r => r.Requester).Include(r => r.Beneficiary).Include(r => r.Role).ThenInclude(x => x!.System);

    public async Task<Page<RequestListItem>> ListAsync(PageQuery q, RequestFilter f, CancellationToken ct)
    {
        var me = user.Actor;
        var query = db.AccessRequests.AsNoTracking().AsQueryable();
        query = f.Scope switch
        {
            "approvals" => AwaitingDecision(query, me),
            "all" when me.IsAdmin || me.Is(PortalRole.SecurityOfficer) || me.Is(PortalRole.Hr) => query,
            "all" => throw new ForbiddenException("Only administrators, HR and security officers see all requests."),
            "team" => query.Where(r => r.Beneficiary!.ManagerId == me.EmployeeId),
            _ => query.Where(r => r.RequesterId == me.EmployeeId || r.BeneficiaryId == me.EmployeeId),
        };
        if (Enum.TryParse<AccessRequestStatus>(f.Status, true, out var st)) query = query.Where(r => r.Status == st);
        else if (f.Status == "open")
            query = query.Where(r => r.Status == AccessRequestStatus.Draft || r.Status == AccessRequestStatus.PendingManager
                || r.Status == AccessRequestStatus.PendingOwner || r.Status == AccessRequestStatus.PendingSecurity || r.Status == AccessRequestStatus.Approved);
        if (f.SystemId is int sys) query = query.Where(r => r.Role!.SystemId == sys);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim().ToLower();
            query = query.Where(r => r.Number.ToLower().Contains(s) || r.Role!.Name.ToLower().Contains(s) || r.Role.System!.Name.ToLower().Contains(s)
                || (r.Beneficiary!.FirstName + " " + r.Beneficiary.LastName).ToLower().Contains(s));
        }
        var total = await query.CountAsync(ct);
        var awaiting = AwaitingDecision(db.AccessRequests.AsNoTracking(), me).Select(r => r.Id);
        var items = await (q.Desc || q.Sort is null ? query.OrderByDescending(r => r.CreatedAt) : query.OrderBy(r => r.CreatedAt))
            .Skip(q.Skip).Take(q.Size)
            .Select(r => new RequestListItem(
                r.Id, r.Number, r.Status.ToString(), r.Role!.System!.Name, r.Role.Name, r.Role.Privileged,
                r.Requester!.FirstName + " " + r.Requester.LastName, r.Beneficiary!.FirstName + " " + r.Beneficiary.LastName,
                r.CreatedAt, r.ValidUntil, awaiting.Contains(r.Id)))
            .ToListAsync(ct);
        return new Page<RequestListItem>(items, total, q.Page, q.Size);
    }

    /// <summary>The requests the user can decide on now - the same rules as the state classes, as a query.</summary>
    public static IQueryable<AccessRequest> AwaitingDecision(IQueryable<AccessRequest> q, Actor me)
    {
        var id = me.EmployeeId;
        var admin = me.IsAdmin;
        var security = me.Is(PortalRole.SecurityOfficer);
        return q.Where(r => r.RequesterId != id && r.BeneficiaryId != id && !r.ApproverIds.Contains(id) && (
            (r.Status == AccessRequestStatus.PendingManager && (admin || r.Beneficiary!.ManagerId == id)) ||
            (r.Status == AccessRequestStatus.PendingOwner && (admin || r.Role!.System!.OwnerId == id)) ||
            (r.Status == AccessRequestStatus.PendingSecurity && security)));
    }

    public async Task<RequestDetail> GetAsync(int id, CancellationToken ct)
    {
        var r = (await Full.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Request", id);
        var me = user.Actor;
        var involved = r.RequesterId == me.EmployeeId || r.BeneficiaryId == me.EmployeeId || r.Beneficiary!.ManagerId == me.EmployeeId
            || r.Role!.System!.OwnerId == me.EmployeeId || me.IsAdmin || me.Is(PortalRole.SecurityOfficer) || me.Is(PortalRole.Hr)
            || r.ApproverIds.Contains(me.EmployeeId);
        if (!involved) throw new ForbiddenException("You are not involved in this request.");
        return ToDetail(r);
    }

    private RequestDetail ToDetail(AccessRequest r)
    {
        var role = r.Role!;
        var steps = new List<string> { "Draft", "Manager", "Application owner" };
        if (role.Privileged) steps.Add("Security officer");
        steps.AddRange(["Provisioning", "Active"]);
        var current = r.Status switch
        {
            AccessRequestStatus.Draft => 0,
            AccessRequestStatus.PendingManager => 1,
            AccessRequestStatus.PendingOwner => 2,
            AccessRequestStatus.PendingSecurity => 3,
            AccessRequestStatus.Approved => steps.Count - 2,
            AccessRequestStatus.Active => steps.Count - 1,
            _ => -1,
        };
        return new RequestDetail(
            r.Id, r.Number, r.Status.ToString(),
            new PersonRef(r.RequesterId, r.Requester!.FullName, r.Requester.EmployeeNumber),
            new PersonRef(r.BeneficiaryId, r.Beneficiary!.FullName, r.Beneficiary.EmployeeNumber),
            role.SystemId, role.System!.Name, role.Id, role.Name, role.Privileged, r.Justification, r.CreatedAt, r.SubmittedAt, r.DecidedAt,
            r.ValidUntil, steps, current,
            r.History.OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Select(h => new HistoryDto(h.At, h.ActorName, h.Action, h.From, h.To, h.Comment)).ToList(),
            ActionsFor(r, user.Actor));
    }

    /// <summary>What the user can do here: the state's actions, narrowed to the ones this person is entitled to.</summary>
    public static IReadOnlyList<string> ActionsFor(AccessRequest r, Actor me)
    {
        var state = r.State;
        bool CanDecide() => state.CanDecide(r, me);
        var owner = r.Role?.System?.OwnerId;
        return state.AllowedActions.Where(a => a switch
        {
            "submit" => me.EmployeeId == r.RequesterId || me.IsAdmin,
            "approve" or "reject" => CanDecide(),
            "cancel" => me.EmployeeId == r.RequesterId || me.EmployeeId == r.BeneficiaryId || me.IsAdmin,
            "provision" => me.IsAdmin,
            "revoke" => me.IsAdmin || me.Is(PortalRole.SecurityOfficer) || me.EmployeeId == owner || me.EmployeeId == r.Beneficiary?.ManagerId,
            _ => false,   // expire: only the daily job
        }).ToList();
    }

    public async Task<RequestDetail> CreateAsync(NewRequest input, CancellationToken ct)
    {
        var me = user.Actor;
        var requester = (await db.Employees.FirstOrDefaultAsync(e => e.Id == me.EmployeeId, ct)).OrNotFound("Employee", me.EmployeeId);
        var beneficiaryId = input.BeneficiaryId ?? me.EmployeeId;
        var beneficiary = (await db.Employees.FirstOrDefaultAsync(e => e.Id == beneficiaryId, ct)).OrNotFound("Employee", beneficiaryId);
        if (beneficiaryId != me.EmployeeId && beneficiary.ManagerId != me.EmployeeId && !me.IsAdmin && !me.Is(PortalRole.Hr))
            throw new ForbiddenException("You can request access for yourself and for your direct reports.");
        var role = (await db.Roles.Include(r => r.System).FirstOrDefaultAsync(r => r.Id == input.RoleId, ct)).OrNotFound("Role", input.RoleId);

        await CheckRulesAsync(beneficiary, role, ct);

        var r = AccessRequest.Create(requester, beneficiary, role, input.Justification ?? "", user.Context(time));
        db.AccessRequests.Add(r);
        await db.SaveChangesAsync(ct);
        r.Number = $"REQ-{r.Id:000000}";
        if (input.Submit) r.Submit(user.Context(time));
        await db.SaveChangesAsync(ct);
        return await GetAsync(r.Id, ct);
    }

    /// <summary>Rules that need the database (the state classes check the rest).</summary>
    private async Task CheckRulesAsync(Employee beneficiary, SystemRole role, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await db.Grants.AnyAsync(g => g.EmployeeId == beneficiary.Id && g.RoleId == role.Id && g.EndedAt == null && g.ValidUntil > now, ct))
            throw new BusinessRuleException($"{beneficiary.FullName} already has {role.System!.Name} / {role.Name}.", "request.already_granted");

        var open = await db.AccessRequests.Where(r => r.BeneficiaryId == beneficiary.Id && (r.Status == AccessRequestStatus.Draft
            || r.Status == AccessRequestStatus.PendingManager || r.Status == AccessRequestStatus.PendingOwner
            || r.Status == AccessRequestStatus.PendingSecurity || r.Status == AccessRequestStatus.Approved))
            .Select(r => new { r.RoleId, r.Number }).ToListAsync(ct);
        if (open.FirstOrDefault(o => o.RoleId == role.Id) is { } dup)
            throw new BusinessRuleException($"There is already an open request for this role: {dup.Number}.", "request.duplicate");
        if (open.Count >= AccessRequest.MaxOpenRequestsPerBeneficiary)
            throw new BusinessRuleException($"{beneficiary.FullName} already has {open.Count} open requests - wait until some are decided.", "request.too_many_open");

        if (role.System!.RequiredTrainingId is int courseId)
        {
            var today = time.Today();
            var valid = await db.Assignments.AnyAsync(a => a.EmployeeId == beneficiary.Id && a.CourseId == courseId
                && a.Status == AssignmentStatus.Completed && (a.ValidUntil == null || a.ValidUntil >= today), ct);
            if (!valid)
            {
                var course = await db.Courses.Where(c => c.Id == courseId).Select(c => c.Title).FirstOrDefaultAsync(ct);
                throw new BusinessRuleException($"{role.System.Name} requires the training \"{course}\" first.", "request.training_required");
            }
        }
    }

    public async Task<RequestDetail> TransitionAsync(int id, RequestTransition t, CancellationToken ct)
    {
        var r = (await Full.FirstOrDefaultAsync(x => x.Id == id, ct)).OrNotFound("Request", id);
        var ctx = user.Context(time, t.Comment);
        var perform = Actions.GetValueOrDefault(t.Action) ?? throw ValidationFailedException.For("action", $"Unknown action '{t.Action}'.");
        await perform(r, ctx, ct);
        // systems with automatic provisioning get the access right after the last approval
        if (r.Status == AccessRequestStatus.Approved && r.Role!.System!.Provisioning != "manual")
            await provisioner.ProvisionAsync(r, new TransitionContext(Actor.System with { Name = "Provisioning" }, ctx.Now), ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }
}
