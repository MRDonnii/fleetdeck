using System.Globalization;
using System.Text;

namespace Fleetdeck.Server.Services.Reports;

/// <summary>Rapporten som CSV (dansk Excel), PDF (lille indbygget generator, ingen afhængigheder) og JSON-sider.</summary>
public static class ReportExport
{
    public const int MaxPageSize = 5000;

    private static bool IsNumber(object? v) => v is int or long or double or float or decimal;

    /// <summary>
    /// CSV: UTF-8 med BOM, semikolon og CRLF. Med flere tabeller står hver tabels titel på en linje for sig og en tom linje imellem.
    /// Tekst går gennem <see cref="ExcelCsv.Cell"/> (formel-beskyttelse); tal skrives med decimalkomma uden enhed, så Excel regner med dem.
    /// </summary>
    public static async Task WriteCsv(TextWriter w, ReportResult r, string? section = null)
    {
        await w.WriteAsync(ExcelCsv.Bom);
        var sections = Pick(r, section);
        var titled = sections.Count > 1;
        var n = 0;
        foreach (var s in sections)
        {
            if (titled)
            {
                if (n++ > 0) await w.WriteAsync(ExcelCsv.Eol);
                await w.WriteAsync(ExcelCsv.Line([s.Title]));
            }
            await w.WriteAsync(ExcelCsv.Line(s.Columns.Select(c => c.Label)));
            var i = 0;
            foreach (var row in s.Rows)
            {
                var sb = new StringBuilder();
                for (var c = 0; c < s.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(ExcelCsv.Sep);
                    var v = c < row.Length ? row[c] : null;
                    sb.Append(IsNumber(v) && s.Columns[c].Kind != "min" ? ReportFormat.CsvNumber(v!) : ExcelCsv.Cell(ReportFormat.Text(v, s.Columns[c].Kind)));
                }
                sb.Append(ExcelCsv.Eol);
                await w.WriteAsync(sb.ToString());
                if (++i % 500 == 0) await w.FlushAsync();
            }
        }
        await w.FlushAsync();
    }

    public static async Task<string> Csv(ReportResult r, string? section = null)
    {
        using var w = new StringWriter(CultureInfo.InvariantCulture);
        await WriteCsv(w, r, section);
        return w.ToString();
    }

