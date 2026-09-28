using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DemoAIDynamicSession.ApiService.SemanticKernelDemo;

internal static class ExcelContactsReader
{
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static IReadOnlyList<ExtractedContact> Read(byte[] workbook)
    {
        using var stream = new MemoryStream(workbook);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var worksheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("Il file Excel non contiene il primo foglio di lavoro.");

        var sharedStrings = ReadSharedStrings(archive);
        using var worksheetStream = worksheetEntry.Open();
        var worksheet = XDocument.Load(worksheetStream);

        var rows = worksheet
            .Descendants(SpreadsheetNamespace + "row")
            .Select(row => ReadRow(row, sharedStrings))
            .Where(row => row.Count > 0)
            .ToArray();

        if (rows.Length < 2)
        {
            throw new InvalidOperationException("Il foglio Excel non contiene righe di contatti.");
        }

        var headers = rows[0]
            .ToDictionary(
                cell => NormalizeHeader(cell.Value),
                cell => cell.Key,
                StringComparer.OrdinalIgnoreCase);

        var firstNameColumn = FindColumn(headers, "nome", "firstname", "first name");
        var lastNameColumn = FindColumn(headers, "cognome", "lastname", "last name");
        var companyColumn = FindColumn(headers, "azienda", "company", "societa");
        var roleColumn = FindColumn(headers, "ruolo", "role", "posizione");
        var emailColumn = FindColumn(headers, "email", "e-mail");
        var phoneColumn = FindColumn(headers, "telefono", "phone", "cellulare");

        return rows
            .Skip(1)
            .Select(row => new ExtractedContact(
                GetCell(row, firstNameColumn),
                GetCell(row, lastNameColumn),
                GetCell(row, companyColumn),
                GetCell(row, roleColumn),
                GetCell(row, emailColumn),
                GetCell(row, phoneColumn)))
            .Where(contact =>
                !string.IsNullOrWhiteSpace(contact.FirstName) ||
                !string.IsNullOrWhiteSpace(contact.LastName) ||
                !string.IsNullOrWhiteSpace(contact.Email) ||
                !string.IsNullOrWhiteSpace(contact.Phone))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);

        return document
            .Descendants(SpreadsheetNamespace + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static Dictionary<int, string> ReadRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new Dictionary<int, string>();

        foreach (var cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            var reference = cell.Attribute("r")?.Value;
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            var column = ColumnIndex(reference);
            var type = cell.Attribute("t")?.Value;
            var value = type switch
            {
                "inlineStr" => string.Concat(
                    cell.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)),
                "s" => ReadSharedString(cell, sharedStrings),
                _ => cell.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty,
            };

            values[column] = value;
        }

        return values;
    }

    private static string ReadSharedString(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var value = cell.Element(SpreadsheetNamespace + "v")?.Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
               index >= 0 &&
               index < sharedStrings.Count
            ? sharedStrings[index]
            : string.Empty;
    }

    private static int ColumnIndex(string cellReference)
    {
        var index = 0;
        foreach (var character in cellReference.TakeWhile(char.IsLetter))
        {
            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return index - 1;
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> headers, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (headers.TryGetValue(NormalizeHeader(alias), out var column))
            {
                return column;
            }
        }

        return -1;
    }

    private static string GetCell(IReadOnlyDictionary<int, string> row, int column) =>
        column >= 0 && row.TryGetValue(column, out var value) ? value.Trim() : string.Empty;

    private static string NormalizeHeader(string value) =>
        new(value
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
