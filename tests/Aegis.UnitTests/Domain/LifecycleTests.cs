using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.Training;
using static Aegis.UnitTests.Domain.People;

namespace Aegis.UnitTests.Domain;

[TestClass]
public class EmployeeLifecycleTests
{
    private readonly People _p = new();
    private static readonly DateOnly Today = new(2026, 11, 23);

    [TestMethod]
    public void FutureStartDate_StartsInPreboarding_ThenTheDailyJobActivates()
    {
        var e = Employee.Hire(new Employee { FirstName = "New", LastName = "Joiner", StartDate = Today.AddDays(14) }, Today, Ctx(_p.Hr));
        Assert.AreEqual(EmployeeStatus.Preboarding, e.Status);
        e.Activate(Ctx(Actor.System));
        Assert.AreEqual(EmployeeStatus.Active, e.Status);
    }

    [TestMethod]
    public void ExternalEmployees_NeedAnEndDateAndASponsor()
    {
        var data = new Employee { FirstName = "Ext", LastName = "Ernal", Type = EmploymentType.External, StartDate = Today, ExternalCompany = "Contoso" };
        var e = Assert.ThrowsException<BusinessRuleException>(() => Employee.Hire(data, Today, Ctx(_p.Hr)));
        Assert.AreEqual("employee.external_end_date", e.Code);
        data.EndDate = Today.AddMonths(6);
        Assert.AreEqual("employee.external_sponsor", Assert.ThrowsException<BusinessRuleException>(() => Employee.Hire(data, Today, Ctx(_p.Hr))).Code);
        data.ManagerId = _p.Manager.Id;
        Assert.AreEqual(EmployeeStatus.Active, Employee.Hire(data, Today, Ctx(_p.Hr)).Status);
    }

    [TestMethod]
    public void LeaveAndReturn_AreHrOnly()
    {
        var e = _p.Alice;
        Assert.ThrowsException<ForbiddenException>(() => e.StartLeave(Ctx(_p.Manager)));
        e.StartLeave(Ctx(_p.Hr, "Parental leave"));
        Assert.AreEqual(EmployeeStatus.OnLeave, e.Status);
        e.ReturnFromLeave(Ctx(_p.Hr));
        Assert.AreEqual(EmployeeStatus.Active, e.Status);
    }

    [TestMethod]
    public void Notice_SetsTheEndDate_AndCanBeWithdrawn()
    {
        var e = _p.Bob;
        e.GiveNotice(Today.AddDays(30), Ctx(_p.Hr));
        Assert.AreEqual(EmployeeStatus.Offboarding, e.Status);
        Assert.AreEqual(Today.AddDays(30), e.EndDate);
        e.WithdrawNotice(Ctx(_p.Hr));
        Assert.AreEqual(EmployeeStatus.Active, e.Status);
        Assert.IsNull(e.EndDate);
    }

    [TestMethod]
    public void Left_IsFinal()
    {
        var e = _p.Bob;
        e.GiveNotice(Today, Ctx(_p.Hr));
        e.Leave(Ctx(Actor.System));
        Assert.IsTrue(e.State.IsFinal);
        Assert.ThrowsException<InvalidStateTransitionException>(() => e.ReturnFromLeave(Ctx(_p.Hr)));
        Assert.AreEqual(3, e.History.Count);   // hire, give-notice, leave
    }
}

[TestClass]
public class TrainingAssignmentTests
{
    private readonly People _p = new();
    private static readonly DateOnly Today = new(2026, 11, 23);
    private readonly Course _gdpr = new() { Id = 1, Code = "DP-101", Title = "Data protection basics", DueDays = 30, ValidityMonths = 12, PassScore = 80 };

    [TestMethod]
    public void PassingCompletes_WithAValidity()
    {
        var a = TrainingAssignment.Assign(_p.Alice, _gdpr, Today, Ctx(_p.Hr));
        Assert.AreEqual(Today.AddDays(30), a.DueOn);
        a.Start(Ctx(_p.Alice));
        a.Complete(92, Ctx(_p.Alice));
        Assert.AreEqual(AssignmentStatus.Completed, a.Status);
        Assert.AreEqual(Today.AddMonths(12), a.ValidUntil);
        Assert.IsTrue(a.IsValid(Today));
    }

    [TestMethod]
    public void FailingKeepsItOpen_AndCountsAttempts()
    {
        var a = TrainingAssignment.Assign(_p.Alice, _gdpr, Today, Ctx(_p.Hr));
        a.Complete(60, Ctx(_p.Alice));
        Assert.AreEqual(AssignmentStatus.InProgress, a.Status);
        a.Complete(85, Ctx(_p.Alice));
        Assert.AreEqual(AssignmentStatus.Completed, a.Status);
        Assert.AreEqual(2, a.Attempts);
    }

