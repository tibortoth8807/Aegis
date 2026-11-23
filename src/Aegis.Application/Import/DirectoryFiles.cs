using System.Text;
using Aegis.Domain.Common;

namespace Aegis.Application.Import;

/// <summary>
/// Reads an LDIF export of the directory (RFC 2849: "attr: value", base64 "attr:: …", folded lines).
/// Attribute mapping follows inetOrgPerson; the manager is resolved from the RDN of its DN (uid= or employeeNumber=).
/// </summary>
public sealed class LdifDirectorySource(string fileName, string content) : IDirectorySource
{
    public string Name => fileName;

    public Task<IReadOnlyList<DirectoryRecord>> ReadAsync(CancellationToken ct) => Task.FromResult(Parse(content));

    public static IReadOnlyList<DirectoryRecord> Parse(string ldif)
    {
        var records = new List<DirectoryRecord>();
        foreach (var entry in Entries(ldif))
        {
            if (!entry.ContainsKey("employeenumber") && !entry.ContainsKey("uid")) continue;   // groups, OUs...
            string? Get(string a) => entry.TryGetValue(a, out var v) ? v[0] : null;
            var uac = int.TryParse(Get("useraccountcontrol"), out var u) ? u : 0;
            records.Add(new DirectoryRecord(
                Get("employeenumber") ?? Get("uid") ?? "",
                Get("givenname") ?? "", Get("sn") ?? "", Get("mail") ?? "", Get("title") ?? "",
                ManagerNumber(Get("manager")), Get("department") ?? Get("ou"), Get("l"), Get("c"),
                string.Equals(Get("employeetype"), "external", StringComparison.OrdinalIgnoreCase), Get("o"),
                string.Equals(Get("nsaccountlock"), "true", StringComparison.OrdinalIgnoreCase) || (uac & 2) == 2));
        }
        return records;
    }

    /// <summary>"uid=E10001,ou=people,dc=aegis,dc=example" → "E10001".</summary>
    public static string? ManagerNumber(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        var rdn = dn.Split(',')[0];
        var eq = rdn.IndexOf('=');
        return eq < 0 ? null : rdn[(eq + 1)..].Trim();
    }

    private static IEnumerable<Dictionary<string, List<string>>> Entries(string ldif)
    {
        var current = new Dictionary<string, List<string>>();
        var lines = Unfold(ldif);
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (current.Count > 0) yield return current;
                current = new Dictionary<string, List<string>>();
                continue;
            }
            if (line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw ValidationFailedException.For("file", $"Not an LDIF line: \"{line}\"");
            var name = line[..colon].Trim().ToLowerInvariant();
            if (name == "version") continue;
            string value;
            if (colon + 1 < line.Length && line[colon + 1] == ':')
            {
                try { value = Encoding.UTF8.GetString(Convert.FromBase64String(line[(colon + 2)..].Trim())); }
                catch (FormatException) { throw ValidationFailedException.For("file", $"Invalid base64 value for {name}."); }
            }
            else value = line[(colon + 1)..].Trim();
            if (!current.TryGetValue(name, out var list)) current[name] = list = [];
            list.Add(value);
        }
        if (current.Count > 0) yield return current;
    }

    private static List<string> Unfold(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith(' ') && result.Count > 0 && result[^1].Length > 0) result[^1] += raw[1..];
            else result.Add(raw.TrimEnd());
        }
        return result;
    }
}

/// <summary>
/// Reads a CSV export (HR system, spreadsheet). The header names the columns, in any order:
/// employeeNumber, firstName, lastName, email, jobTitle, managerNumber, team, location, country, type, company, disabled.
/// The separator (';' or ',') is detected from the header.
/// </summary>
public sealed class CsvDirectorySource(string fileName, string content) : IDirectorySource
{
    private static readonly string[] Required = ["employeenumber", "firstname", "lastname", "email"];

    public string Name => fileName;

    public Task<IReadOnlyList<DirectoryRecord>> ReadAsync(CancellationToken ct) => Task.FromResult(Parse(content));

    public static IReadOnlyList<DirectoryRecord> Parse(string csv)
    {
        var text = csv.TrimStart('﻿');
        var firstLine = text.Split('\n')[0];
        var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = Rows(text, sep).Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0)).ToList();
        if (rows.Count == 0) return [];
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "")).ToList();
        var missing = Required.Where(r => !header.Contains(r)).ToList();
        if (missing.Count > 0) throw ValidationFailedException.For("file", $"Missing column(s): {string.Join(", ", missing)}.");
        string? Cell(List<string> row, string col)
        {
            var i = header.IndexOf(col);
            return i >= 0 && i < row.Count && row[i].Trim().Length > 0 ? row[i].Trim() : null;
        }
        return rows.Skip(1).Select(r => new DirectoryRecord(
            Cell(r, "employeenumber") ?? "", Cell(r, "firstname") ?? "", Cell(r, "lastname") ?? "", Cell(r, "email") ?? "", Cell(r, "jobtitle") ?? "",
            Cell(r, "managernumber"), Cell(r, "team"), Cell(r, "location"), Cell(r, "country"),
            string.Equals(Cell(r, "type"), "external", StringComparison.OrdinalIgnoreCase), Cell(r, "company"),
            Cell(r, "disabled") is "true" or "yes" or "1")).ToList();
    }

    /// <summary>RFC 4180: quoted fields may contain separators, quotes ("") and line breaks.</summary>
    private static IEnumerable<List<string>> Rows(string text, char sep)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                yield return row;
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().TrimEnd('\r'));
            yield return row;
        }
    }
}
