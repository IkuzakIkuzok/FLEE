using FLEE.Controls;

namespace FLEE;

public sealed partial class MainForm
{
    private const string ApplicationTitle = "FLEE — Fluorescence Lifetime Exchange Engine";
    private static readonly Color Ink = Color.FromArgb(30, 47, 70);
    private static readonly Color Muted = Color.FromArgb(87, 103, 123);
    private static readonly Color Accent = Color.FromArgb(31, 91, 170);
    private static readonly Color ErrorColor = Color.FromArgb(171, 66, 28);
    private readonly Icon applicationIcon = new(typeof(MainForm), "Assets.Flee.ico");
    private readonly Bitmap brandImage;
    private readonly Button openButton = MakeButton("Open CSV…", "Open CSV", true);
    private readonly Button saveButton = MakeButton("Export selection…", "Export selection", true);
    private readonly Button applyButton = MakeButton("Apply range", "Apply range");
    private readonly Button fullButton = MakeButton("Full range", "Full range");
    private readonly Button limitButton = MakeButton("Limit from start", "Limit from start");
    private readonly TextBox startTime = new() { Dock = DockStyle.Fill, AccessibleName = "Start time (ns)" };
    private readonly TextBox endTime = new() { Dock = DockStyle.Fill, AccessibleName = "End time (ns)" };
    private readonly CheckBox logScale = new() { Text = "Log signal scale", Checked = true, AutoSize = true, AccessibleName = "Log signal scale" };
    private readonly Label fileName = MakeLabel(string.Empty, 12, FontStyle.Bold);
    private readonly Label filePath = MakeLabel(string.Empty, 9);
    private readonly Label datasetSummary = MakeLabel(string.Empty, 10);
    private readonly Label overviewCaption = MakeLabel(string.Empty, 9);
    private readonly Label previewCaption = MakeLabel(string.Empty, 9);
    private readonly Label selectionSummary = MakeLabel(string.Empty, 11, FontStyle.Bold);
    private readonly Label status = MakeLabel(string.Empty, 10);
    private readonly TableLayoutPanel rangePanel = new() { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2 };
    private readonly TracePlotControl overview = new() { IsOverview = true, AccessibleName = "Entire dataset and trimming selection" };
    private readonly TracePlotControl preview = new() { AccessibleName = "Selected data preview" };

    private void InitializeLayout()
    {
        this.Text = ApplicationTitle;
        this.Icon = this.applicationIcon;
        this.ClientSize = new Size(1160, 820);
        this.MinimumSize = new Size(980, 720);
        this.StartPosition = FormStartPosition.CenterScreen;
        this.AutoScaleMode = AutoScaleMode.Dpi;
        this.Font = new Font("Segoe UI", 10);
        this.ForeColor = Ink;
        this.BackColor = Color.FromArgb(243, 246, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 14), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 48, 66, 30 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.RowStyles.Add(new(SizeType.Absolute, 64));
        layout.RowStyles.Add(new(SizeType.Absolute, 26));
        layout.Controls.Add(BuildHeader(), 0, 0);
        layout.Controls.Add(BuildFileBar(), 0, 1);
        layout.Controls.Add(this.datasetSummary, 0, 2);
        layout.Controls.Add(BuildPlotCard("01  Entire dataset", this.overviewCaption, this.overview), 0, 3);
        layout.Controls.Add(BuildRangePanel(), 0, 4);
        layout.Controls.Add(BuildPlotCard("02  Selected data preview", this.previewCaption, this.preview), 0, 5);
        layout.Controls.Add(BuildExportBar(), 0, 6);
        layout.Controls.Add(MakeLabel("Export keeps the original sample spacing. Only the selected samples are written; no rebinning or interpolation.", 9), 0, 7);
        this.Controls.Add(layout);
    }

