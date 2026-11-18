using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using FLEE.Controls;
using FLEE.Core;
using Xunit;

namespace FLEE.App.Tests;

public sealed class MainFormTests
{
    [Fact]
    public void EmptyAppDisablesExport() => OnUiThread(form =>
    {
        Assert.False(Find<Button>(form, "Export selection").Enabled);
        Assert.False(Find<TextBox>(form, "Start time (ns)").Enabled);
        Assert.True(Find<CheckBox>(form, "Log signal scale").Checked);
        Assert.All(Descendants(form).OfType<TracePlotControl>(), plot => Assert.True(plot.Logarithmic));
        Capture(form, "empty.png");
    });

    [Fact]
    public void LongInputShowsFullDataAndInitiallyValidTrim() => OnUiThread(form =>
    {
        var data = LoadFilm();
        form.DisplayDataset(data);
        Assert.True(Find<Button>(form, "Export selection").Enabled);
        Assert.Equal("-5", Find<TextBox>(form, "Start time (ns)").Text);
        Assert.Contains(Texts(form), text => text.Contains("25,001 samples"));
        Assert.Contains(Texts(form), text => text.Contains("16,384 selected"));
        Assert.Contains(Texts(form), text => text.Contains("8,617 excluded"));
        Capture(form, "film-initial.png");
    });

    [Fact]
    public void PendingAndOversizedRangesDisableExportUntilApplied() => OnUiThread(form =>
    {
        var data = LoadFilm();
        form.DisplayDataset(data);
        var save = Find<Button>(form, "Export selection");
        form.UpdateSelection(data.FullRange);
        Assert.False(save.Enabled);
        Assert.Contains(Texts(form), text => text.Contains("too long by 8,617"));
        Find<TextBox>(form, "Start time (ns)").Text = "-2";
        Find<TextBox>(form, "End time (ns)").Text = "20";
        Assert.False(save.Enabled);
        form.ApplyTimeRange();
        Assert.True(save.Enabled);
        Assert.Contains(Texts(form), text => text.Contains("11,001 selected"));
        Assert.Contains(Texts(form), text => text.Contains("-2 to 20 ns"));
        Assert.True(Find<CheckBox>(form, "Log signal scale").Checked);
        Capture(form, "film-trimmed-log.png");
        Find<CheckBox>(form, "Log signal scale").Checked = false;
        Assert.All(Descendants(form).OfType<TracePlotControl>(), plot => Assert.False(plot.Logarithmic));
        Capture(form, "film-trimmed.png");
    });

    [Fact]
    public void InvalidRangeKeepsPreviewButBlocksExport() => OnUiThread(form =>
    {
        form.DisplayDataset(LoadFilm());
        Find<TextBox>(form, "Start time (ns)").Text = "NaN";
        form.ApplyTimeRange();
        Assert.False(Find<Button>(form, "Export selection").Enabled);
        Assert.Contains(Texts(form), text => text.Contains("Enter finite"));
        Assert.Contains(Texts(form), text => text.Contains("16,384 selected"));
        Find<TextBox>(form, "Start time (ns)").Text = "0";
        Find<TextBox>(form, "End time (ns)").Text = "0";
        form.ApplyTimeRange();
        Assert.False(Find<Button>(form, "Export selection").Enabled);
        Assert.Contains(Texts(form), text => text.Contains("at least 2 samples"));
    });

