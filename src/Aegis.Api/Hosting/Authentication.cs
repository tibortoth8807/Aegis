using System.Security.Claims;
using System.Text.Encodings.Web;
using Aegis.Application.Common;
using Aegis.Domain.Common;
using Aegis.Domain.Employees;
using Aegis.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aegis.Api.Hosting;

/// <summary>
/// Demo sign-in: the client names the employee in the <c>X-Aegis-User</c> header (the UI has a "view as" switcher).
/// The handler loads that person and issues their portal roles as claims. In a real deployment this scheme is
/// replaced by OpenID Connect / JWT (e.g. Entra ID) that maps the token's e-mail to the employee - nothing else
/// changes, because the rest of the API only sees <see cref="ICurrentUser"/>.
/// </summary>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AegisDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Demo";
    public const string Header = "X-Aegis-User";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || string.IsNullOrWhiteSpace(value)) return AuthenticateResult.NoResult();
        var number = value.ToString().Trim().ToUpperInvariant();
        var e = await db.Employees.AsNoTracking()
            .Where(x => x.EmployeeNumber == number && x.Status != EmployeeStatus.Left)
            .Select(x => new { x.Id, x.FirstName, x.LastName, x.PortalRoles, x.EmployeeNumber })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (e is null) return AuthenticateResult.Fail($"Unknown or inactive employee {number}.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, $"{e.FirstName} {e.LastName}"),
            new("employee_number", e.EmployeeNumber),
        };
        claims.AddRange(Enum.GetValues<PortalRole>().Where(r => r != PortalRole.None && e.PortalRoles.HasFlag(r)).Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), Scheme));
    }
}

/// <summary>The signed-in user, from the claims.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private Actor? _actor;

    public Actor Actor => _actor ??= Create();

    private Actor Create()
    {
        var p = accessor.HttpContext?.User;
        if (p?.Identity?.IsAuthenticated != true) throw new ForbiddenException("Sign in first.");
        var id = int.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!, System.Globalization.CultureInfo.InvariantCulture);
        var roles = p.FindAll(ClaimTypes.Role).Select(c => Enum.Parse<PortalRole>(c.Value)).Aggregate(PortalRole.None, (a, b) => a | b);
        return new Actor(id, p.Identity.Name ?? "", roles);
    }
}
