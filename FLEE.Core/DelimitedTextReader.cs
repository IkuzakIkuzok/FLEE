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
        this.Index = index;
    }
    public ColumnSelector(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // Like the Python reader, decimal strings select indices.
        if (name.All(char.IsAsciiDigit))
            this.Index = int.Parse(name, NumberStyles.None, CultureInfo.InvariantCulture);
        else this.Name = name;
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
        var delimiter = NormalizeDelimiter(options.Delimiter);
        var parser = new TableParser(path, columns, delimiter);
        foreach (var line in File.ReadLines(path, options.Encoding))
            parser.ReadLine(line, options.SkipRows);
        return parser.GetColumns();
    }

    private static string NormalizeDelimiter(string value)
    {
        var delimiter = value switch { "tab" or "\\t" => "\t", _ => value };
        if (delimiter is null || (delimiter is not ("auto" or "whitespace") && delimiter.Length != 1))
            throw new ArgumentException("Delimiter must be auto, whitespace, tab, or one character.");
        return delimiter;
    }

    // Parsing state belongs to one table; header discovery and numeric-row handling stay separate.
    private sealed class TableParser
    {
        private static readonly string[] AutoDelimiters = [",", "\t", ";", "|", "whitespace"];
        private readonly string path;
        private readonly ColumnSelector[] selectors;
        private readonly string[] names;
        private readonly List<double>[] result;
        private int[]? indices;
        private string? chosen;
        private bool skippedHeader;
        private int lineNumber;

        internal TableParser(string path, ReadOnlySpan<ColumnSelector> columns, string delimiter)
        {
            this.path = path;
            this.selectors = columns.IsEmpty ? [0, 1] : columns.ToArray();
            this.names = [.. this.selectors.Where(c => c.Name is not null).Select(c => c.Name!)];
            this.indices = this.names.Length > 0 ? null : [.. this.selectors.Select(c => c.Index)];
            this.chosen = delimiter == "auto" ? null : delimiter;
            this.result = [.. this.selectors.Select(_ => new List<double>())];
        }

        internal void ReadLine(string line, int skipRows)
        {
            this.lineNumber++;
            if (this.lineNumber <= skipRows || string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) return;
            if (this.indices is null)
            {
                FindNamedHeader(line);
                return;
            }
            this.chosen ??= DetectDelimiter(line, this.indices);
            if (this.chosen is null) throw Error("Cannot detect table delimiter; set Delimiter/SkipRows explicitly.");
            ReadDataLine(line, this.chosen, this.indices);
        }

        private void FindNamedHeader(string line)
        {
            var candidates = this.chosen is null ? AutoDelimiters : [this.chosen];
            foreach (var separator in candidates)
            {
                var row = TrySplit(line, separator);
                if (row is null || !this.names.All(name => row.Contains(name, StringComparer.Ordinal))) continue;
                if (this.names.Any(name => row.Count(cell => cell == name) != 1))
                    throw Error("Ambiguous duplicate column name.");
                this.indices = [.. this.selectors.Select(c => c.Name is null ? c.Index : Array.IndexOf(row, c.Name))];
                this.chosen = separator;
                return;
            }
        }

        private string? DetectDelimiter(string line, int[] selectedIndices)
        {
            string? firstPlausible = null;
            foreach (var separator in AutoDelimiters)
            {
                var row = TrySplit(line, separator);
                if (row is null || row.Length <= selectedIndices.Max()) continue;
                firstPlausible ??= separator;
                if (TryNumeric(row, selectedIndices, out _)) return separator;
            }
            return this.skippedHeader ? null : firstPlausible;
        }

        private void ReadDataLine(string line, string delimiter, int[] selectedIndices)
        {
            string[] cells;
            try { cells = Split(line, delimiter); }
            catch (FormatException ex) { throw new FormatException($"{this.path}:{this.lineNumber}: Malformed CSV row.", ex); }
            if (!TryNumeric(cells, selectedIndices, out var values))
            {
                if (IsOptionalHeader(cells, selectedIndices))
                {
                    this.skippedHeader = true;
                    return;
                }
                throw Error("Missing or nonnumeric selected column.");
            }
            if (values.Any(v => !double.IsFinite(v))) throw Error("NaN or infinity is not supported.");
            for (var i = 0; i < this.result.Length; i++) this.result[i].Add(values[i]);
        }

        private bool IsOptionalHeader(string[] cells, int[] selectedIndices) =>
            this.names.Length == 0 && this.result[0].Count == 0 && !this.skippedHeader &&
            selectedIndices.All(i => i < cells.Length && cells[i].Length > 0 && !TryNumber(cells[i], out _));

        internal double[][] GetColumns()
        {
            if (this.indices is null) throw new FormatException($"{this.path}: Could not find header columns {string.Join(", ", this.names)}.");
            if (this.result[0].Count < 2) throw new FormatException($"{this.path}: At least two numeric data rows are required.");
            return [.. this.result.Select(column => column.ToArray())];
        }

        private FormatException Error(string message) => new($"{this.path}:{this.lineNumber}: {message}");
    }

    private static string[]? TrySplit(string line, string delimiter)
    {
        try { return Split(line, delimiter); }
        catch (FormatException) { return null; }
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
            fields.Add(ReadField(line, delimiter[0], ref position).Trim());
            if (position >= line.Length) break;
            position++;
        }
        return [.. fields];
    }

    private static string ReadField(ReadOnlySpan<char> line, char delimiter, ref int position)
    {
        while (position < line.Length && line[position] == ' ') position++;
        if (position < line.Length && line[position] == '"')
            return ReadQuotedField(line, delimiter, ref position);
        var start = position;
        while (position < line.Length && line[position] != delimiter) position++;
        return line[start..position].ToString();
    }

    private static string ReadQuotedField(ReadOnlySpan<char> line, char delimiter, ref int position)
    {
        position++; // Opening quote.
        var cell = new StringBuilder();
        while (position < line.Length)
        {
            var current = line[position++];
            if (current != '"')
            {
                cell.Append(current);
                continue;
            }
            if (position < line.Length && line[position] == '"')
            {
                cell.Append('"');
                position++;
                continue;
            }
            if (position < line.Length && line[position] != delimiter)
                throw new FormatException("Unterminated or malformed quoted field.");
            return cell.ToString();
        }
        throw new FormatException("Unterminated or malformed quoted field.");
    }
}
