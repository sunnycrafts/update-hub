using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UpdateHub.Models;

namespace UpdateHub.Services;

/// <summary>
/// Writes the inventory out in four formats. XLSX and PDF are generated directly rather
/// than through a library: both are flat table reports, and hand-writing them keeps the
/// single-file executable small and avoids a native-dependency in a portable build.
/// </summary>
public sealed class ExportService
{
    private readonly LogService _log;
    public ExportService(LogService log) => _log = log;

    private static readonly (string Header, Func<InstalledApp, string> Value)[] Columns =
    {
        ("Name",                 a => a.Name),
        ("Publisher",            a => a.Publisher ?? ""),
        ("Category",             a => a.CategoryText),
        ("Size Tier",            a => Core.AppClassifier.Describe(a.SizeTier)),
        ("Installed Version",    a => a.InstalledVersion),
        ("Installed Released",   a => Date(a.InstalledReleaseDate)),
        ("Date Precision",       a => a.InstalledReleasePrecision.ToString()),
        ("Installed On",         a => Date(a.InstallDate)),
        ("Latest Version",       a => a.LatestVersion ?? ""),
        ("Latest Released",      a => Date(a.LatestReleaseDate)),
        ("Age Behind",           a => a.Age.IsKnown ? a.Age.Long : ""),
        ("Days Behind",          a => a.Age.IsKnown ? a.Age.TotalDays.ToString() : ""),
        ("Months Behind",        a => a.Age.IsKnown ? a.Age.TotalMonths.ToString() : ""),
        ("Major Versions Behind",a => a.MajorVersionsBehind.ToString()),
        ("Minor Versions Behind",a => a.MinorVersionsBehind.ToString()),
        ("Releases Missed",      a => a.ReleasesMissed.ToString()),
        ("Security Missed",      a => a.SecurityReleasesMissed.ToString()),
        ("Status",               a => a.StatusText),
        ("Update Available",     a => a.UpdateAvailable ? "Yes" : "No"),
        ("Update Method",        a => a.UpdateMethod.ToString()),
        ("Confidence",           a => a.Confidence + "%"),
        ("Sources",              a => string.Join(" / ", a.ConfidenceSources.Distinct())),
        ("Winget ID",            a => a.WingetId ?? ""),
        ("Homepage",             a => a.Homepage ?? ""),
        ("Install Location",     a => a.InstallLocation ?? ""),
        ("Size (MB)",            a => a.EstimatedSizeKb > 0 ? (a.EstimatedSizeKb / 1024.0).ToString("F1") : ""),
        ("Last Checked",         a => a.LastChecked?.ToString("yyyy-MM-dd HH:mm") ?? ""),
    };

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";

    public void Export(IEnumerable<InstalledApp> apps, string path, ExportFormat format)
    {
        var list = apps.ToList();
        _log.Info($"Exporting {list.Count} apps to {format}: {path}");

        switch (format)
        {
            case ExportFormat.Csv: WriteCsv(list, path); break;
            case ExportFormat.Json: WriteJson(list, path); break;
            case ExportFormat.Excel: WriteXlsx(list, path); break;
            case ExportFormat.Pdf: WritePdf(list, path); break;
        }
    }

    public static string DefaultExtension(ExportFormat f) => f switch
    {
        ExportFormat.Csv => ".csv",
        ExportFormat.Json => ".json",
        ExportFormat.Excel => ".xlsx",
        _ => ".pdf",
    };

    // ---- CSV -------------------------------------------------------------

