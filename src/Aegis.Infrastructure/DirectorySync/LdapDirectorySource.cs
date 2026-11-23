using System.DirectoryServices.Protocols;
using Aegis.Application.Import;
using Aegis.Infrastructure.Provisioning;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.DirectorySync;

/// <summary>Reads people directly from the LDAP server (paged search, inetOrgPerson attributes).</summary>
public sealed class LdapDirectorySource(IOptions<LdapOptions> options) : IDirectorySource
{
    private static readonly string[] Attributes =
        ["uid", "employeeNumber", "givenName", "sn", "mail", "title", "manager", "department", "ou", "l", "c", "employeeType", "o", "nsAccountLock", "userAccountControl"];

    public string Name => $"LDAP {options.Value.Server}";

    public Task<IReadOnlyList<DirectoryRecord>> ReadAsync(CancellationToken ct) => Task.Run<IReadOnlyList<DirectoryRecord>>(() =>
    {
        var o = options.Value;
        if (!o.Enabled) throw new InvalidOperationException("LDAP is not configured (Ldap:Server).");
        using var c = o.Connect();
        var records = new List<DirectoryRecord>();
        var paging = new PageResultRequestControl(500);
        var request = new SearchRequest(o.PeopleBase, "(objectClass=inetOrgPerson)", SearchScope.Subtree, Attributes);
        request.Controls.Add(paging);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var response = (SearchResponse)c.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                string? Get(string name) => entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0 ? entry.Attributes[name][0]?.ToString() : null;
                var uac = int.TryParse(Get("userAccountControl"), out var u) ? u : 0;
                records.Add(new DirectoryRecord(
                    Get("employeeNumber") ?? Get("uid") ?? "", Get("givenName") ?? "", Get("sn") ?? "", Get("mail") ?? "", Get("title") ?? "",
                    LdifDirectorySource.ManagerNumber(Get("manager")), Get("department") ?? Get("ou"), Get("l"), Get("c"),
                    string.Equals(Get("employeeType"), "external", StringComparison.OrdinalIgnoreCase), Get("o"),
                    string.Equals(Get("nsAccountLock"), "true", StringComparison.OrdinalIgnoreCase) || (uac & 2) == 2));
            }
            var cookie = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault()?.Cookie;
            if (cookie is null || cookie.Length == 0) break;
            paging.Cookie = cookie;
        }
        return records;
    }, ct);
}
