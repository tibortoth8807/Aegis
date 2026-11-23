using Aegis.Domain.Common;
using Aegis.Domain.Employees;

namespace Aegis.Domain.Access;

public enum AccessRequestStatus
{
    Draft,
    PendingManager,
    PendingOwner,
    PendingSecurity,
    /// <summary>All approvals done, waiting for provisioning.</summary>
    Approved,
    /// <summary>Provisioned: the person has the access until <see cref="AccessRequest.ValidUntil"/>.</summary>
    Active,
    Rejected,
    Cancelled,
    Expired,
    Revoked,
}

/// <summary>A request for a role in a target system, for oneself or for someone else (e.g. a manager for a new joiner).</summary>
public sealed class AccessRequest
{
    public const int MinJustificationLength = 15;
    public const int MaxOpenRequestsPerBeneficiary = 5;

    public int Id { get; set; }
    /// <summary>Human-friendly number, e.g. "REQ-000042".</summary>
    public string Number { get; set; } = "";
    public int RequesterId { get; set; }
    public Employee? Requester { get; set; }
    public int BeneficiaryId { get; set; }
    public Employee? Beneficiary { get; set; }
    public int RoleId { get; set; }
    public SystemRole? Role { get; set; }
    public string Justification { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }

    /// <summary>Approvers so far, in order - used for the four-eyes rules.</summary>
    public List<int> ApproverIds { get; set; } = [];

    public AccessRequestStatus Status { get; private set; }
    public List<StateChange> History { get; private set; } = [];

    public AccessRequestState State => AccessRequestState.Of(Status);

    public bool IsOpen => Status is AccessRequestStatus.Draft or AccessRequestStatus.PendingManager or AccessRequestStatus.PendingOwner
        or AccessRequestStatus.PendingSecurity or AccessRequestStatus.Approved;

    public static AccessRequest Create(Employee requester, Employee beneficiary, SystemRole role, string justification, TransitionContext ctx)
    {
        justification = justification.Trim();
        if (justification.Length < MinJustificationLength)
            throw ValidationFailedException.For(nameof(Justification), $"Explain in at least {MinJustificationLength} characters why the access is needed.");
        if (beneficiary.Status is EmployeeStatus.Left or EmployeeStatus.Offboarding)
            throw new BusinessRuleException($"{beneficiary.FullName} is leaving the company - no new access can be requested.", "request.beneficiary_leaving");
        var r = new AccessRequest
        {
            RequesterId = requester.Id,
            Requester = requester,
            BeneficiaryId = beneficiary.Id,
            Beneficiary = beneficiary,
            RoleId = role.Id,
            Role = role,
            Justification = justification,
            CreatedAt = ctx.Now,
            Status = AccessRequestStatus.Draft,
        };
        r.History.Add(new StateChange { At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = "create", From = "", To = nameof(AccessRequestStatus.Draft) });
        return r;
    }

    public void Submit(TransitionContext ctx) => Apply("submit", ctx, s => s.Submit(this, ctx));

    public void Approve(TransitionContext ctx) => Apply("approve", ctx, s => s.Approve(this, ctx));

    public void Reject(TransitionContext ctx) => Apply("reject", ctx, s => s.Reject(this, ctx));

    public void Cancel(TransitionContext ctx) => Apply("cancel", ctx, s => s.Cancel(this, ctx));

    public void Provision(TransitionContext ctx) => Apply("provision", ctx, s => s.Provision(this, ctx));

    public void Revoke(TransitionContext ctx) => Apply("revoke", ctx, s => s.Revoke(this, ctx));

    public void Expire(TransitionContext ctx) => Apply("expire", ctx, s => s.Expire(this, ctx));

    private void Apply(string action, TransitionContext ctx, Func<AccessRequestState, AccessRequestState> transition)
    {
        var from = State;
        var to = transition(from);
        Status = to.Status;
        History.Add(new StateChange
        {
            At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = action,
            From = from.Status.ToString(), To = to.Status.ToString(), Comment = ctx.Comment,
        });
        to.OnEnter(this, ctx);
    }
}
