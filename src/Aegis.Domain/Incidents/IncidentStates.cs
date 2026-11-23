using Aegis.Domain.Common;

namespace Aegis.Domain.Incidents;

/// <summary>
/// Support ticket lifecycle.
/// <code>
/// New ─take─▶ InProgress ⇄ OnHold (hold / resume)
///                 └─resolve─▶ Resolved ─close─▶ Closed
///                                └─reopen─▶ InProgress
/// New / InProgress / OnHold ─cancel─▶ Cancelled
/// </code>
/// </summary>
public abstract class IncidentState : StateBase<IncidentStatus>
{
    private const string Entity = "ticket";

    [Transition("take")]
    public virtual IncidentState Take(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "take");

    [Transition("hold")]
    public virtual IncidentState Hold(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "put on hold");

    [Transition("resume")]
    public virtual IncidentState Resume(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "resume");

    [Transition("resolve")]
    public virtual IncidentState Resolve(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "resolve");

    [Transition("reopen")]
    public virtual IncidentState Reopen(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "reopen");

    [Transition("close")]
    public virtual IncidentState Close(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "close");

    [Transition("cancel")]
    public virtual IncidentState Cancel(Incident i, TransitionContext ctx) => throw NotAllowed(Entity, "cancel");

    protected static void EnsureAgent(TransitionContext ctx)
    {
        if (!ctx.Actor.IsAdmin && !ctx.Actor.Is(PortalRole.Hr))
            throw new ForbiddenException("Only the support team (administrators, HR) works on tickets.");
    }

    protected static IncidentState CancelBy(Incident i, TransitionContext ctx)
    {
        if (ctx.Actor.EmployeeId != i.ReporterId && !ctx.Actor.IsAdmin) throw new ForbiddenException("Only the reporter can cancel a ticket.");
        return Cancelled;
    }

    protected static IncidentState ResolveBy(Incident i, TransitionContext ctx)
    {
        EnsureAgent(ctx);
        if (string.IsNullOrWhiteSpace(ctx.Comment)) throw ValidationFailedException.For("comment", "Describe the resolution.");
        i.Resolution = ctx.Comment.Trim();
        i.ResolvedAt = ctx.Now;
        return Resolved;
    }

    public static readonly IncidentState New = new NewState();
    public static readonly IncidentState InProgress = new InProgressState();
    public static readonly IncidentState OnHold = new OnHoldState();
    public static readonly IncidentState Resolved = new ResolvedState();
    public static readonly IncidentState Closed = new EndState(IncidentStatus.Closed);
    public static readonly IncidentState Cancelled = new EndState(IncidentStatus.Cancelled);

    private static readonly Dictionary<IncidentStatus, IncidentState> All =
        new[] { New, InProgress, OnHold, Resolved, Closed, Cancelled }.ToDictionary(s => s.Status);

    public static IncidentState Of(IncidentStatus status) => All[status];

    private sealed class NewState : IncidentState
    {
        public override IncidentStatus Status => IncidentStatus.New;

        public override IncidentState Take(Incident i, TransitionContext ctx)
        {
            EnsureAgent(ctx);
            i.AssigneeId = ctx.Actor.EmployeeId;
            return InProgress;
        }

        public override IncidentState Cancel(Incident i, TransitionContext ctx) => CancelBy(i, ctx);
    }

    private sealed class InProgressState : IncidentState
    {
        public override IncidentStatus Status => IncidentStatus.InProgress;

        public override IncidentState Hold(Incident i, TransitionContext ctx)
        {
            EnsureAgent(ctx);
            if (string.IsNullOrWhiteSpace(ctx.Comment)) throw ValidationFailedException.For("comment", "Say what the ticket is waiting for.");
            return OnHold;
        }

        public override IncidentState Resolve(Incident i, TransitionContext ctx) => ResolveBy(i, ctx);

        public override IncidentState Cancel(Incident i, TransitionContext ctx) => CancelBy(i, ctx);
    }

    private sealed class OnHoldState : IncidentState
    {
        public override IncidentStatus Status => IncidentStatus.OnHold;

        public override IncidentState Resume(Incident i, TransitionContext ctx)
        {
            EnsureAgent(ctx);
            return InProgress;
        }

        public override IncidentState Resolve(Incident i, TransitionContext ctx) => ResolveBy(i, ctx);

        public override IncidentState Cancel(Incident i, TransitionContext ctx) => CancelBy(i, ctx);
    }

    private sealed class ResolvedState : IncidentState
    {
        public override IncidentStatus Status => IncidentStatus.Resolved;

        /// <summary>The reporter says it is not fixed.</summary>
        public override IncidentState Reopen(Incident i, TransitionContext ctx)
        {
            if (ctx.Actor.EmployeeId != i.ReporterId && !ctx.Actor.IsAdmin) throw new ForbiddenException("Only the reporter can reopen a ticket.");
            i.ResolvedAt = null;
            i.Resolution = null;
            return InProgress;
        }

        /// <summary>The reporter confirms, or the daily job closes it after <see cref="Incident.AutoCloseDays"/> days.</summary>
        public override IncidentState Close(Incident i, TransitionContext ctx)
        {
            if (ctx.Actor == Actor.System && i.ResolvedAt is { } at && at.AddDays(Incident.AutoCloseDays) > ctx.Now)
                throw new BusinessRuleException("The ticket was resolved too recently to close automatically.", "incident.too_recent");
            if (ctx.Actor != Actor.System && ctx.Actor.EmployeeId != i.ReporterId && !ctx.Actor.IsAdmin)
                throw new ForbiddenException("Only the reporter can confirm and close a ticket.");
            return Closed;
        }
    }

    private sealed class EndState(IncidentStatus status) : IncidentState
    {
        public override IncidentStatus Status => status;
    }
}
