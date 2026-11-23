using Aegis.Domain.Common;
using Aegis.Domain.Employees;

namespace Aegis.Domain.Training;

/// <summary>A course employees must (or may) complete, e.g. data protection or a system-specific qualification.</summary>
public sealed class Course
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Assigned to everyone automatically on hire.</summary>
    public bool Mandatory { get; set; }
    /// <summary>Days to complete after assignment.</summary>
    public int DueDays { get; set; } = 30;
    /// <summary>How long a completion is valid; null = forever.</summary>
    public int? ValidityMonths { get; set; }
    /// <summary>Pass mark in percent (0 = attendance only).</summary>
    public int PassScore { get; set; }
}

public enum AssignmentStatus
{
    Assigned,
    InProgress,
    Completed,
    Overdue,
    /// <summary>A completion that is no longer valid - must be taken again.</summary>
    Expired,
    Waived,
}

public sealed class TrainingAssignment
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public int CourseId { get; set; }
    public Course? Course { get; set; }
    public DateOnly AssignedOn { get; set; }
    public DateOnly DueOn { get; set; }
    public DateOnly? CompletedOn { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public int? Score { get; set; }
    public int Attempts { get; set; }

    public AssignmentStatus Status { get; private set; }
    public List<StateChange> History { get; private set; } = [];

    public TrainingAssignmentState State => TrainingAssignmentState.Of(Status);

    /// <summary>Holds a valid completion today (what access rules check).</summary>
    public bool IsValid(DateOnly today) => Status == AssignmentStatus.Completed && (ValidUntil is null || ValidUntil >= today);

    public static TrainingAssignment Assign(Employee employee, Course course, DateOnly today, TransitionContext ctx)
    {
        var a = new TrainingAssignment
        {
            EmployeeId = employee.Id, Employee = employee, CourseId = course.Id, Course = course,
            AssignedOn = today, DueOn = today.AddDays(course.DueDays), Status = AssignmentStatus.Assigned,
        };
        a.History.Add(new StateChange { At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = "assign", From = "", To = nameof(AssignmentStatus.Assigned) });
        return a;
    }

    public void Start(TransitionContext ctx) => Apply("start", ctx, s => s.Start(this, ctx));

    public void Complete(int score, TransitionContext ctx) => Apply("complete", ctx, s => s.Complete(this, score, ctx));

    public void MarkOverdue(TransitionContext ctx) => Apply("mark-overdue", ctx, s => s.MarkOverdue(this, ctx));

    public void Expire(TransitionContext ctx) => Apply("expire", ctx, s => s.Expire(this, ctx));

    public void Waive(TransitionContext ctx) => Apply("waive", ctx, s => s.Waive(this, ctx));

    private void Apply(string action, TransitionContext ctx, Func<TrainingAssignmentState, TrainingAssignmentState> transition)
    {
        var from = State;
        var to = transition(from);
        Status = to.Status;
        History.Add(new StateChange
        {
            At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = action,
            From = from.Status.ToString(), To = to.Status.ToString(), Comment = ctx.Comment,
        });
    }
}
