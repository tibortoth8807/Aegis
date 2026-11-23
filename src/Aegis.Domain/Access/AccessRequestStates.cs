using Aegis.Domain.Common;

namespace Aegis.Domain.Access;

/// <summary>
/// Approval workflow of an access request. One class per state; see <see cref="StateBase{TStatus}"/>.
/// <code>
/// Draft ─submit─▶ PendingManager ─approve─▶ PendingOwner ─approve─▶ [privileged?] PendingSecurity ─approve─▶ Approved ─provision─▶ Active
///   │                 │                          │                                  │                                        │  ├─revoke─▶ Revoked
///   └──cancel─────────┴──reject / cancel─────────┴──reject / cancel────────────────┘──▶ Rejected / Cancelled                └─expire─▶ Expired
/// </code>
/// Four-eyes rules: nobody approves their own request or a request for themselves, and every approval step is
/// done by a different person.
/// </summary>
public abstract class AccessRequestState : StateBase<AccessRequestStatus>
{
    private const string Entity = "request";
    /// <summary>Privileged roles are never granted for longer than this.</summary>
    public const int PrivilegedMaxDays = 30;

    [Transition("submit")]
    public virtual AccessRequestState Submit(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "submit");

    [Transition("approve")]
    public virtual AccessRequestState Approve(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "approve");

    [Transition("reject")]
    public virtual AccessRequestState Reject(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "reject");

    [Transition("cancel")]
    public virtual AccessRequestState Cancel(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "cancel");

    [Transition("provision")]
    public virtual AccessRequestState Provision(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "provision");

    [Transition("revoke")]
    public virtual AccessRequestState Revoke(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "revoke");

    [Transition("expire")]
    public virtual AccessRequestState Expire(AccessRequest r, TransitionContext ctx) => throw NotAllowed(Entity, "expire");

    /// <summary>Entry action, runs after the status changed.</summary>
    public virtual void OnEnter(AccessRequest r, TransitionContext ctx)
    {
    }

    /// <summary>Who may approve or reject in this state (pending states override it).</summary>
    public virtual bool CanDecide(AccessRequest r, Actor actor) => false;

    // ------------------------------------------------------------------ shared rules

    protected static void EnsureCanDecide(AccessRequestState state, AccessRequest r, Actor actor)
    {
        if (actor.EmployeeId == r.RequesterId) throw new ForbiddenException("You cannot approve or reject a request you created.");
        if (actor.EmployeeId == r.BeneficiaryId) throw new ForbiddenException("You cannot approve or reject access for yourself.");
        if (r.ApproverIds.Contains(actor.EmployeeId)) throw new ForbiddenException("You already approved this request - another person must do the next step.");
        if (!state.CanDecide(r, actor)) throw new ForbiddenException($"You are not an approver for a request that is {Humanize(state.Status.ToString())}.");
    }

    protected static bool IsNeutral(AccessRequest r, Actor actor) =>
        actor.EmployeeId != r.RequesterId && actor.EmployeeId != r.BeneficiaryId && !r.ApproverIds.Contains(actor.EmployeeId);

    protected static AccessRequestState RejectWith(AccessRequestState state, AccessRequest r, TransitionContext ctx)
    {
        EnsureCanDecide(state, r, ctx.Actor);
        if (string.IsNullOrWhiteSpace(ctx.Comment)) throw ValidationFailedException.For("comment", "Give a reason for the rejection.");
        return Rejected;
    }

    protected static AccessRequestState CancelBy(AccessRequest r, TransitionContext ctx)
    {
        var a = ctx.Actor;
        if (a.EmployeeId != r.RequesterId && a.EmployeeId != r.BeneficiaryId && !a.IsAdmin)
            throw new ForbiddenException("Only the requester, the beneficiary or an administrator can cancel a request.");
        return Cancelled;
    }

    protected static SystemRole RoleOf(AccessRequest r) =>
        r.Role ?? throw new InvalidOperationException("The request's role must be loaded.");

    protected static int OwnerOf(AccessRequest r) =>
        (RoleOf(r).System ?? throw new InvalidOperationException("The role's system must be loaded.")).OwnerId;

    // ------------------------------------------------------------------ the states

    public static readonly AccessRequestState Draft = new DraftState();
    public static readonly AccessRequestState PendingManager = new PendingManagerState();
    public static readonly AccessRequestState PendingOwner = new PendingOwnerState();
    public static readonly AccessRequestState PendingSecurity = new PendingSecurityState();
    public static readonly AccessRequestState Approved = new ApprovedState();
    public static readonly AccessRequestState Active = new ActiveState();
    public static readonly AccessRequestState Rejected = new FinalState(AccessRequestStatus.Rejected);
    public static readonly AccessRequestState Cancelled = new FinalState(AccessRequestStatus.Cancelled);
    public static readonly AccessRequestState Expired = new FinalState(AccessRequestStatus.Expired);
    public static readonly AccessRequestState Revoked = new FinalState(AccessRequestStatus.Revoked);