    [Fact]
    public void MinimumWindowSizeKeepsRangeControlsVisible() => OnUiThread(form =>
    {
        form.DisplayDataset(LoadFilm());
        form.Size = form.MinimumSize;
        Capture(form, "minimum-size.png");
        foreach (var control in Descendants(form))
        {
            Assert.True(control.Left >= 0 && control.Right <= control.Parent!.ClientSize.Width, control.Text);
            Assert.True(control.Top >= 0 && control.Bottom <= control.Parent!.ClientSize.Height, control.Text);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedLoadDiscardsPreviousDataAndAllowsRecovery(bool malformed) => OnUiThreadAsync(async form =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"FLEE-load-{Guid.NewGuid():N}.csv");
        try
        {
            if (malformed) File.WriteAllText(path, "metadata\n0,1,1\n1,invalid,2\n");
            var emptyPlots = PlotImages(form);
            form.DisplayDataset(LoadFilm());
            Find<TextBox>(form, "Start time (ns)").Text = "unapplied edit";

            var loading = form.LoadPathAsync(path);
            AssertDataCleared(form, emptyPlots);
            await loading;

            AssertDataCleared(form, emptyPlots);
            Assert.True(Find<Button>(form, "Open CSV").Enabled);
            Assert.Contains(Texts(form), text => text.StartsWith("Could not load the file:"));
            Capture(form, malformed ? "failed-load-format.png" : "failed-load-missing.png");

            File.WriteAllText(path, "metadata\n0,1,1\n1,2,2\n2,1,1\n");
            await form.LoadPathAsync(path);
            Assert.True(Find<Button>(form, "Export selection").Enabled);
            Assert.Equal("0", Find<TextBox>(form, "Start time (ns)").Text);
            Assert.Equal("2", Find<TextBox>(form, "End time (ns)").Text);
            Assert.Contains(Texts(form), text => text.Contains("3 selected"));
            Assert.DoesNotContain(Texts(form), text => text.StartsWith("Could not load the file:"));
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void SuccessfulLoadClearsOldDataWhileLoading() => OnUiThreadAsync(async form =>
    {
        var emptyPlots = PlotImages(form);
        var source = LoadFilm();
        form.DisplayDataset(source);
        form.UpdateSelection(source.SelectTimeRange(-2, 20));

        var loading = form.LoadPathAsync(source.SourcePath);
        if (!loading.IsCompleted)
        {
            AssertDataCleared(form, emptyPlots);
            Assert.False(Find<Button>(form, "Open CSV").Enabled);
        }
        await loading;
        Assert.True(Find<Button>(form, "Export selection").Enabled);
        Assert.Equal("-5", Find<TextBox>(form, "Start time (ns)").Text);
        Assert.Contains(Texts(form), text => text.Contains("16,384 selected"));
    });

    private static void AssertDataCleared(MainForm form, byte[][] emptyPlots)
    {
        Assert.False(Find<Button>(form, "Export selection").Enabled);
        Assert.False(Find<TextBox>(form, "Start time (ns)").Enabled);
        Assert.Empty(Find<TextBox>(form, "Start time (ns)").Text);
        Assert.Empty(Find<TextBox>(form, "End time (ns)").Text);
        Assert.Contains("No data loaded", Texts(form));
        Assert.DoesNotContain("Y6 film", form.Text);
        Assert.DoesNotContain(Texts(form), text => text.Contains("25,001") || text.Contains("16,384 selected"));
        var actual = PlotImages(form);
        Assert.Equal(emptyPlots.Length, actual.Length);
        for (var i = 0; i < actual.Length; i++) Assert.Equal(emptyPlots[i], actual[i]);
    }

    private static byte[][] PlotImages(MainForm form) => Descendants(form).OfType<TracePlotControl>()
        .Select(plot =>
        {
            using var bitmap = new Bitmap(plot.Width, plot.Height);
            plot.DrawToBitmap(bitmap, new Rectangle(Point.Empty, plot.Size));
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }).ToArray();

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Find<T>(Control parent, string name) where T : Control =>
        Descendants(parent).OfType<T>().Single(control => control.AccessibleName == name);
    private static string[] Texts(Control form) => Descendants(form).Select(control => control.Text).ToArray();
    private static DecayDataset LoadFilm() => DecayDataset.LoadCsv(Path.Combine(RepositoryRoot(), "python", "Y6 film-1exp.CSV"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FLEE.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository fixtures not found.");
    }

    private static void Capture(MainForm form, string name)
    {
        form.CreateControl();
        // Instantiate child handles for offscreen WM_PRINT rendering without showing a window.
        foreach (var control in Descendants(form)) _ = control.Handle;
        form.PerformLayout();
        var folder = Path.Combine(RepositoryRoot(), ".artifacts", "ui-preview");
        Directory.CreateDirectory(folder);
        using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        RenderChildren(form, image, Point.Empty);
        image.Save(Path.Combine(folder, name), ImageFormat.Png);
    }

    private static void RenderChildren(Control parent, Bitmap image, Point offset)
    {
        foreach (Control child in parent.Controls)
        {
            var location = new Point(offset.X + child.Left, offset.Y + child.Top);
            child.DrawToBitmap(image, new Rectangle(location, child.Size));
            RenderChildren(child, image, location);
        }
    }

    private static void OnUiThread(Action<MainForm> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                test(form);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void OnUiThreadAsync(Func<MainForm, Task> test) => OnUiThread(form =>
    {
        // Pump async UI continuations without showing a desktop window.
        using var loop = new ApplicationContext();
        using var context = new WindowsFormsSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Exception? failure = null;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            context.Post(async _ =>
            {
                try { await test(form); }
                catch (Exception ex) { failure = ex; }
                finally { loop.ExitThread(); }
            }, null);
            Application.Run(loop);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });
}
