using System.IO.Compression;
using System.Text;
using Aegis.Application.Exports;
using Aegis.Application.Import;
using Aegis.Domain.Common;

namespace Aegis.UnitTests.Application;

[TestClass]
public class ExportFormatTests
{
    private static readonly Table Sample = new("People", ["Name", "Age", "Start", "Note"],
    [
        ["Kiss Anna", 42, new DateOnly(2024, 3, 1), "says \"hi\"; twice"],
        ["=HYPERLINK(\"x\")", 7, null, null],
    ]);

    [TestMethod]
    public void Csv_HasBom_QuotesWhenNeeded_AndDefusesFormulas()
    {
        var bytes = CsvWriter.Write(Sample);
        CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.AreEqual("Name;Age;Start;Note", lines[0]);
        Assert.AreEqual("Kiss Anna;42;2024-03-01;\"says \"\"hi\"\"; twice\"", lines[1]);
        Assert.AreEqual("\"'=HYPERLINK(\"\"x\"\")\";7;;", lines[2]);
    }

    [TestMethod]
    public void Xlsx_IsAZipWithTheSheetAndAFrozenHeader()
    {
        using var zip = new ZipArchive(new MemoryStream(XlsxWriter.Write(Sample)));
        CollectionAssert.IsSubsetOf(new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/styles.xml", "xl/worksheets/sheet1.xml" },
            zip.Entries.Select(e => e.FullName).ToArray());
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = reader.ReadToEnd();
        StringAssert.Contains(sheet, "state=\"frozen\"");
        StringAssert.Contains(sheet, "<c r=\"B2\"><v>42</v></c>");
        StringAssert.Contains(sheet, "says &quot;hi&quot;; twice");
        StringAssert.Contains(sheet, "<autoFilter ref=\"A1:D3\"/>");
    }

    [DataTestMethod]
    [DataRow(0, "A")]
    [DataRow(25, "Z")]
    [DataRow(26, "AA")]
    [DataRow(701, "ZZ")]
    [DataRow(702, "AAA")]
    public void ColumnLetters(int index, string expected) => Assert.AreEqual(expected, XlsxWriter.Column(index));
}

[TestClass]
public class DirectoryFileTests
{
    [TestMethod]
    public void Ldif_IsParsed_WithBase64FoldedLinesAndManagerDns()
    {
        const string ldif = """
            version: 1

            dn: uid=E10002,ou=people,dc=aegis,dc=example
            objectClass: inetOrgPerson
            uid: E10002
            employeeNumber: E10002
            givenName: Anna
            sn:: S2lzcyDDgWdp
            mail: anna.kiss@aegis.example
            title: Senior Analyst, Customer
              Insights
            manager: uid=E10001,ou=people,dc=aegis,dc=example
            department: Sales Analytics
            l: Budapest
            c: HU

            dn: cn=crm-users,ou=groups,dc=aegis,dc=example
            objectClass: groupOfNames
            cn: crm-users

            dn: uid=E10099,ou=people,dc=aegis,dc=example
            uid: E10099
            givenName: Old
            sn: Account
            nsAccountLock: true
            """;
        var records = LdifDirectorySource.Parse(ldif);

        Assert.AreEqual(2, records.Count, "the group entry is skipped");
        var anna = records[0];
        Assert.AreEqual("E10002", anna.EmployeeNumber);
        Assert.AreEqual("Kiss Ági", anna.LastName);
        Assert.AreEqual("Senior Analyst, Customer Insights", anna.JobTitle);
        Assert.AreEqual("E10001", anna.ManagerNumber);
        Assert.AreEqual("Sales Analytics", anna.Team);
        Assert.IsTrue(records[1].Disabled);
    }

    [TestMethod]
    public void Csv_ColumnsInAnyOrder_SemicolonDetected_QuotedFields()
    {
        const string csv = "﻿lastName;firstName;employeeNumber;email;jobTitle;team;type\r\n" +
                           "Kiss;Anna;E10002;anna@x.example;\"Analyst; senior\";Sales;internal\r\n" +
                           "Nagy;Béla;X20001;bela@contoso.example;Consultant;;external\r\n";
        var records = CsvDirectorySource.Parse(csv);
        Assert.AreEqual(2, records.Count);
        Assert.AreEqual("Analyst; senior", records[0].JobTitle);
        Assert.IsNull(records[1].Team);
        Assert.IsTrue(records[1].External);
    }

    [TestMethod]
    public void Csv_MissingRequiredColumns_IsAValidationError()
    {
        var e = Assert.ThrowsException<ValidationFailedException>(() => CsvDirectorySource.Parse("name,mail\nAnna,a@x"));
        StringAssert.Contains(e.Errors["file"][0], "employeenumber");
    }
}
