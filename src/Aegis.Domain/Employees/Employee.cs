using Aegis.Domain.Common;
using Aegis.Domain.Organization;

namespace Aegis.Domain.Employees;

public enum EmployeeStatus
{
    /// <summary>Contract signed, start date in the future: accounts are prepared but not active.</summary>
    Preboarding,
    Active,
    OnLeave,
    /// <summary>Notice given, end date set: access is reviewed and removed by the end date.</summary>
    Offboarding,
    Left,
}

public enum EmploymentType
{
    Internal,
    /// <summary>Contractor or partner employee - always has an end date and a sponsoring manager.</summary>
    External,
}

public sealed class Employee
{
    public int Id { get; set; }
    /// <summary>Unique personnel number, e.g. "E10042" (externals: "X20007").</summary>
    public string EmployeeNumber { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public EmploymentType Type { get; set; }
    /// <summary>Employer of an external.</summary>
    public string? ExternalCompany { get; set; }
    public string? NationalityCode { get; set; }
    public Country? Nationality { get; set; }
    public int? LocationId { get; set; }
    public Location? Location { get; set; }
    public int? TeamId { get; set; }
    public Team? Team { get; set; }
    public int? ManagerId { get; set; }
    public Employee? Manager { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    /// <summary>Aegis portal roles of this person.</summary>
    public PortalRole PortalRoles { get; set; } = PortalRole.Employee;
    /// <summary>Source of the record: "manual", "ldap", "csv".</summary>
    public string Source { get; set; } = "manual";

    public EmployeeStatus Status { get; private set; }
    public List<StateChange> History { get; private set; } = [];

    /// <summary>Optimistic concurrency token (two admins editing the same person).</summary>
    public int Version { get; set; }

    public string FullName => $"{FirstName} {LastName}";

    public EmployeeState State => EmployeeState.Of(Status);

    public Actor AsActor() => new(Id, FullName, PortalRoles);

    /// <summary>Creates a new hire. The start date decides whether they start in preboarding or active.</summary>
    public static Employee Hire(Employee data, DateOnly today, TransitionContext ctx)
    {
        if (data.Type == EmploymentType.External)
        {
            if (data.EndDate is null) throw new BusinessRuleException("An external employee needs a contract end date.", "employee.external_end_date");
            if (data.ManagerId is null) throw new BusinessRuleException("An external employee needs a sponsoring manager.", "employee.external_sponsor");
            if (string.IsNullOrWhiteSpace(data.ExternalCompany)) throw ValidationFailedException.For(nameof(ExternalCompany), "Required for external employees.");
        }
        if (data.EndDate is { } end && end < data.StartDate) throw ValidationFailedException.For(nameof(EndDate), "The end date is before the start date.");
        data.Status = data.StartDate > today ? EmployeeStatus.Preboarding : EmployeeStatus.Active;
        data.History.Add(new StateChange { At = ctx.Now, ActorId = ctx.Actor.EmployeeId, ActorName = ctx.Actor.Name, Action = "hire", From = "", To = data.Status.ToString(), Comment = ctx.Comment });
        return data;
    }

    public void Activate(TransitionContext ctx) => Apply("activate", ctx, s => s.Activate(this, ctx));

    public void StartLeave(TransitionContext ctx) => Apply("start-leave", ctx, s => s.StartLeave(this, ctx));

    public void ReturnFromLeave(TransitionContext ctx) => Apply("return", ctx, s => s.ReturnFromLeave(this, ctx));

    public void GiveNotice(DateOnly endDate, TransitionContext ctx) => Apply("give-notice", ctx, s => s.GiveNotice(this, endDate, ctx));

    public void WithdrawNotice(TransitionContext ctx) => Apply("withdraw-notice", ctx, s => s.WithdrawNotice(this, ctx));

    public void Leave(TransitionContext ctx) => Apply("leave", ctx, s => s.Leave(this, ctx));

    private void Apply(string action, TransitionContext ctx, Func<EmployeeState, EmployeeState> transition)
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