    private static void WriteCsv(List<InstalledApp> apps, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns.Select(c => Csv(c.Header))));
        foreach (var app in apps)
            sb.AppendLine(string.Join(",", Columns.Select(c => Csv(c.Value(app)))));

        // UTF-8 BOM so Excel opens accented names correctly on a Windows locale.
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string value)
    {
        if (value.Length == 0) return "";
        bool needsQuotes = value.Contains(',') || value.Contains('"')
            || value.Contains('\n') || value.Contains('\r');
        var v = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return needsQuotes ? '"' + v.Replace("\"", "\"\"") + '"' : v;
    }

    // ---- JSON ------------------------------------------------------------

    private static void WriteJson(List<InstalledApp> apps, string path)
    {
        var payload = new
        {
            generated = DateTime.Now,
            machine = Environment.MachineName,
            architecture = Sources.ArchitectureHelper.Display,
            count = apps.Count,
            applications = apps.Select(a => new
            {
                name = a.Name,
                publisher = a.Publisher,
                category = a.CategoryText,
                sizeTier = a.SizeTier.ToString(),
                installedVersion = a.InstalledVersion,
                installedReleaseDate = a.InstalledReleaseDate?.ToString("yyyy-MM-dd"),
                installedReleaseDatePrecision = a.InstalledReleasePrecision.ToString(),
                installedDate = a.InstallDate?.ToString("yyyy-MM-dd"),
                latestVersion = a.LatestVersion,
                latestReleaseDate = a.LatestReleaseDate?.ToString("yyyy-MM-dd"),
                daysBehind = a.Age.IsKnown ? a.Age.TotalDays : (int?)null,
                monthsBehind = a.Age.IsKnown ? a.Age.TotalMonths : (int?)null,
                ageBehind = a.Age.IsKnown ? a.Age.Long : null,
                majorVersionsBehind = a.MajorVersionsBehind,
                minorVersionsBehind = a.MinorVersionsBehind,
                releasesMissed = a.ReleasesMissed,
                securityReleasesMissed = a.SecurityReleasesMissed,
                updateAvailable = a.UpdateAvailable,
                updateMethod = a.UpdateMethod.ToString(),
                status = a.StatusText,
                confidence = a.Confidence,
                confidenceSources = a.ConfidenceSources.Distinct(),
                wingetId = a.WingetId,
                gitHubRepo = a.GitHubRepo,
                homepage = a.Homepage,
                installLocation = a.InstallLocation,
                sizeKb = a.EstimatedSizeKb,
                lastChecked = a.LastChecked,
            }),
        };

        // No BOM: RFC 8259 forbids it, and strict parsers reject the file outright.
        // (The CSV writer deliberately does the opposite — Excel needs the BOM.)
        File.WriteAllText(path, JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ---- XLSX ------------------------------------------------------------

    /// <summary>
    /// Writes a spreadsheet as a raw OpenXML package. Values use inline strings so no
    /// shared-string table is needed, and numeric columns are written as real numbers so
    /// sorting and filtering behave in Excel.
    /// </summary>
    private static void WriteXlsx(List<InstalledApp> apps, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Write(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);

        Write(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        Write(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Installed Software" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);

        Write(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);

        // Two formats: index 0 default, index 1 bold for the header row.
        Write(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2">
                <font><sz val="11"/><name val="Calibri"/></font>
                <font><b/><sz val="11"/><name val="Calibri"/></font>
              </fonts>
              <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
              <borders count="1"><border/></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="2">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
              </cellXfs>
            </styleSheet>
            """);

        var numericColumns = new HashSet<int>();
        for (int i = 0; i < Columns.Length; i++)
        {
            var h = Columns[i].Header;
            if (h.Contains("Behind") || h.Contains("Missed") || h == "Size (MB)") numericColumns.Add(i);
        }

        var sb = new StringBuilder(apps.Count * 400);
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append("<cols>");
        for (int i = 0; i < Columns.Length; i++)
            sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{ColumnWidth(Columns[i].Header)}" customWidth="1"/>""");
        sb.Append("</cols><sheetData>");

        sb.Append("""<row r="1">""");
        for (int i = 0; i < Columns.Length; i++)
            sb.Append($"""<c r="{CellRef(i, 1)}" t="inlineStr" s="1"><is><t>{Xml(Columns[i].Header)}</t></is></c>""");
        sb.Append("</row>");

        int rowNum = 2;
        foreach (var app in apps)
        {
            sb.Append($"""<row r="{rowNum}">""");
            for (int i = 0; i < Columns.Length; i++)
            {
                var value = Columns[i].Value(app);
                var reference = CellRef(i, rowNum);
                if (value.Length == 0) continue;

                if (numericColumns.Contains(i) &&
                    double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                    sb.Append($"""<c r="{reference}"><v>{n.ToString(CultureInfo.InvariantCulture)}</v></c>""");
                else
                    sb.Append($"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{Xml(value)}</t></is></c>""");
            }
            sb.Append("</row>");
            rowNum++;
        }

        sb.Append("</sheetData>");
        sb.Append($"""<autoFilter ref="A1:{CellRef(Columns.Length - 1, Math.Max(1, rowNum - 1))}"/>""");
        sb.Append("</worksheet>");

        Write(zip, "xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static int ColumnWidth(string header) => header switch
    {
        "Name" => 34,
        "Publisher" or "Homepage" or "Install Location" or "Winget ID" or "Sources" => 28,
        "Age Behind" => 22,
        _ => 16,
    };

    private static void Write(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Converts a zero-based column index into A, B, ... Z, AA, AB.</summary>
    private static string CellRef(int columnIndex, int row)
    {
        var name = "";
        int i = columnIndex;
        do
        {
            name = (char)('A' + i % 26) + name;
            i = i / 26 - 1;
        } while (i >= 0);
        return name + row;
    }

    private static string Xml(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            // XML 1.0 forbids most control characters outright; drop them rather than
            // emit a file Excel will refuse to open.
            if (ch < 0x20 && ch is not '\t' and not '\n' and not '\r') continue;
            sb.Append(ch switch
            {
                '<' => "&lt;", '>' => "&gt;", '&' => "&amp;",
                '"' => "&quot;", '\'' => "&apos;",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    // ---- PDF -------------------------------------------------------------

    /// <summary>
    /// Emits a landscape PDF report. Uses the base-14 Helvetica faces, which every reader
    /// provides, so no font has to be embedded.
    /// </summary>
    private static void WritePdf(List<InstalledApp> apps, string path)
    {
        const double pageWidth = 841.89;   // A4 landscape
        const double pageHeight = 595.28;
        const double margin = 32;
        const double rowHeight = 14;
        const double fontSize = 8;

        (string Header, Func<InstalledApp, string> Value, double Width)[] cols =
        {
            ("Program",      a => a.Name, 150),
            ("Installed",    a => a.InstalledVersion, 78),
            ("Released",     a => a.InstalledReleaseDate?.ToString("d MMM yyyy") ?? "—", 74),
            ("Latest",       a => a.LatestVersion ?? "—", 78),
            ("Released",     a => a.LatestReleaseDate?.ToString("d MMM yyyy") ?? "—", 74),
            ("Age behind",   a => a.Age.IsKnown ? a.Age.Short : "—", 74),
            ("Missed",       a => a.ReleasesMissed > 0 ? a.ReleasesMissed.ToString() : "—", 40),
            ("Status",       a => a.StatusText, 72),
            ("Conf.",        a => a.Confidence + "%", 36),
        };

        var pages = new List<string>();
        var body = new StringBuilder();
        double y = 0;
        int rowsPerPage = 0;

        void StartPage(int pageNumber)
        {
            body.Clear();
            y = pageHeight - margin;

            body.Append("BT /F2 14 Tf 1 0 0 1 ").Append(F(margin)).Append(' ').Append(F(y))
                .Append(" Tm (").Append(Pdf("UpdateHub — installed software report")).Append(") Tj ET\n");
            y -= 16;
            body.Append("BT /F1 8 Tf 1 0 0 1 ").Append(F(margin)).Append(' ').Append(F(y))
                .Append(" Tm (").Append(Pdf($"{Environment.MachineName} · {DateTime.Now:d MMMM yyyy HH:mm} · {apps.Count} applications"))
                .Append(") Tj ET\n");
            y -= 18;

            // Header row.
            body.Append("0.85 0.85 0.85 rg ").Append(F(margin)).Append(' ').Append(F(y - 3))
                .Append(' ').Append(F(pageWidth - 2 * margin)).Append(' ').Append(F(rowHeight))
                .Append(" re f\n0 0 0 rg\n");
            double x = margin + 2;
            foreach (var (header, _, width) in cols)
            {
                body.Append("BT /F2 ").Append(F(fontSize)).Append(" Tf 1 0 0 1 ")
                    .Append(F(x)).Append(' ').Append(F(y)).Append(" Tm (")
                    .Append(Pdf(header)).Append(") Tj ET\n");
                x += width;
            }
            y -= rowHeight + 2;
            rowsPerPage = 0;
        }

        StartPage(1);

        foreach (var app in apps)
        {
            if (y < margin + rowHeight * 2)
            {
                pages.Add(body.ToString());
                StartPage(pages.Count + 1);
            }

            if (rowsPerPage % 2 == 1)
                body.Append("0.96 0.96 0.96 rg ").Append(F(margin)).Append(' ').Append(F(y - 3))
                    .Append(' ').Append(F(pageWidth - 2 * margin)).Append(' ').Append(F(rowHeight))
                    .Append(" re f\n0 0 0 rg\n");

            double x = margin + 2;
            foreach (var (_, value, width) in cols)
            {
                var text = Truncate(value(app), width, fontSize);
                body.Append("BT /F1 ").Append(F(fontSize)).Append(" Tf 1 0 0 1 ")
                    .Append(F(x)).Append(' ').Append(F(y)).Append(" Tm (")
                    .Append(Pdf(text)).Append(") Tj ET\n");
                x += width;
            }
            y -= rowHeight;
            rowsPerPage++;
        }
        pages.Add(body.ToString());

        WritePdfFile(path, pages, pageWidth, pageHeight);
    }

    /// <summary>Helvetica averages roughly 0.5 em per character; enough to fit a column.</summary>
    private static string Truncate(string text, double width, double fontSize)
    {
        int max = Math.Max(3, (int)((width - 6) / (fontSize * 0.5)));
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Escapes the three characters that terminate or nest a PDF string literal.</summary>
    private static string Pdf(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            if (ch is '(' or ')' or '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            // WinAnsi covers Latin-1; anything beyond it would need an embedded font.
            else if (ch > 255) sb.Append('?');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void WritePdfFile(string path, List<string> pageContents, double width, double height)
    {
        var objects = new List<string>();
        // 1 catalog, 2 pages, 3 font regular, 4 font bold, then per page: page + content.
        int firstPageObj = 5;
        int pageCount = pageContents.Count;

        var kids = string.Join(" ", Enumerable.Range(0, pageCount)
            .Select(i => $"{firstPageObj + i * 2} 0 R"));

        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (int i = 0; i < pageCount; i++)
        {
            int contentObj = firstPageObj + i * 2 + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(width)} {F(height)}] " +
                        $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObj} 0 R >>");
            objects.Add($"__STREAM__{i}");
        }

        var output = new MemoryStream();
        var writer = new StreamWriter(output, Encoding.ASCII) { NewLine = "\n" };
        writer.Write("%PDF-1.4\n");
        writer.Write("%âãÏÓ\n");
        writer.Flush();

        var offsets = new long[objects.Count + 1];

        for (int i = 0; i < objects.Count; i++)
        {
            writer.Flush();
            offsets[i + 1] = output.Position;

            var payload = objects[i];
            if (payload.StartsWith("__STREAM__", StringComparison.Ordinal))
            {
                var content = pageContents[int.Parse(payload["__STREAM__".Length..])];
                var bytes = Encoding.ASCII.GetByteCount(content);
                writer.Write($"{i + 1} 0 obj\n<< /Length {bytes} >>\nstream\n");
                writer.Write(content);
                writer.Write("\nendstream\nendobj\n");
            }
            else
            {
                writer.Write($"{i + 1} 0 obj\n{payload}\nendobj\n");
            }
        }

        writer.Flush();
        long xrefPos = output.Position;

        writer.Write($"xref\n0 {objects.Count + 1}\n");
        writer.Write("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            writer.Write($"{offsets[i]:D10} 00000 n \n");

        writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        writer.Flush();

        File.WriteAllBytes(path, output.ToArray());
    }
}
