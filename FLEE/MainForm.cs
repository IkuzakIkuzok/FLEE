using System.Globalization;
using FLEE.Core;

namespace FLEE;

public sealed partial class MainForm : Form
{
    private DecayDataset? dataset;
    private SampleRange selection;
    private bool busy, updatingRange, pendingRange;

    public MainForm()
    {
        using var headerIcon = new Icon(this.applicationIcon, 256, 256);
        this.brandImage = headerIcon.ToBitmap();
        InitializeLayout();
        this.openButton.Click += OpenClicked;
        this.saveButton.Click += SaveClicked;
        this.applyButton.Click += (_, _) => ApplyTimeRange();
        this.fullButton.Click += (_, _) => SelectFullRange();
        this.limitButton.Click += (_, _) => LimitSelection();
        this.startTime.TextChanged += RangeTextChanged;
        this.endTime.TextChanged += RangeTextChanged;
        this.startTime.KeyDown += RangeKeyDown;
        this.endTime.KeyDown += RangeKeyDown;
        this.overview.SelectionChanged += UpdateSelection;
        this.logScale.CheckedChanged += (_, _) => UpdatePlotScale();
        UpdatePlotScale();
        ClearDataset();
        SetStatus("Open an instrument CSV to begin.", false);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        this.brandImage.Dispose();
        this.applicationIcon.Dispose();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var button = keyData switch
        {
            (Keys.Control | Keys.O) => this.openButton,
            (Keys.Control | Keys.S) => this.saveButton,
            _ => null
        };
        if (button is null) return base.ProcessCmdKey(ref msg, keyData);
        if (button.Enabled) button.PerformClick();
        return true;
    }

