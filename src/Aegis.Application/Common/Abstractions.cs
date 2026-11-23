using Aegis.Domain.Access;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Domain.Incidents;
using Aegis.Domain.News;
using Aegis.Domain.Organization;
using Aegis.Domain.Training;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Application.Common;

/// <summary>The database as the use cases see it (implemented by the EF Core context in Infrastructure).</summary>
public interface IAegisDbContext
{
    DbSet<Employee> Employees { get; }
    DbSet<Department> Departments { get; }
    DbSet<Team> Teams { get; }
    DbSet<Location> Locations { get; }
    DbSet<Country> Countries { get; }
    DbSet<TargetSystem> Systems { get; }
    DbSet<SystemRole> Roles { get; }
    DbSet<AccessRequest> AccessRequests { get; }
    DbSet<AccessGrant> Grants { get; }
    DbSet<Course> Courses { get; }
    DbSet<TrainingAssignment> Assignments { get; }
    DbSet<Incident> Incidents { get; }
    DbSet<NewsItem> News { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>The signed-in user.</summary>
public interface ICurrentUser
{
    Actor Actor { get; }
}

/// <summary>
/// Puts access into effect in the target system (an LDAP group, a REST call, a ticket for manual work).
/// Infrastructure has an LDAP-group implementation and a logging one for development.
/// </summary>
public interface IProvisioningGateway
{
    Task GrantAsync(Employee employee, SystemRole role, CancellationToken ct);

    Task RevokeAsync(Employee employee, SystemRole role, CancellationToken ct);
}

/// <summary>A large language model behind the assistant (Ollama, OpenAI-compatible...). Optional.</summary>
public interface IChatModel
{
    string Name { get; }

    /// <summary>Answers a question using the given facts as context. Throws when the model is unreachable.</summary>
    Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> conversation, CancellationToken ct);
}

public sealed record ChatTurn(string Role, string Text);

/// <summary>Page of a list.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int Total, int PageNumber, int PageSize);

public sealed record PageQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Sort = null, bool Desc = false)
{
    public int Skip => (Math.Max(1, Page) - 1) * Size;

    public int Size => Math.Clamp(PageSize, 1, 200);
}

public static class Clock
{
    public static TransitionContext Context(this ICurrentUser user, TimeProvider time, string? comment = null) =>
        new(user.Actor, time.GetUtcNow(), comment);

    public static DateOnly Today(this TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