    private TableLayoutPanel BuildHeader()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new(SizeType.Percent, 50));
        panel.Controls.Add(BuildBrand());
        var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        var prompt = MakeLabel("●  Prompt", 10); prompt.ForeColor = TracePlotControl.PromptColor; prompt.AutoSize = true;
        var decay = MakeLabel("●  Decay", 10); decay.ForeColor = TracePlotControl.DecayColor; decay.AutoSize = true;
        legend.Controls.Add(prompt);
        legend.Controls.Add(decay);
        panel.Controls.Add(legend);
        return panel;
    }

    private TableLayoutPanel BuildBrand()
    {
        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        brand.ColumnStyles.Add(new(SizeType.Absolute, 52));
        brand.ColumnStyles.Add(new(SizeType.Percent, 100));
        brand.RowStyles.Add(new(SizeType.Percent, 100));
        brand.Controls.Add(new PictureBox
        {
            Dock = DockStyle.Fill, Image = this.brandImage, SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 8, 0), AccessibleName = "FLEE application icon", TabStop = false
        });
        var name = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        name.RowStyles.Add(new(SizeType.Absolute, 30));
        name.RowStyles.Add(new(SizeType.Percent, 100));
        name.Controls.Add(MakeLabel("FLEE", 21, FontStyle.Bold));
        name.Controls.Add(MakeLabel("Fluorescence Lifetime Exchange Engine", 9));
        brand.Controls.Add(name);
        return brand;
    }

    private TableLayoutPanel BuildFileBar()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 6, 0, 6) };
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Absolute, 142));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(this.openButton);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = new Padding(12, 0, 0, 0) };
        details.RowStyles.Add(new(SizeType.Percent, 52)); details.RowStyles.Add(new(SizeType.Percent, 48));
        details.Controls.Add(this.fileName); details.Controls.Add(this.filePath);
        panel.Controls.Add(details);
        return panel;
    }

    private static TableLayoutPanel BuildPlotCard(string title, Label caption, TracePlotControl plot)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12, 6, 10, 2),
            ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 2, 0, 2)
        };
        panel.RowStyles.Add(new(SizeType.Absolute, 26));
        panel.RowStyles.Add(new(SizeType.Absolute, 22));
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(MakeLabel(title, 11, FontStyle.Bold));
        panel.Controls.Add(caption);
        panel.Controls.Add(plot);
        return panel;
    }

    private TableLayoutPanel BuildRangePanel()
    {
        this.rangePanel.Margin = new Padding(0, 5, 0, 5);
        foreach (var width in new[] { 166, 166, 126, 152, 120 }) this.rangePanel.ColumnStyles.Add(new(SizeType.Absolute, width));
        this.rangePanel.ColumnStyles.Add(new(SizeType.Percent, 100));
        this.rangePanel.RowStyles.Add(new(SizeType.Absolute, 24));
        this.rangePanel.RowStyles.Add(new(SizeType.Percent, 100));
        this.rangePanel.Controls.Add(MakeLabel("Start time (ns)", 9), 0, 0);
        this.rangePanel.Controls.Add(MakeLabel("End time (ns)", 9), 1, 0);
        this.rangePanel.Controls.Add(this.startTime, 0, 1);
        this.rangePanel.Controls.Add(this.endTime, 1, 1);
        this.rangePanel.Controls.Add(this.applyButton, 2, 1);
        this.rangePanel.Controls.Add(this.limitButton, 3, 1);
        this.rangePanel.Controls.Add(this.fullButton, 4, 1);
        this.logScale.Anchor = AnchorStyles.Right;
        this.rangePanel.Controls.Add(this.logScale, 5, 1);
        return this.rangePanel;
    }

    private TableLayoutPanel BuildExportBar()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
        panel.RowStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Absolute, 190));
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty };
        details.RowStyles.Add(new(SizeType.Absolute, 25)); details.RowStyles.Add(new(SizeType.Percent, 100));
        details.Controls.Add(this.selectionSummary); details.Controls.Add(this.status);
        panel.Controls.Add(details); panel.Controls.Add(this.saveButton);
        return panel;
    }

    private static Label MakeLabel(string text, float size, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
        ForeColor = style == FontStyle.Bold ? Ink : Muted, Font = new Font("Segoe UI", size, style), Margin = new Padding(0)
    };

    private static Button MakeButton(string text, string accessibleName, bool primary = false) => new()
    {
        Text = text, AccessibleName = accessibleName, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
        Cursor = Cursors.Hand, Margin = new Padding(3, 0, 7, 4), UseVisualStyleBackColor = false,
        Font = new Font("Segoe UI", 10, FontStyle.Bold)
    };
}
