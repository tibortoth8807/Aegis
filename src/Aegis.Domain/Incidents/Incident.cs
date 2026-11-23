using Aegis.Domain.Common;
using Aegis.Domain.Employees;

namespace Aegis.Domain.Incidents;

public enum IncidentPriority { P1 = 1, P2, P3, P4 }

public enum IncidentStatus { New, InProgress, OnHold, Resolved, Closed, Cancelled }

/// <summary>A support ticket ("my access does not work", "wrong manager in my record").</summary>
public sealed class Incident
{
    /// <summary>Resolved tickets close automatically after this many days without a reopen.</summary>
    public const int AutoCloseDays = 5;

    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public IncidentPriority Priority { get; set; } = IncidentPriority.P3;
    /// <summary>e.g. "access", "employee-data", "training", "other".</summary>
    public string Category { get; set; } = "other";
    public int ReporterId { get; set; }
    public Employee? Reporter { get; set; }
    public int? AssigneeId { get; set; }
    public Employee? Assignee { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }

    public IncidentStatus Status { get; private set; }
    public List<StateChange> History { get; private set; } = [];

    public IncidentState State => IncidentState.Of(Status);

    /// <summary>Hours until the response is due (SLA), by priority.</summary>
    public static int SlaHours(IncidentPriority p) => p switch { IncidentPriority.P1 => 4, IncidentPriority.P2 => 8, IncidentPriority.P3 => 24, _ => 72 };

    public DateTimeOffset DueAt => CreatedAt.AddHours(SlaHours(Priority));

    public static Incident Open(Employee reporter, string title, string description, IncidentPriority priority, string category, TransitionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(title)) throw ValidationFailedException.For(nameof(Title), "Required.");
        var i = new Incident
        {
            ReporterId = reporter.Id, Reporter = reporter, Title = title.Trim(), Description = description.Trim(),
            Priority = priority, Category = category, CreatedAt = ctx.Now, Status = IncidentStatus.New,
        };
        i.History.Add(new StateChange { At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = "open", From = "", To = nameof(IncidentStatus.New) });
        return i;
    }

    public void Take(TransitionContext ctx) => Apply("take", ctx, s => s.Take(this, ctx));

    public void Hold(TransitionContext ctx) => Apply("hold", ctx, s => s.Hold(this, ctx));

    public void Resume(TransitionContext ctx) => Apply("resume", ctx, s => s.Resume(this, ctx));

    public void Resolve(TransitionContext ctx) => Apply("resolve", ctx, s => s.Resolve(this, ctx));

    public void Reopen(TransitionContext ctx) => Apply("reopen", ctx, s => s.Reopen(this, ctx));

    public void Close(TransitionContext ctx) => Apply("close", ctx, s => s.Close(this, ctx));

    public void Cancel(TransitionContext ctx) => Apply("cancel", ctx, s => s.Cancel(this, ctx));

    private void Apply(string action, TransitionContext ctx, Func<IncidentState, IncidentState> transition)
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
