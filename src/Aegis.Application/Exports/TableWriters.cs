using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Aegis.Application.Exports;

/// <summary>A table to export: a title, column headers and rows of cell values (string, number, date or null).</summary>
public sealed record Table(string Title, IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);

/// <summary>RFC 4180 CSV with a UTF-8 BOM (so Excel opens accents correctly) and ';' or ',' as separator.</summary>
public static class CsvWriter
{
    public static byte[] Write(Table t, char separator = ';')
    {
        var sb = new StringBuilder();
        sb.AppendJoin(separator, t.Columns.Select(c => Escape(c, separator))).Append("\r\n");
        foreach (var row in t.Rows) sb.AppendJoin(separator, row.Select(v => Escape(Format(v), separator))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static string Format(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string Escape(string s, char separator)
    {
        // formula injection: a cell starting with = + - @ would be executed by Excel
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([separator, '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>
/// Minimal Excel (.xlsx, Office Open XML) writer without dependencies: bold, coloured header row that stays frozen,
/// auto-filter, column widths from the content, numbers as numbers.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(Table t)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            var sheetName = SecurityElement.Escape(new string(t.Title.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray()));
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheets><sheet name="{sheetName}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts>
                <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF1F3A5F"/><bgColor indexed="64"/></patternFill></fill></fills>
                <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs>
                <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
                </styleSheet>
                """);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(t));
        }
        return ms.ToArray();
    }

    private static string Sheet(Table t)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""").Append('\n');
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var c = 0; c < t.Columns.Count; c++)
        {
            var width = Math.Max(t.Columns[c].Length, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => c < r.Length ? CsvWriter.Format(r[c]).Length : 0));
            sb.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{Math.Clamp(width + 2, 8, 60)}" customWidth="1"/>""");
        }
        sb.Append("</cols><sheetData>");
        AppendRow(sb, 1, t.Columns.Cast<object?>().ToArray(), header: true);
        for (var i = 0; i < t.Rows.Count; i++) AppendRow(sb, i + 2, t.Rows[i], header: false);
        sb.Append("</sheetData>");
        if (t.Columns.Count > 0) sb.Append(CultureInfo.InvariantCulture, $"""<autoFilter ref="A1:{Column(t.Columns.Count - 1)}{t.Rows.Count + 1}"/>""");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int r, object?[] cells, bool header)
    {
        sb.Append(CultureInfo.InvariantCulture, $"""<row r="{r}">""");
        for (var c = 0; c < cells.Length; c++)
        {
            var reference = $"{Column(c)}{r}";
            var style = header ? " s=\"1\"" : "";
            switch (cells[c])
            {
                case null:
                    break;
                case int or long or double or decimal or float when !header:
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}"{style}><v>{Convert.ToString(cells[c], CultureInfo.InvariantCulture)}</v></c>""");
                    break;
                default:
                    var text = SecurityElement.Escape(CsvWriter.Format(cells[c]));
                    sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" t="inlineStr"{style}><is><t xml:space="preserve">{text}</t></is></c>""");
                    break;
            }
        }
        sb.Append("</row>");
    }

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string Column(int index)
    {
        var s = "";
        for (index++; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
