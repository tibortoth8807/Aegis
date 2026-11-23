using Aegis.Domain.Common;

namespace Aegis.Domain.Employees;

/// <summary>
/// Employee lifecycle (joiner - mover - leaver). One class per state; see <see cref="StateBase{TStatus}"/>.
/// <code>
/// Preboarding ──activate──▶ Active ◀──return── OnLeave
///      │                     │  └──start-leave──▶ ▲
///      │                 give-notice
///      │                     ▼
///      └──give-notice──▶ Offboarding ──leave──▶ Left
///                            └──withdraw-notice──▶ Active
/// </code>
/// </summary>
public abstract class EmployeeState : StateBase<EmployeeStatus>
{
    private const string Entity = "employee";

    [Transition("activate")]
    public virtual EmployeeState Activate(Employee e, TransitionContext ctx) => throw NotAllowed(Entity, "activate");

    [Transition("start-leave")]
    public virtual EmployeeState StartLeave(Employee e, TransitionContext ctx) => throw NotAllowed(Entity, "start leave for");

    [Transition("return")]
    public virtual EmployeeState ReturnFromLeave(Employee e, TransitionContext ctx) => throw NotAllowed(Entity, "return");

    [Transition("give-notice")]
    public virtual EmployeeState GiveNotice(Employee e, DateOnly endDate, TransitionContext ctx) => throw NotAllowed(Entity, "give notice to");

    [Transition("withdraw-notice")]
    public virtual EmployeeState WithdrawNotice(Employee e, TransitionContext ctx) => throw NotAllowed(Entity, "withdraw the notice of");

    [Transition("leave")]
    public virtual EmployeeState Leave(Employee e, TransitionContext ctx) => throw NotAllowed(Entity, "offboard");

    protected static void RequireHr(TransitionContext ctx)
    {
        if (!ctx.Actor.Is(PortalRole.Hr) && !ctx.Actor.IsAdmin) throw new ForbiddenException("Only HR or an administrator can change the employment status.");
    }

    protected static EmployeeState NoticeTo(Employee e, DateOnly endDate, TransitionContext ctx)
    {
        RequireHr(ctx);
        if (endDate < e.StartDate) throw ValidationFailedException.For("endDate", "The end date is before the start date.");
        e.EndDate = endDate;
        return Offboarding;
    }

    public static readonly EmployeeState Preboarding = new PreboardingState();
    public static readonly EmployeeState Active = new ActiveState();
    public static readonly EmployeeState OnLeave = new OnLeaveState();
    public static readonly EmployeeState Offboarding = new OffboardingState();
    public static readonly EmployeeState Left = new LeftState();

    private static readonly Dictionary<EmployeeStatus, EmployeeState> All =
        new[] { Preboarding, Active, OnLeave, Offboarding, Left }.ToDictionary(s => s.Status);

    public static EmployeeState Of(EmployeeStatus status) => All[status];

    private sealed class PreboardingState : EmployeeState
    {
        public override EmployeeStatus Status => EmployeeStatus.Preboarding;

        /// <summary>Normally done by the daily job on the start date; HR can also activate early.</summary>
        public override EmployeeState Activate(Employee e, TransitionContext ctx)
        {
            if (ctx.Actor != Actor.System) RequireHr(ctx);
            return Active;
        }

        public override EmployeeState GiveNotice(Employee e, DateOnly endDate, TransitionContext ctx) => NoticeTo(e, endDate, ctx);
    }

    private sealed class ActiveState : EmployeeState
    {
        public override EmployeeStatus Status => EmployeeStatus.Active;

        public override EmployeeState StartLeave(Employee e, TransitionContext ctx)
        {
            RequireHr(ctx);
            return OnLeave;
        }

        public override EmployeeState GiveNotice(Employee e, DateOnly endDate, TransitionContext ctx) => NoticeTo(e, endDate, ctx);
    }

    private sealed class OnLeaveState : EmployeeState
    {
        public override EmployeeStatus Status => EmployeeStatus.OnLeave;

        public override EmployeeState ReturnFromLeave(Employee e, TransitionContext ctx)
        {
            RequireHr(ctx);
            return Active;
        }

        public override EmployeeState GiveNotice(Employee e, DateOnly endDate, TransitionContext ctx) => NoticeTo(e, endDate, ctx);
    }

    private sealed class OffboardingState : EmployeeState
    {
        public override EmployeeStatus Status => EmployeeStatus.Offboarding;

        public override EmployeeState WithdrawNotice(Employee e, TransitionContext ctx)
        {
            RequireHr(ctx);
            if (e.Type == EmploymentType.External)
                throw new BusinessRuleException("An external contract cannot be open-ended - extend the end date instead.", "employee.external_end_date");
            e.EndDate = null;
            return Active;
        }

        /// <summary>Done by the daily job on the end date. Removing access is the application service's job.</summary>
        public override EmployeeState Leave(Employee e, TransitionContext ctx)
        {
            if (ctx.Actor != Actor.System) RequireHr(ctx);
            return Left;
        }
    }

    private sealed class LeftState : EmployeeState
    {
        public override EmployeeStatus Status => EmployeeStatus.Left;
    }
}
