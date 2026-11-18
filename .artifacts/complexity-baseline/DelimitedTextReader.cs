using System.Globalization;
using System.Text;

namespace FLEE.Core;

/// <summary>A zero-based column index or exact, case-sensitive header name.</summary>
public readonly record struct ColumnSelector
{
    public int Index { get; }
    public string? Name { get; }
    public ColumnSelector(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
    }
    public ColumnSelector(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Like the Python reader, decimal strings select indices.
        if (name.All(char.IsAsciiDigit))
            Index = int.Parse(name, NumberStyles.None, CultureInfo.InvariantCulture);
        else Name = name;
    }
    public static implicit operator ColumnSelector(int index) => new(index);
    public static implicit operator ColumnSelector(string name) => new(name);
}

/// <summary>Settings for the optional delimited-text reader.</summary>
public sealed record DelimitedTextOptions
{
    /// <summary>auto, whitespace, tab, \\t, or a single delimiter character.</summary>
    public string Delimiter { get; init; } = "auto";
    /// <summary>Number of physical lines to skip before parsing.</summary>
    public int SkipRows { get; init; }
    public Encoding Encoding { get; init; } = new UTF8Encoding(false, true);
}

/// <summary>Reads numeric columns from CSV, TSV, or whitespace text independently of DAS writing.</summary>
public static class DelimitedTextReader
{
    /// <summary>
    /// Returns one array per selected column. An empty selector span defaults to columns 0 and 1.
    /// Named columns locate a header after a preamble; indexed columns allow one textual header.
    /// Malformed data raises FormatException containing the path and physical line number.
    /// </summary>
    public static double[][] LoadColumns(string path, ReadOnlySpan<ColumnSelector> columns = default,
        DelimitedTextOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.SkipRows);
        ArgumentNullException.ThrowIfNull(options.Encoding);
        var delimiter = options.Delimiter switch { "tab" or "\\t" => "\t", _ => options.Delimiter };
        if (delimiter is null || (delimiter is not ("auto" or "whitespace") && delimiter.Length != 1))
            throw new ArgumentException("Delimiter must be auto, whitespace, tab, or one character.");
        ColumnSelector[] selectors = columns.IsEmpty ? [0, 1] : columns.ToArray();
        var names = selectors.Where(c => c.Name is not null).Select(c => c.Name!).ToArray();
        var indices = names.Length > 0 ? null : selectors.Select(c => c.Index).ToArray();
        var chosen = delimiter == "auto" ? null : delimiter;
        var result = selectors.Select(_ => new List<double>()).ToArray();
        var skippedHeader = false;
        var lineNumber = 0;
        FormatException Error(string message) => new($"{path}:{lineNumber}: {message}");
        foreach (var line in File.ReadLines(path, options.Encoding))
        {
            lineNumber++;
            if (lineNumber <= options.SkipRows || string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            string[] candidates = chosen is null ? [",", "\t", ";", "|", "whitespace"] : [chosen];
            if (indices is null)
            {
                foreach (var separator in candidates)
                {
                    string[] row;
                    try { row = Split(line, separator); }
                    catch (FormatException) { continue; }
                    if (!names.All(name => row.Contains(name, StringComparer.Ordinal))) continue;
                    if (names.Any(name => row.Count(cell => cell == name) != 1))
                        throw Error("Ambiguous duplicate column name.");
                    indices = selectors.Select(c => c.Name is null ? c.Index : Array.IndexOf(row, c.Name)).ToArray();
                    chosen = separator;
                    break;
                }
                continue;
            }
            if (chosen is null)
            {
                List<string> plausible = [];
                foreach (var separator in candidates)
                {
                    string[] row;
                    try { row = Split(line, separator); }
                    catch (FormatException) { continue; }
                    if (row.Length <= indices.Max()) continue;
                    plausible.Add(separator);
                    if (TryNumeric(row, indices, out _)) { chosen = separator; break; }
                }
                if (chosen is null && plausible.Count > 0 && !skippedHeader) chosen = plausible[0];
            }
            if (chosen is null) throw Error("Cannot detect table delimiter; set Delimiter/SkipRows explicitly.");
            string[] cells;
            try { cells = Split(line, chosen); }
            catch (FormatException ex) { throw new FormatException($"{path}:{lineNumber}: Malformed CSV row.", ex); }
            if (!TryNumeric(cells, indices, out var values))
            {
                var isHeader = names.Length == 0 && result[0].Count == 0 && !skippedHeader &&
                    indices.All(i => i < cells.Length && cells[i].Length > 0 && !TryNumber(cells[i], out _));
                if (isHeader) { skippedHeader = true; continue; }
                throw Error("Missing or nonnumeric selected column.");
            }
            if (values.Any(v => !double.IsFinite(v))) throw Error("NaN or infinity is not supported.");
            for (var i = 0; i < result.Length; i++) result[i].Add(values[i]);
        }
        if (indices is null) throw new FormatException($"{path}: Could not find header columns {string.Join(", ", names)}.");
        if (result[0].Count < 2) throw new FormatException($"{path}: At least two numeric data rows are required.");
        return result.Select(column => column.ToArray()).ToArray();
    }

    private static bool TryNumeric(string[] row, ReadOnlySpan<int> indices, out double[] values)
    {
        values = new double[indices.Length];
        for (var i = 0; i < indices.Length; i++)
            if (indices[i] >= row.Length || !TryNumber(row[indices[i]], out values[i])) return false;
        return true;
    }

    private static bool TryNumber(string cell, out double value)
    {
        switch (cell.ToLowerInvariant())
        {
            case "inf": case "+inf": value = double.PositiveInfinity; return true;
            case "-inf": value = double.NegativeInfinity; return true;
            default: return double.TryParse(cell.Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }
    }

    private static string[] Split(string line, string delimiter)
    {
        if (delimiter == "whitespace") return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        List<string> fields = [];
        var position = 0;
        while (true)
        {
            while (position < line.Length && line[position] == ' ') position++;
            var cell = new StringBuilder();
            if (position < line.Length && line[position] == '"')
            {
                position++;
                var closed = false;
                while (position < line.Length)
                {
                    var current = line[position++];
                    if (current != '"') { cell.Append(current); continue; }
                    if (position < line.Length && line[position] == '"') { cell.Append('"'); position++; }
                    else { closed = true; break; }
                }
                if (!closed || (position < line.Length && line[position] != delimiter[0]))
                    throw new FormatException("Unterminated or malformed quoted field.");
            }
            else
                while (position < line.Length && line[position] != delimiter[0]) cell.Append(line[position++]);
            fields.Add(cell.ToString().Trim());
            if (position >= line.Length) break;
            position++;
        }
        return fields.ToArray();
    }
}