    private async void OpenClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open instrument CSV", Filter = "Instrument CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) await LoadPathAsync(dialog.FileName);
    }

    internal async Task LoadPathAsync(string path)
    {
        ClearDataset();
        SetBusy(true, "Loading and validating the CSV…");
        try
        {
            var loaded = await Task.Run(() => DecayDataset.LoadCsv(path));
            if (this.IsDisposed) return;
            DisplayDataset(loaded);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (!this.IsDisposed) SetStatus($"Could not load the file: {ex.Message}", true);
        }
        finally
        {
            if (!this.IsDisposed) SetBusy(false);
        }
    }

    private async void SaveClicked(object? sender, EventArgs e)
    {
        if (!CanExport() || this.dataset is null) return;
        var loaded = this.dataset;
        var range = this.selection;
        using var dialog = new SaveFileDialog
        {
            Title = "Export selected samples", Filter = "DAS dataset (*.das)|*.das", DefaultExt = "das",
            AddExtension = true, OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(loaded.SourcePath),
            FileName = Path.GetFileNameWithoutExtension(loaded.SourcePath) + ".das"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true, "Writing the selected samples…");
        try
        {
            var info = await Task.Run(() => loaded.Export(dialog.FileName, range, overwrite: true));
            if (!this.IsDisposed) SetStatus($"Saved {info.Channels:N0} samples to {info.Path}", false);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            if (!this.IsDisposed) SetStatus($"Could not export: {ex.Message}", true);
        }
        finally
        {
            if (!this.IsDisposed) SetBusy(false);
        }
    }

    private static bool IsDataError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException;

    private void ClearDataset()
    {
        this.dataset = null;
        this.selection = default;
        this.pendingRange = false;
        this.overview.SetDataset(null);
        this.preview.SetDataset(null);
        this.startTime.Clear();
        this.endTime.Clear();
        this.Text = ApplicationTitle;
        this.fileName.Text = "No data loaded";
        this.filePath.Text = "CSV: one metadata row, then time (ns), decay and prompt.";
        this.datasetSummary.Text = "Open a CSV to inspect and trim your measurement.";
        this.overviewCaption.Text = "The entire source remains visible here.";
        this.previewCaption.Text = "The exact samples selected for export appear here.";
        this.selectionSummary.Text = $"Select 2–{DasWriter.MaxChannels:N0} samples for DAS export.";
        this.selectionSummary.ForeColor = Ink;
        UpdateAvailability();
    }

    internal void DisplayDataset(DecayDataset loaded)
    {
        this.dataset = loaded;
        this.fileName.Text = Path.GetFileName(loaded.SourcePath);
        this.filePath.Text = loaded.SourcePath;
        this.Text = $"FLEE — {Path.GetFileName(loaded.SourcePath)}";
        this.overview.SetDataset(loaded);
        this.preview.SetDataset(loaded);
        UpdateSelection(loaded.InitialRange);
    }

    internal void UpdateSelection(SampleRange range)
    {
        if (this.dataset is null) return;
        this.dataset.ValidateRange(range);
        this.selection = range;
        this.pendingRange = false;
        UpdateTimeFields();
        this.overview.SetSelection(range);
        this.preview.SetSelection(range);
        UpdateSummaries();
        UpdateSelectionStatus();
        UpdateAvailability();
    }

    private void UpdateTimeFields()
    {
        if (this.dataset is null) return;
        this.updatingRange = true;
        this.startTime.Text = this.dataset.TimeNanoseconds[this.selection.StartIndex].ToString("R", CultureInfo.InvariantCulture);
        this.endTime.Text = this.dataset.TimeNanoseconds[this.selection.EndIndex].ToString("R", CultureInfo.InvariantCulture);
        this.updatingRange = false;
    }

    private void UpdateSummaries()
    {
        if (this.dataset is null) return;
        var times = this.dataset.TimeNanoseconds;
        this.datasetSummary.Text = $"{this.dataset.Count:N0} samples  ·  {FormatTime(times[0])} to {FormatTime(times[^1])} ns  ·  Decay + measured prompt";
        this.overviewCaption.Text = $"Full source: {this.dataset.Count:N0} samples. Drag a new range or move either blue boundary.";
        this.previewCaption.Text = $"Samples {this.selection.StartIndex + 1:N0}–{this.selection.EndIndex + 1:N0}  ·  " +
            $"{FormatTime(times[this.selection.StartIndex])} to {FormatTime(times[this.selection.EndIndex])} ns";
        this.selectionSummary.Text = $"{this.selection.Count:N0} selected / {DasWriter.MaxChannels:N0} maximum    ·    {this.dataset.Count - this.selection.Count:N0} excluded";
        this.selectionSummary.ForeColor = this.selection.FitsDasLimit ? Ink : ErrorColor;
    }

    private void UpdateSelectionStatus()
    {
        if (this.dataset is null) return;
        if (this.selection.Count < 2)
        {
            SetStatus("Select at least 2 samples to export. Widen the highlighted range.", true);
            return;
        }
        if (!this.selection.FitsDasLimit)
        {
            SetStatus($"Selection is too long by {this.selection.Count - DasWriter.MaxChannels:N0} samples. Narrow the range or choose ‘Limit from start’.", true);
            return;
        }
        var message = this.dataset.Count > DasWriter.MaxChannels
            ? "This dataset requires trimming. Review the highlighted range and the preview before exporting."
            : "Ready to export the previewed samples. Adjust the range if you want a shorter interval.";
        SetStatus(message, false);
    }

    internal void ApplyTimeRange()
    {
        if (this.dataset is null || this.busy) return;
        if (!TryTime(this.startTime.Text, out var start) || !TryTime(this.endTime.Text, out var end))
        {
            SetStatus("Enter finite start and end times in nanoseconds, using a decimal point.", true);
            return;
        }
        try { UpdateSelection(this.dataset.SelectTimeRange(start, end)); }
        catch (ArgumentException ex) { SetStatus(ex.Message, true); }
    }

    private static bool TryTime(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private void RangeTextChanged(object? sender, EventArgs e)
    {
        if (this.updatingRange || this.dataset is null) return;
        this.pendingRange = true;
        SetStatus("Range edits are not applied yet. Choose Apply range or press Enter to update the preview.", true);
        UpdateAvailability();
    }

    private void RangeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { ApplyTimeRange(); e.SuppressKeyPress = true; }
        if (e.KeyCode == Keys.Escape) { UpdateSelection(this.selection); e.SuppressKeyPress = true; }
    }

    private void SelectFullRange()
    {
        if (this.dataset is not null) UpdateSelection(this.dataset.FullRange);
    }

    private void LimitSelection()
    {
        if (this.dataset is not null) UpdateSelection(this.dataset.LimitFrom(this.selection.StartIndex));
    }

    private void UpdatePlotScale()
    {
        this.overview.Logarithmic = this.logScale.Checked;
        this.preview.Logarithmic = this.logScale.Checked;
    }

    private bool CanExport() => this.dataset is not null && this.selection.FitsDasLimit && !this.pendingRange && !this.busy;

    private void UpdateAvailability()
    {
        this.openButton.Enabled = !this.busy;
        this.saveButton.Enabled = CanExport();
        var editable = this.dataset is not null && !this.busy;
        this.rangePanel.Enabled = editable;
        this.overview.Enabled = editable;
        this.preview.Enabled = !this.busy;
    }

    private void SetBusy(bool value, string? message = null)
    {
        this.busy = value;
        this.UseWaitCursor = value;
        if (message is not null) SetStatus(message, false);
        UpdateAvailability();
    }

    private void SetStatus(string text, bool error)
    {
        this.status.Text = text;
        this.status.ForeColor = error ? ErrorColor : Muted;
    }

    private static string FormatTime(double value) => value.ToString("G8", CultureInfo.InvariantCulture);
}
