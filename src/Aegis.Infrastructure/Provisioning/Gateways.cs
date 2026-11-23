using System.DirectoryServices.Protocols;
using System.Net;
using Aegis.Application.Common;
using Aegis.Domain.Access;
using Aegis.Domain.Employees;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Provisioning;

/// <summary>Development / demo gateway: logs what would be changed in the target systems.</summary>
public sealed class LoggingProvisioningGateway(ILogger<LoggingProvisioningGateway> log) : IProvisioningGateway
{
    public Task GrantAsync(Employee employee, SystemRole role, CancellationToken ct)
    {
        log.LogInformation("GRANT {Employee} → {System}/{Role} (group {Group})", employee.EmployeeNumber, role.System?.Code, role.Name, role.ProvisioningGroup ?? "-");
        return Task.CompletedTask;
    }

    public Task RevokeAsync(Employee employee, SystemRole role, CancellationToken ct)
    {
        log.LogInformation("REVOKE {Employee} → {System}/{Role}", employee.EmployeeNumber, role.System?.Code, role.Name);
        return Task.CompletedTask;
    }
}

public sealed class LdapOptions
{
    public const string Section = "Ldap";

    /// <summary>e.g. "ldap.company.example:636". Empty = LDAP is not used.</summary>
    public string Server { get; set; } = "";
    public bool Ssl { get; set; } = true;
    public string BindDn { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Where people are searched, e.g. "ou=people,dc=company,dc=example".</summary>
    public string PeopleBase { get; set; } = "";
    /// <summary>Where provisioning groups live, e.g. "ou=groups,dc=company,dc=example".</summary>
    public string GroupBase { get; set; } = "";
    /// <summary>RDN attribute of people entries (uid for OpenLDAP, sAMAccountName-based DNs differ in AD).</summary>
    public string UserRdn { get; set; } = "uid";

    public bool Enabled => !string.IsNullOrWhiteSpace(Server);

    public LdapConnection Connect()
    {
        var c = new LdapConnection(new LdapDirectoryIdentifier(Server))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(BindDn, Password),
            Timeout = TimeSpan.FromSeconds(15),
        };
        c.SessionOptions.ProtocolVersion = 3;
        c.SessionOptions.SecureSocketLayer = Ssl;
        c.Bind();
        return c;
    }
}

/// <summary>
/// Provisions roles that have an LDAP group by adding / removing the person's DN as a group member. Roles without a
/// group are handed to the fallback gateway (manual work, logged).
/// </summary>
public sealed class LdapGroupProvisioningGateway(IOptions<LdapOptions> options, LoggingProvisioningGateway fallback, ILogger<LdapGroupProvisioningGateway> log)
    : IProvisioningGateway
{
    private readonly LdapOptions _o = options.Value;

    public Task GrantAsync(Employee employee, SystemRole role, CancellationToken ct) => Modify(employee, role, DirectoryAttributeOperation.Add, ct);

    public Task RevokeAsync(Employee employee, SystemRole role, CancellationToken ct) => Modify(employee, role, DirectoryAttributeOperation.Delete, ct);

    private Task Modify(Employee employee, SystemRole role, DirectoryAttributeOperation op, CancellationToken ct)
    {
        if (role.ProvisioningGroup is null)
            return op == DirectoryAttributeOperation.Add ? fallback.GrantAsync(employee, role, ct) : fallback.RevokeAsync(employee, role, ct);
        return Task.Run(() =>
        {
            using var c = _o.Connect();
            var group = $"cn={role.ProvisioningGroup},{_o.GroupBase}";
            var member = $"{_o.UserRdn}={employee.EmployeeNumber},{_o.PeopleBase}";
            try
            {
                c.SendRequest(new ModifyRequest(group, op, "member", member));
                log.LogInformation("LDAP {Operation} {Member} in {Group}", op, member, group);
            }
            catch (DirectoryOperationException e) when (e.Response.ResultCode is ResultCode.AttributeOrValueExists or ResultCode.NoSuchAttribute)
            {
                // already a member / already removed: the desired state is reached
                log.LogInformation("LDAP {Operation} {Member} in {Group}: nothing to do ({Result})", op, member, group, e.Response.ResultCode);
            }
        }, ct);
    }
}