    [TestMethod]
    public void OverdueOnlyAfterTheDueDate()
    {
        var a = TrainingAssignment.Assign(_p.Alice, _gdpr, Today, Ctx(_p.Hr));
        Assert.ThrowsException<BusinessRuleException>(() => a.MarkOverdue(Ctx(Actor.System)));
        a.MarkOverdue(Ctx(Actor.System, at: Now.AddDays(31)));
        Assert.AreEqual(AssignmentStatus.Overdue, a.Status);
        a.Complete(50, Ctx(_p.Alice, at: Now.AddDays(32)));
        Assert.AreEqual(AssignmentStatus.Overdue, a.Status, "a failed attempt keeps it overdue");
    }

    [TestMethod]
    public void ExpiredCompletion_CanBeRetaken()
    {
        var a = TrainingAssignment.Assign(_p.Alice, _gdpr, Today, Ctx(_p.Hr));
        a.Complete(100, Ctx(_p.Alice));
        a.Expire(Ctx(Actor.System, at: Now.AddMonths(13)));
        Assert.AreEqual(AssignmentStatus.Expired, a.Status);
        a.Start(Ctx(_p.Alice, at: Now.AddMonths(13)));
        Assert.AreEqual(AssignmentStatus.InProgress, a.Status);
    }

    [TestMethod]
    public void OthersCannotRecordMyProgress_ButHrCanWaive()
    {
        var a = TrainingAssignment.Assign(_p.Alice, _gdpr, Today, Ctx(_p.Hr));
        Assert.ThrowsException<ForbiddenException>(() => a.Complete(100, Ctx(_p.Bob)));
        Assert.ThrowsException<ValidationFailedException>(() => a.Waive(Ctx(_p.Hr)));
        a.Waive(Ctx(_p.Hr, "Completed the same course at the previous employer"));
        Assert.IsTrue(a.State.IsFinal);
    }
}

[TestClass]
public class IncidentTests
{
    private readonly People _p = new();

    [TestMethod]
    public void TicketLifecycle_WithHoldAndReopen()
    {
        var i = Incident.Open(_p.Alice, "CRM login fails", "Since this morning", IncidentPriority.P2, "access", Ctx(_p.Alice));
        Assert.AreEqual(Now.AddHours(8), i.DueAt);
        Assert.ThrowsException<ForbiddenException>(() => i.Take(Ctx(_p.Bob)));
        i.Take(Ctx(_p.Admin));
        Assert.AreEqual(_p.Admin.Id, i.AssigneeId);
        i.Hold(Ctx(_p.Admin, "Waiting for the CRM vendor"));
        i.Resume(Ctx(_p.Admin));
        i.Resolve(Ctx(_p.Admin, "Password expired, reset"));
        i.Reopen(Ctx(_p.Alice));
        Assert.IsNull(i.Resolution);
        i.Resolve(Ctx(_p.Admin, "Account unlocked"));
        i.Close(Ctx(_p.Alice));
        Assert.AreEqual(IncidentStatus.Closed, i.Status);
    }

    [TestMethod]
    public void AutoClose_WaitsFiveDays()
    {
        var i = Incident.Open(_p.Alice, "Wrong manager", "", IncidentPriority.P4, "employee-data", Ctx(_p.Alice));
        i.Take(Ctx(_p.Hr));
        i.Resolve(Ctx(_p.Hr, "Fixed in the record"));
        Assert.ThrowsException<BusinessRuleException>(() => i.Close(Ctx(Actor.System, at: Now.AddDays(2))));
        i.Close(Ctx(Actor.System, at: Now.AddDays(6)));
        Assert.AreEqual(IncidentStatus.Closed, i.Status);
    }
}

[TestClass]
public class StateMachineTests
{
    [TestMethod]
    public void EveryStatusOfEveryWorkflowHasAStateClass()
    {
        foreach (var s in Enum.GetValues<EmployeeStatus>()) Assert.AreEqual(s, EmployeeState.Of(s).Status);
        foreach (var s in Enum.GetValues<AssignmentStatus>()) Assert.AreEqual(s, TrainingAssignmentState.Of(s).Status);
        foreach (var s in Enum.GetValues<IncidentStatus>()) Assert.AreEqual(s, IncidentState.Of(s).Status);
    }

    [TestMethod]
    public void Humanize_SplitsPascalCase() => Assert.AreEqual("pending security", StateBase<IncidentStatus>.Humanize("PendingSecurity"));
}
