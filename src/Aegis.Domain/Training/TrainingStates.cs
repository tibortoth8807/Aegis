using Aegis.Domain.Common;

namespace Aegis.Domain.Training;

/// <summary>
/// Training assignment lifecycle.
/// <code>
/// Assigned ─start─▶ InProgress ─complete (pass)─▶ Completed ─expire─▶ Expired ─start─▶ InProgress
///    │  └──────complete──────────────▲                ▲
///    └─mark-overdue─▶ Overdue ─start / complete───────┘     (any open state) ─waive─▶ Waived
/// </code>
/// A failed attempt keeps the assignment in progress and counts the attempt.
/// </summary>
public abstract class TrainingAssignmentState : StateBase<AssignmentStatus>
{
    private const string Entity = "training assignment";

    [Transition("start")]
    public virtual TrainingAssignmentState Start(TrainingAssignment a, TransitionContext ctx) => throw NotAllowed(Entity, "start");

    [Transition("complete")]
    public virtual TrainingAssignmentState Complete(TrainingAssignment a, int score, TransitionContext ctx) => throw NotAllowed(Entity, "complete");

    [Transition("mark-overdue")]
    public virtual TrainingAssignmentState MarkOverdue(TrainingAssignment a, TransitionContext ctx) => throw NotAllowed(Entity, "mark as overdue");

    [Transition("expire")]
    public virtual TrainingAssignmentState Expire(TrainingAssignment a, TransitionContext ctx) => throw NotAllowed(Entity, "expire");

    [Transition("waive")]
    public virtual TrainingAssignmentState Waive(TrainingAssignment a, TransitionContext ctx) => throw NotAllowed(Entity, "waive");

    protected static void EnsureSelfOrHr(TrainingAssignment a, TransitionContext ctx)
    {
        if (ctx.Actor.EmployeeId != a.EmployeeId && !ctx.Actor.Is(PortalRole.Hr) && !ctx.Actor.IsAdmin)
            throw new ForbiddenException("Only the employee or HR can record progress on a training.");
    }

    /// <summary>Records an attempt; passes → Completed with a validity, fails → stays in progress.</summary>
    protected static TrainingAssignmentState Attempt(TrainingAssignment a, int score, TransitionContext ctx)
    {
        EnsureSelfOrHr(a, ctx);
        if (score is < 0 or > 100) throw ValidationFailedException.For("score", "The score is a percentage between 0 and 100.");
        var course = a.Course ?? throw new InvalidOperationException("The course must be loaded.");
        a.Attempts++;
        a.Score = score;
        if (score < course.PassScore) return InProgress;
        var today = DateOnly.FromDateTime(ctx.Now.UtcDateTime);
        a.CompletedOn = today;
        a.ValidUntil = course.ValidityMonths is int months ? today.AddMonths(months) : null;
        return Completed;
    }

    protected static TrainingAssignmentState WaiveBy(TransitionContext ctx)
    {
        if (!ctx.Actor.Is(PortalRole.Hr) && !ctx.Actor.IsAdmin) throw new ForbiddenException("Only HR can waive a training.");
        if (string.IsNullOrWhiteSpace(ctx.Comment)) throw ValidationFailedException.For("comment", "Give a reason for the waiver.");
        return Waived;
    }

    public static readonly TrainingAssignmentState Assigned = new AssignedState();
    public static readonly TrainingAssignmentState InProgress = new InProgressState();
    public static readonly TrainingAssignmentState Completed = new CompletedState();
    public static readonly TrainingAssignmentState Overdue = new OverdueState();
    public static readonly TrainingAssignmentState Expired = new ExpiredState();
    public static readonly TrainingAssignmentState Waived = new WaivedState();

    private static readonly Dictionary<AssignmentStatus, TrainingAssignmentState> All =
        new[] { Assigned, InProgress, Completed, Overdue, Expired, Waived }.ToDictionary(s => s.Status);

    public static TrainingAssignmentState Of(AssignmentStatus status) => All[status];

    private sealed class AssignedState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.Assigned;

        public override TrainingAssignmentState Start(TrainingAssignment a, TransitionContext ctx)
        {
            EnsureSelfOrHr(a, ctx);
            return InProgress;
        }

        public override TrainingAssignmentState Complete(TrainingAssignment a, int score, TransitionContext ctx) => Attempt(a, score, ctx);

        public override TrainingAssignmentState MarkOverdue(TrainingAssignment a, TransitionContext ctx) =>
            DateOnly.FromDateTime(ctx.Now.UtcDateTime) > a.DueOn ? Overdue : throw new BusinessRuleException("The training is not due yet.", "training.not_due");

        public override TrainingAssignmentState Waive(TrainingAssignment a, TransitionContext ctx) => WaiveBy(ctx);
    }

    private sealed class InProgressState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.InProgress;

        public override TrainingAssignmentState Complete(TrainingAssignment a, int score, TransitionContext ctx) => Attempt(a, score, ctx);

        public override TrainingAssignmentState MarkOverdue(TrainingAssignment a, TransitionContext ctx) =>
            DateOnly.FromDateTime(ctx.Now.UtcDateTime) > a.DueOn ? Overdue : throw new BusinessRuleException("The training is not due yet.", "training.not_due");

        public override TrainingAssignmentState Waive(TrainingAssignment a, TransitionContext ctx) => WaiveBy(ctx);
    }

    private sealed class OverdueState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.Overdue;

        public override TrainingAssignmentState Start(TrainingAssignment a, TransitionContext ctx)
        {
            EnsureSelfOrHr(a, ctx);
            return InProgress;
        }

        /// <summary>A failed attempt keeps it overdue.</summary>
        public override TrainingAssignmentState Complete(TrainingAssignment a, int score, TransitionContext ctx)
        {
            var next = Attempt(a, score, ctx);
            return next == InProgress ? Overdue : next;
        }

        public override TrainingAssignmentState Waive(TrainingAssignment a, TransitionContext ctx) => WaiveBy(ctx);
    }

    private sealed class CompletedState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.Completed;

        /// <summary>Run by the daily job when the completion's validity has passed.</summary>
        public override TrainingAssignmentState Expire(TrainingAssignment a, TransitionContext ctx) =>
            a.ValidUntil is { } until && until < DateOnly.FromDateTime(ctx.Now.UtcDateTime)
                ? Expired
                : throw new BusinessRuleException("The completion is still valid.", "training.still_valid");
    }

    private sealed class ExpiredState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.Expired;

        /// <summary>Retake: a new due date from today.</summary>
        public override TrainingAssignmentState Start(TrainingAssignment a, TransitionContext ctx)
        {
            EnsureSelfOrHr(a, ctx);
            var course = a.Course ?? throw new InvalidOperationException("The course must be loaded.");
            a.DueOn = DateOnly.FromDateTime(ctx.Now.UtcDateTime).AddDays(course.DueDays);
            return InProgress;
        }
    }

    private sealed class WaivedState : TrainingAssignmentState
    {
        public override AssignmentStatus Status => AssignmentStatus.Waived;
    }
}