    private static readonly Dictionary<AccessRequestStatus, AccessRequestState> All =
        new[] { Draft, PendingManager, PendingOwner, PendingSecurity, Approved, Active, Rejected, Cancelled, Expired, Revoked }.ToDictionary(s => s.Status);

    public static AccessRequestState Of(AccessRequestStatus status) => All[status];

    private sealed class DraftState : AccessRequestState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.Draft;

        public override AccessRequestState Submit(AccessRequest r, TransitionContext ctx)
        {
            if (ctx.Actor.EmployeeId != r.RequesterId && !ctx.Actor.IsAdmin) throw new ForbiddenException("Only the requester can submit the request.");
            var managerId = (r.Beneficiary ?? throw new InvalidOperationException("The beneficiary must be loaded.")).ManagerId;
            // A manager requesting for their own report has approved it by asking; no manager → straight to the owner.
            if (managerId is null || managerId == r.RequesterId)
            {
                if (managerId is not null) r.ApproverIds.Add(managerId.Value);
                return PendingOwner;
            }
            return PendingManager;
        }

        public override AccessRequestState Cancel(AccessRequest r, TransitionContext ctx) => CancelBy(r, ctx);
    }

    private abstract class PendingState : AccessRequestState
    {
        public override AccessRequestState Reject(AccessRequest r, TransitionContext ctx) => RejectWith(this, r, ctx);

        public override AccessRequestState Cancel(AccessRequest r, TransitionContext ctx) => CancelBy(r, ctx);

        public override void OnEnter(AccessRequest r, TransitionContext ctx) => r.SubmittedAt ??= ctx.Now;

        protected AccessRequestState ApproveStep(AccessRequest r, TransitionContext ctx, AccessRequestState next)
        {
            EnsureCanDecide(this, r, ctx.Actor);
            r.ApproverIds.Add(ctx.Actor.EmployeeId);
            return next;
        }
    }

    private sealed class PendingManagerState : PendingState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.PendingManager;

        public override bool CanDecide(AccessRequest r, Actor actor) =>
            IsNeutral(r, actor) && (actor.EmployeeId == r.Beneficiary?.ManagerId || actor.IsAdmin);

        public override AccessRequestState Approve(AccessRequest r, TransitionContext ctx) => ApproveStep(r, ctx, PendingOwner);
    }

    private sealed class PendingOwnerState : PendingState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.PendingOwner;

        public override bool CanDecide(AccessRequest r, Actor actor) =>
            IsNeutral(r, actor) && (actor.EmployeeId == OwnerOf(r) || actor.IsAdmin);

        public override AccessRequestState Approve(AccessRequest r, TransitionContext ctx) =>
            ApproveStep(r, ctx, RoleOf(r).Privileged ? PendingSecurity : Approved);
    }

    private sealed class PendingSecurityState : PendingState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.PendingSecurity;

        public override bool CanDecide(AccessRequest r, Actor actor) => IsNeutral(r, actor) && actor.Is(PortalRole.SecurityOfficer);

        public override AccessRequestState Approve(AccessRequest r, TransitionContext ctx) => ApproveStep(r, ctx, Approved);
    }

    private sealed class ApprovedState : AccessRequestState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.Approved;

        public override void OnEnter(AccessRequest r, TransitionContext ctx) => r.DecidedAt = ctx.Now;

        public override AccessRequestState Provision(AccessRequest r, TransitionContext ctx)
        {
            if (!ctx.Actor.IsAdmin) throw new ForbiddenException("Only provisioning (an administrator or the system) can activate access.");
            return Active;
        }
    }

    private sealed class ActiveState : AccessRequestState
    {
        public override AccessRequestStatus Status => AccessRequestStatus.Active;

        public override void OnEnter(AccessRequest r, TransitionContext ctx)
        {
            var role = RoleOf(r);
            var days = role.Privileged ? Math.Min(role.ValidityDays, PrivilegedMaxDays) : role.ValidityDays;
            r.ValidUntil = ctx.Now.AddDays(days);
        }

        public override AccessRequestState Revoke(AccessRequest r, TransitionContext ctx)
        {
            var a = ctx.Actor;
            var allowed = a.IsAdmin || a.Is(PortalRole.SecurityOfficer) || a.EmployeeId == OwnerOf(r) || a.EmployeeId == r.Beneficiary?.ManagerId;
            if (!allowed) throw new ForbiddenException("Only an administrator, a security officer, the application owner or the manager can revoke access.");
            if (string.IsNullOrWhiteSpace(ctx.Comment)) throw ValidationFailedException.For("comment", "Give a reason for revoking the access.");
            return Revoked;
        }

        public override AccessRequestState Expire(AccessRequest r, TransitionContext ctx)
        {
            if (r.ValidUntil is { } until && until > ctx.Now)
                throw new BusinessRuleException($"The access is valid until {until:yyyy-MM-dd} and cannot expire yet.", "request.not_expired");
            return Expired;
        }
    }

    private sealed class FinalState(AccessRequestStatus status) : AccessRequestState
    {
        public override AccessRequestStatus Status => status;

        public override void OnEnter(AccessRequest r, TransitionContext ctx) => r.DecidedAt ??= ctx.Now;
    }
}
