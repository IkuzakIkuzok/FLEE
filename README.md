# FLEE

**Fluorescence Lifetime Exchange Engine**

FLEE is a Windows app for inspecting fluorescence decay measurements, choosing
the time range to use, and exporting the selected data as a DAS file.

## Getting started

Start **FLEE.exe** on Windows. The published app runs without a separate .NET
installation. If your copy shows a missing-runtime prompt, install the
**.NET 10 Desktop Runtime** and start the app again.

Prepare an instrument CSV with one metadata row, followed by these columns:

| Column | Contents |
| --- | --- |
| 1 | Time in nanoseconds (ns) |
| 2 | Decay counts |
| 3 | Measured prompt counts |

Time values must be finite, increasing, and evenly spaced. Both count columns
must contain finite, nonnegative numbers.

## Inspect, trim, export

1. Choose **Open CSV…** or press **Ctrl+O**, then select your measurement file.
2. Inspect **Entire dataset**. Both traces remain visible, with the selected range
   highlighted and excluded samples faded. Long inputs initially select the first
   16,384 samples; shorter inputs initially select everything.
3. Drag across the overview to select an interval, or drag either blue boundary.
   Alternatively, enter **Start time (ns)** and **End time (ns)** and choose
   **Apply range** or press Enter. Enter decimal values with a decimal point;
   scientific notation is supported.
4. Inspect **Selected data preview**, which follows the current selection. The
   captions show the inclusive sample indices, time interval, selected count, and
   excluded count.
5. Choose **Export selection…** or press **Ctrl+S**, select a destination, and save
   the DAS file. The selection must contain **2–16,384 samples**.

## Adjusting the range

| Control | Action |
| --- | --- |
| **Apply range** | Apply the times entered in the start and end fields. |
| **Limit from start** | Select up to 16,384 samples from the current applied start. |
| **Full range** | Select the entire dataset for inspection. |
| **Log signal scale** | Switch both plots between logarithmic and linear scales. |

The start and end times are inclusive. When a bound falls between samples, FLEE
uses the samples inside the requested interval and updates the fields to show
their actual times. You can inspect an oversized selection, but must narrow it
before exporting.

## Reading the plots

The logarithmic scale is enabled by default to help inspect low-count regions.
It uses `log10(1 + counts)` so zero counts remain visible. Labels show actual
counts as powers of ten: `10¹` means 10, `10²` means 100, and `10³` means 1,000.
Zero is labeled `0`. Clear **Log signal scale** to use a linear scale.

Both time axes always show zero. If your selected interval is entirely before or
after zero, the plot includes empty space up to zero; this does not add samples
to the selection.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| **Ctrl+O** | Open a CSV. |
| **Ctrl+S** | Export the current selection when export is available. |
| **Enter** in a time field | Apply the edited range. |
| **Escape** in a time field | Discard the edits and restore the applied bounds. |

## Loading errors and unavailable export

Selecting a new file discards the current dataset, range, and plots immediately,
before reading the file. If loading fails, the view stays empty, export remains
disabled, and the error stays visible until the next action. A successful load
displays the new dataset with its initial selection. Canceling the file picker
does not start a load.

If a file cannot be loaded, check the error message and confirm that its columns,
time spacing, and counts match the supported format.

If **Export selection…** is unavailable:

- Finish loading the file before exporting.
- Choose **Apply range** or press Enter after editing the time fields.
- Select at least two samples and no more than 16,384.
- Correct reversed bounds, nonfinite values, or an interval containing no samples.
  Invalid range edits leave the last applied preview visible until corrected.

## Exported data

The DAS file contains exactly the selected decay and prompt samples, with their
original spacing. FLEE does not combine bins, interpolate measurements, or change
the source CSV. If the source file changes while it is open, reopen it to use the
updated measurements.

The exported time axis is relative to the start of the selection. The original
time offset and selected interval are recorded in the file's comment.

For building from source or contributing, see the [developer guide](DEVELOPMENT.md).