    public static List<ReportSection> Pick(ReportResult r, string? section) =>
        section is { Length: > 0 } ? r.Sections.Where(s => s.Key.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList() : r.Sections;

    /// <summary>JSON: hver tabel skæres til den ønskede side; total fortæller hvor mange rækker, der er i alt.</summary>
    public static object Page(ReportResult r, string? section, int page, int pageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var pg = Math.Max(page, 1);
        return new
        {
            name = r.Name, title = r.Title, description = r.Description, filter = r.Filter, generated = r.Generated, stats = r.Stats, summary = r.Summary,
            page = pg, pageSize = size,
            sections = Pick(r, section).Select(s => new
            {
                key = s.Key, title = s.Title, note = s.Note, columns = s.Columns, total = s.Rows.Count,
                rows = s.Rows.Skip((pg - 1) * size).Take(size),
            }),
        };
    }

    public static byte[] Pdf(ReportResult r, string? section = null)
    {
        var doc = new MiniPdf();
        doc.Title(r.Title, $"{r.Filter} · dannet " + ReportFormat.Local(r.Generated).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        if (r.Stats.Count > 0) doc.Line(string.Join("   ·   ", r.Stats.Select(s => $"{s.Label}: {s.Value}")), 9, bold: false);
        foreach (var s in Pick(r, section))
        {
            doc.Heading(s.Title);
            if (s.Note is { } note) doc.Line(note, 7.5, bold: false, gray: true);
            doc.Table(s.Columns.Select(c => c.Label).ToList(),
                s.Rows.Select(row => s.Columns.Select((c, i) => ReportFormat.Text(i < row.Length ? row[i] : null, c.Kind)).ToList()).ToList(),
                s.Columns.Select(c => c.Kind is "num" or "pct" or "min").ToList());
        }
        return doc.Build($"Fleetdeck · {r.Title}");
    }
}

/// <summary>
/// En meget lille PDF-generator: A4 på tværs, Helvetica (indbygget i alle PDF-læsere, så ingen skrifttyper indlejres),
/// WinAnsi-tegnsæt (dækker æ, ø, å og de fleste vesteuropæiske tegn). Kun tekst og streger – nok til tabelrapporter.
/// </summary>
public sealed class MiniPdf
{
    private const double W = 842, H = 595, Margin = 36;
    private readonly List<StringBuilder> pages = [];
    private StringBuilder cur = null!;
    private double y;

    public MiniPdf() => NewPage();

    private void NewPage() { cur = new StringBuilder(); pages.Add(cur); y = H - Margin; }

    private void Ensure(double h) { if (y - h < Margin + 14) NewPage(); }

    // Helvetica-bredder (1/1000 em) for ASCII 32–126; andre tegn regnes som 556.
    private static readonly short[] Widths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    public static double Measure(string s, double size, bool bold = false) =>
        s.Sum(c => c is >= ' ' and <= '~' ? Widths[c - 32] : 556) * size / 1000 * (bold ? 1.06 : 1);

    public static string Fit(string s, double width, double size, bool bold = false)
    {
        if (Measure(s, size, bold) <= width) return s;
        while (s.Length > 0 && Measure(s + "…", size, bold) > width) s = s[..^1];
        return s + "…";
    }

    /// <summary>Tegn til WinAnsi-bytes; parenteser og backslash escapes. Tegn uden for tegnsættet bliver til "?".</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            var c = ch switch
            {
                '–' => '\u0096', '—' => '\u0097', '…' => '\u0085', '€' => '\u0080', '‘' => '\u0091', '’' => '\u0092', '“' => '\u0093', '”' => '\u0094', '•' => '\u0095',
                '→' => '>',
                _ when ch is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' => ch,
                _ when ch is '\t' or '\n' or '\r' => ' ',
                _ => '?',
            };
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void Text(double x, double yy, string s, double size, bool bold, bool gray = false)
    {
        var f = bold ? "F2" : "F1";
        cur.Append(gray ? "0.4 g " : "0 g ").Append(CultureInfo.InvariantCulture, $"BT /{f} {size:0.##} Tf {x:0.##} {yy:0.##} Td (").Append(Escape(s)).Append(") Tj ET\n");
    }

    public void Title(string title, string sub)
    {
        Text(Margin, y - 18, title, 18, true); y -= 26;
        Text(Margin, y - 10, sub, 9, false, gray: true); y -= 18;
    }

    public void Line(string s, double size, bool bold, bool gray = false)
    {
        Ensure(size + 6);
        Text(Margin, y - size, Fit(s, W - 2 * Margin, size, bold), size, bold, gray); y -= size + 6;
    }

    public void Heading(string s)
    {
        Ensure(40);
        y -= 8;
        Text(Margin, y - 11, s, 11, true); y -= 17;
    }

    public void Table(List<string> header, List<List<string>> rows, List<bool> right)
    {
        const double size = 7.5, rowH = 11, pad = 3;
        var avail = W - 2 * Margin;
        // bredder: hver kolonnes behov (overskrift og de første 200 rækker), skaleret ned så de passer
        var data = header.Select((_, i) => rows.Take(200).Select(r => Measure(r[i], size)).DefaultIfEmpty(0).Max()).ToList();
        var need = header.Select((h, i) => Math.Max(Measure(h, size, true), data[i]) + 2 * pad).ToList();
        // for bredt: lange overskrifter må forkortes før dataene
        if (need.Sum() > avail) need = header.Select((h, i) => Math.Max(data[i], Math.Min(Measure(h, size, true), 60)) + 2 * pad).ToList();
        var total = need.Sum();
        var widths = total <= avail ? need : need.Select(n => Math.Max(24, n * avail / total)).ToList();
        var scale = widths.Sum() > avail ? avail / widths.Sum() : 1;
        widths = widths.Select(w => w * scale).ToList();

        void Row(List<string> cells, bool bold)
        {
            Ensure(rowH);
            var x = Margin;
            for (var i = 0; i < cells.Count; i++)
            {
                var t = Fit(cells[i], widths[i] - 2 * pad, size, bold);
                var tx = right[i] && !bold ? x + widths[i] - pad - Measure(t, size) : x + pad;
                Text(tx, y - rowH + 3, t, size, bold);
                x += widths[i];
            }
            cur.Append(CultureInfo.InvariantCulture, $"{(bold ? "0.3" : "0.85")} G 0.5 w {Margin:0.##} {y - rowH:0.##} m {Margin + widths.Sum():0.##} {y - rowH:0.##} l S\n");
            y -= rowH;
        }
        Row(header, true);
        if (rows.Count == 0) { Line("Ingen rækker", size, false, gray: true); return; }
        foreach (var r in rows)
        {
            if (y - rowH < Margin + 14) { NewPage(); Row(header, true); }
            Row(r, false);
        }
    }

    public byte[] Build(string footer)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // sider – udfyldes nedenfor
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        var kids = new List<int>();
        for (var i = 0; i < pages.Count; i++)
        {
            var content = pages[i].ToString();
            var foot = $"0.4 g BT /F1 7 Tf {Margin} 20 Td ({Escape(footer)}) Tj ET\n" +
                       string.Create(CultureInfo.InvariantCulture, $"0.4 g BT /F1 7 Tf {W - Margin - Measure($"Side {i + 1} af {pages.Count}", 7):0.##} 20 Td (Side {i + 1} af {pages.Count}) Tj ET\n");
            var stream = content + foot;
            objs.Add($"<< /Length {Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream");
            var contentId = objs.Count;
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>");
            kids.Add(objs.Count);
        }
        objs[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        using var ms = new MemoryStream();
        void Put(string s) { var b = Latin1.GetBytes(s); ms.Write(b); }
        Put("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(ms.Position); Put($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Put(sb.ToString());
        return ms.ToArray();
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;
}
