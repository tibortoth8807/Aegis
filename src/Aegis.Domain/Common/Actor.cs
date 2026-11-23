namespace Aegis.Domain.Common;

/// <summary>Application roles of a user of Aegis itself (not to be confused with roles granted in other systems).</summary>
[Flags]
public enum PortalRole
{
    None = 0,
    Employee = 1,
    Manager = 2,
    Hr = 4,
    SecurityOfficer = 8,
    Admin = 16,
}

/// <summary>Who performs an action. Every state transition records its actor.</summary>
public sealed record Actor(int EmployeeId, string Name, PortalRole Roles)
{
    public bool Is(PortalRole role) => (Roles & role) == role;

    public bool IsAdmin => Is(PortalRole.Admin);

    /// <summary>Used for automatic transitions (expiry jobs, imports).</summary>
    public static readonly Actor System = new(0, "System", PortalRole.Admin);
}

/// <summary>One recorded state change, kept for audit.</summary>
public sealed class StateChange
{
    public int Id { get; set; }
    public DateTimeOffset At { get; set; }
    public int ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Comment { get; set; }
}
