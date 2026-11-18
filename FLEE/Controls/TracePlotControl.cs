using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using FLEE.Core;

namespace FLEE.Controls;

internal sealed class TracePlotControl : Control
{
    internal static readonly Color DecayColor = Color.FromArgb(27, 92, 175);
    internal static readonly Color PromptColor = Color.FromArgb(215, 119, 40);
    private DecayDataset? dataset;
    private SampleRange selection;
    private bool logarithmic, dragging;
    private int anchorIndex;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsOverview { get; init; }
    internal event Action<SampleRange>? SelectionChanged;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Logarithmic
    {
        get => this.logarithmic;
        set { this.logarithmic = value; Invalidate(); }
    }

    internal TracePlotControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.Dock = DockStyle.Fill;
        this.BackColor = Color.White;
        this.Margin = Padding.Empty;
        this.AccessibleRole = AccessibleRole.Graphic;
    }

    internal void SetDataset(DecayDataset? data)
    {
        this.dataset = data;
        this.selection = data?.InitialRange ?? default;
        this.dragging = false;
        this.Capture = false;
        this.Cursor = Cursors.Default;
        Invalidate();
    }

    internal void SetSelection(SampleRange range)
    {
        this.selection = range;
        Invalidate();
    }

    private RectangleF PlotArea => new(74, 28, Math.Max(1, this.ClientSize.Width - 96), Math.Max(1, this.ClientSize.Height - 66));
    private SampleRange VisibleRange => this.IsOverview ? this.dataset!.FullRange : this.selection;
    private PlotViewport NavigationViewport => PlotViewport.CreateTimeAxis(this.dataset!, this.dataset!.FullRange, this.PlotArea);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (this.dataset is null) { DrawEmpty(e.Graphics); return; }
        if (this.ClientSize.Width < 120 || this.ClientSize.Height < 70) return;
        var viewport = PlotViewport.Create(this.dataset, this.VisibleRange, this.PlotArea, this.logarithmic);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        DrawAxes(e.Graphics, viewport);
        DrawTraces(e.Graphics, viewport);
        if (this.IsOverview) DrawSelection(e.Graphics, viewport);
    }

    private void DrawEmpty(Graphics graphics)
    {
        var message = this.IsOverview ? "Open a CSV to see the entire measurement" : "Your selected range will appear here";
        TextRenderer.DrawText(graphics, message, this.Font, this.ClientRectangle, Color.FromArgb(130, 143, 158),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawAxes(Graphics graphics, PlotViewport viewport)
    {
        using var gridPen = new Pen(Color.FromArgb(230, 235, 241));
        using var axisPen = new Pen(Color.FromArgb(162, 174, 188));
        using var textBrush = new SolidBrush(Color.FromArgb(93, 107, 126));
        using var rightAligned = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        using var centred = new StringFormat { Alignment = StringAlignment.Center };
        DrawSignalTicks(graphics, viewport, gridPen, axisPen, textBrush, rightAligned);
        DrawTimeTicks(graphics, viewport, gridPen, axisPen, textBrush, centred);
        graphics.DrawLine(axisPen, viewport.Area.Left, viewport.Area.Top, viewport.Area.Left, viewport.Area.Bottom);
        graphics.DrawLine(axisPen, viewport.Area.Left, viewport.Area.Bottom, viewport.Area.Right, viewport.Area.Bottom);
        graphics.DrawString("Time (ns)", this.Font, textBrush,
            new PointF(viewport.Area.Left + viewport.Area.Width / 2, viewport.Area.Bottom + 24), centred);
        graphics.DrawString(this.logarithmic ? "Counts · log(1+y)" : "Counts", this.Font, textBrush, new PointF(0, 0));
    }

    private void DrawSignalTicks(Graphics graphics, PlotViewport viewport, Pen gridPen, Pen axisPen, Brush brush, StringFormat format)
    {
        var ticks = (stackalloc double[64]);
        var labelHeight = this.Font.Height + 4;
        var count = viewport.Logarithmic
            ? AxisTicks.Logarithmic(ticks, viewport.MaximumCoordinate, viewport.MaximumCoordinate * labelHeight / viewport.Area.Height)
            : AxisTicks.Linear(ticks, 0, viewport.MaximumCoordinate, Math.Max(1, (int)(viewport.Area.Height / labelHeight)));

        var labelBuffer = (stackalloc char[32]);

        foreach (var signal in ticks[..count])
        {
            var y = viewport.Y(signal);
            graphics.DrawLine(gridPen, viewport.Area.Left, y, viewport.Area.Right, y);
            graphics.DrawLine(axisPen, viewport.Area.Left - 4, y, viewport.Area.Left, y);
            var labelString = viewport.Logarithmic && signal != 0 ? ExpFormatter(labelBuffer, signal, "1.00×") : signal.ToString("G3", CultureInfo.InvariantCulture);
            graphics.DrawString(labelString, this.Font, brush, new PointF(viewport.Area.Left - 8, y), format);
        }
    }

    private void DrawTimeTicks(Graphics graphics, PlotViewport viewport, Pen gridPen, Pen axisPen, Brush brush, StringFormat format)
    {
        var ticks = (stackalloc double[64]);
        var intervals = Math.Clamp((int)(viewport.Area.Width / 100), 2, 8);
        var count = AxisTicks.Linear(ticks, viewport.FirstTime, viewport.LastTime, intervals);

        var buffer = (stackalloc char[32]);

        foreach (var time in ticks[..count])
        {
            var x = viewport.X(time);
            graphics.DrawLine(time == 0 ? axisPen : gridPen, x, viewport.Area.Top, x, viewport.Area.Bottom);
            graphics.DrawLine(axisPen, x, viewport.Area.Bottom, x, viewport.Area.Bottom + 4);
            var point = new PointF(x, viewport.Area.Bottom + 6);
            if (time.TryFormat(buffer, out var len, "G5", AxisNumberFormat))
            {
                graphics.DrawString(buffer[..len], this.Font, brush, point, format);
            }
            else
            {
                graphics.DrawString(time.ToString("G5", AxisNumberFormat), this.Font, brush, point, format);
            }
        }
    }

    #region number format

    private static readonly NumberFormatInfo AxisNumberFormat =
    NumberFormatInfo.ReadOnly(new NumberFormatInfo
    {
        NegativeSign = "\u2212"
    });

    private static ReadOnlySpan<char> ExpFormatter(Span<char> buffer, double value, scoped ReadOnlySpan<char> unnecessaryPrefix = default)
    {
        if (!value.TryFormat(buffer, out var len, "E2", CultureInfo.InvariantCulture))
            return value.ToString("E2", CultureInfo.InvariantCulture);

        return ExpFormatterInternal(buffer, len, unnecessaryPrefix);
    }

    private static ReadOnlySpan<char> ExpFormatterInternal(Span<char> s, int len, scoped ReadOnlySpan<char> unnecessaryPrefix)
    {
        var expIndex = s.IndexOf('E');
        var exponent = (stackalloc char[len - expIndex - 1]);
        s[(expIndex + 1)..(len)].CopyTo(exponent);  // copy exponent part to separate buffer, as `s` will be modified in-place

        var index = expIndex;

        s[index++] = '×';
        s[index++] = '1';
        s[index++] = '0';

        if (exponent[0] is '-' or '\u2212')
            s[index++] = '⁻';  // U+207B

        exponent = exponent[1..].TrimStart('0');
        if (exponent.Length == 0)
        {
            s[index++] = '⁰';  // U+2070
        }
        else
        {
            // append unicode superscript
            foreach (var c in exponent)
            {
                /*
                 * Superscript of 1, 2, 3 are located in U+00Bx,
                 * whereas the rest in U+207x.
                 */
                s[index++] = c switch
                {
                    '1' => '¹',  // U+00B9
                    '2' => '²',  // U+00B2
                    '3' => '³',  // U+00B3
                    _ => (char)(c + 0x2040)  // U+2070 - U+2079
                };
            }
        }

        s = s[..index];

        if (!unnecessaryPrefix.IsEmpty && s.StartsWith(unnecessaryPrefix, StringComparison.Ordinal))
            s = s[unnecessaryPrefix.Length..];

        return s;
    }

    #endregion number format

    private void DrawTraces(Graphics graphics, PlotViewport viewport)
    {
        var data = this.dataset!;
        var range = this.VisibleRange;
        var times = data.TimeNanoseconds.Slice(range.StartIndex, range.Count);
        var state = graphics.Save();
        graphics.SetClip(viewport.Area);
        DrawSeries(graphics, times, data.Decay.Slice(range.StartIndex, range.Count), viewport, DecayColor);
        DrawSeries(graphics, times, data.Prompt.Slice(range.StartIndex, range.Count), viewport, PromptColor);
        graphics.Restore(state);
    }

    private static void DrawSeries(Graphics graphics, ReadOnlySpan<double> times, ReadOnlySpan<double> signal,
        PlotViewport viewport, Color color)
    {
        var points = PlotEnvelope.Create(times, signal, viewport);
        using var pen = new Pen(color, 1.4f);
        if (points.Length > 1) graphics.DrawLines(pen, points);
        else graphics.DrawEllipse(pen, points[0].X - 2, points[0].Y - 2, 4, 4);
    }

    private void DrawSelection(Graphics graphics, PlotViewport viewport)
    {
        var start = viewport.X(this.dataset!.TimeNanoseconds[this.selection.StartIndex]);
        var end = viewport.X(this.dataset.TimeNanoseconds[this.selection.EndIndex]);
        using var excluded = new SolidBrush(Color.FromArgb(160, 240, 242, 246));
        using var included = new SolidBrush(Color.FromArgb(24, DecayColor));
        graphics.FillRectangle(excluded, viewport.Area.Left, viewport.Area.Top, Math.Max(0, start - viewport.Area.Left), viewport.Area.Height);
        graphics.FillRectangle(excluded, end, viewport.Area.Top, Math.Max(0, viewport.Area.Right - end), viewport.Area.Height);
        graphics.FillRectangle(included, start, viewport.Area.Top, Math.Max(1, end - start), viewport.Area.Height);
        DrawHandle(graphics, viewport.Area, start);
        DrawHandle(graphics, viewport.Area, end);
    }

    private static void DrawHandle(Graphics graphics, RectangleF area, float x)
    {
        using var pen = new Pen(DecayColor, 2);
        using var brush = new SolidBrush(DecayColor);
        graphics.DrawLine(pen, x, area.Top, x, area.Bottom);
        graphics.FillEllipse(brush, x - 5, area.Top - 5, 10, 10);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!this.IsOverview || this.dataset is null || e.Button != MouseButtons.Left) return;
        var area = this.PlotArea;
        area.Inflate(7, 7);
        if (!area.Contains(e.Location)) return;
        var viewport = this.NavigationViewport;
        var index = this.dataset.FindNearestSample(viewport.TimeAt(e.X));
        this.anchorIndex = ChooseAnchor(e.X, viewport, index);
        this.dragging = true;
        this.Capture = true;
        PublishRange(index);
    }

    private int ChooseAnchor(float x, PlotViewport viewport, int index)
    {
        var startDistance = Math.Abs(x - viewport.X(this.dataset!.TimeNanoseconds[this.selection.StartIndex]));
        var endDistance = Math.Abs(x - viewport.X(this.dataset.TimeNanoseconds[this.selection.EndIndex]));
        if (startDistance <= 9 && startDistance <= endDistance) return this.selection.EndIndex;
        if (endDistance <= 9) return this.selection.StartIndex;
        return index;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!this.IsOverview || this.dataset is null) return;
        this.Cursor = this.PlotArea.Contains(e.Location) ? Cursors.Cross : Cursors.Default;
        if (!this.dragging) return;
        var viewport = this.NavigationViewport;
        PublishRange(this.dataset.FindNearestSample(viewport.TimeAt(e.X)));
    }

    private void PublishRange(int index)
    {
        var range = new SampleRange(Math.Min(this.anchorIndex, index), Math.Max(this.anchorIndex, index));
        if (range == this.selection) return;
        this.selection = range;
        SelectionChanged?.Invoke(range);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        this.dragging = false;
        this.Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!this.Capture) this.dragging = false;
    }
}
