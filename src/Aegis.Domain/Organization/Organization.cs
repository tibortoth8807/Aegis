namespace Aegis.Domain.Organization;

public sealed class Department
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    public Department? Parent { get; set; }
    public List<Team> Teams { get; set; } = [];
}

public sealed class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    /// <summary>Team lead (an employee id); the lead is the default manager of new team members.</summary>
    public int? LeadId { get; set; }
}

/// <summary>Office location.</summary>
public sealed class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string CountryCode { get; set; } = "";
}

/// <summary>Country reference data (the "Nationalities" list).</summary>
public sealed class Country
{
    /// <summary>ISO 3166-1 alpha-2, primary key.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>EU/EEA member - affects which data-protection training is mandatory.</summary>
    public bool Eea { get; set; }
}
