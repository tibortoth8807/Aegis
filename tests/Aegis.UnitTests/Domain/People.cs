using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;

namespace Aegis.UnitTests.Domain;

/// <summary>Test data builder: a small organisation with the people every workflow test needs.</summary>
internal sealed class People
{
    public static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    public Employee Manager { get; } = Person(1, "Mara", "Manager", PortalRole.Employee | PortalRole.Manager);
    public Employee Alice { get; } = Person(2, "Alice", "Analyst", PortalRole.Employee, managerId: 1);
    public Employee Bob { get; } = Person(3, "Bob", "Builder", PortalRole.Employee, managerId: 1);
    public Employee Owner { get; } = Person(4, "Otto", "Owner", PortalRole.Employee);
    public Employee Security { get; } = Person(5, "Sofia", "Secure", PortalRole.Employee | PortalRole.SecurityOfficer);
    public Employee Admin { get; } = Person(6, "Adam", "Admin", PortalRole.Employee | PortalRole.Admin);
    public Employee Hr { get; } = Person(7, "Hanna", "Hr", PortalRole.Employee | PortalRole.Hr);

    public TargetSystem Crm { get; }
    public SystemRole CrmUser { get; }
    public SystemRole CrmAdmin { get; }

    public People()
    {
        Crm = new TargetSystem { Id = 1, Code = "CRM", Name = "Customer CRM", OwnerId = Owner.Id };
        CrmUser = new SystemRole { Id = 10, SystemId = 1, System = Crm, Name = "User", ValidityDays = 365 };
        CrmAdmin = new SystemRole { Id = 11, SystemId = 1, System = Crm, Name = "Administrator", Privileged = true, ValidityDays = 90 };
        Crm.Roles.AddRange([CrmUser, CrmAdmin]);
    }

    public static Employee Person(int id, string first, string last, PortalRole roles, int? managerId = null)
    {
        var e = new Employee
        {
            Id = id, EmployeeNumber = $"E{10000 + id}", FirstName = first, LastName = last, Email = $"{first.ToLowerInvariant()}@aegis.example",
            ManagerId = managerId, PortalRoles = roles, StartDate = new DateOnly(2024, 1, 1),
        };
        return Employee.Hire(e, new DateOnly(2026, 11, 23), Ctx(Actor.System));
    }

    public static TransitionContext Ctx(Actor actor, string? comment = null, DateTimeOffset? at = null) => new(actor, at ?? Now, comment);

    public static TransitionContext Ctx(Employee who, string? comment = null, DateTimeOffset? at = null) => Ctx(who.AsActor(), comment, at);

    public AccessRequest Request(Employee requester, Employee beneficiary, SystemRole role) =>
        AccessRequest.Create(requester, beneficiary, role, "Needed for the quarterly customer review", Ctx(requester));
}
