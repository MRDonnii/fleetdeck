namespace Fleetdeck.Server.Services;

/// <summary>
/// CSV til dansk Excel: semikolon mellem felter, UTF-8 med BOM og CRLF. Tekst, der starter som en formel (=, +, -, @, tab, CR),
/// får et ' foran, så Excel ikke kører den. Bruges af Revisionslog og Rapporter.
/// </summary>
public static class ExcelCsv
{
    public const char Sep = ';';
    public const string Bom = "﻿";
    public const string Eol = "\r\n";

    public static string Cell(string? s)
    {
        s ??= "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny([';', '"', '\n', '\r', ',']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(Sep, cells.Select(Cell)) + Eol;
}
