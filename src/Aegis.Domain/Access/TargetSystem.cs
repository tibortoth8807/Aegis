namespace Aegis.Domain.Access;

/// <summary>An application whose access Aegis manages (shown as "Applications" in the UI).</summary>
public sealed class TargetSystem
{
    public int Id { get; set; }
    /// <summary>Short code, e.g. "CRM".</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Application owner: the second approver of every request.</summary>
    public int OwnerId { get; set; }
    /// <summary>Training that must be completed before any role of this system can be granted (optional).</summary>
    public int? RequiredTrainingId { get; set; }
    /// <summary>How access is provisioned: "ldap-group", "manual", "api".</summary>
    public string Provisioning { get; set; } = "manual";
    public List<SystemRole> Roles { get; set; } = [];
}

/// <summary>A role (permission set) in a target system.</summary>
public sealed class SystemRole
{
    public int Id { get; set; }
    public int SystemId { get; set; }
    public TargetSystem? System { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Privileged roles need an extra security-officer approval and are granted for a short time only.</summary>
    public bool Privileged { get; set; }
    /// <summary>How long a grant is valid before it must be requested again.</summary>
    public int ValidityDays { get; set; } = 365;
    /// <summary>LDAP group (or similar) that provisioning adds the user to.</summary>
    public string? ProvisioningGroup { get; set; }
}

/// <summary>An access a person currently has (or had): the result of a provisioned request.</summary>
public sealed class AccessGrant
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public int RoleId { get; set; }
    public SystemRole? Role { get; set; }
    public int RequestId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    /// <summary>Set when the grant ended early (revoked) or expired.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => EndedAt is null && ValidUntil > now;
}
