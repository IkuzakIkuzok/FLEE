namespace FLEE.Controls;

internal static class AxisTicks
{
    // Multiples of 1, 2, or 5 times a power of ten, always anchored at zero.
    internal static int Linear(Span<double> destination, double minimum, double maximum, int targetIntervals)
    {
        var step = NiceStep((maximum - minimum) / Math.Max(1, targetIntervals));
        var first = Math.Ceiling(minimum / step - 1e-10);
        var last = Math.Floor(maximum / step + 1e-10);
        var count = 0;
        for (var offset = 0; offset < destination.Length && first + offset <= last; offset++)
        {
            var value = (first + offset) * step;
            destination[count++] = value == 0 ? 0 : value; // Avoid a negative-zero label.
        }
        return count;
    }

    // Label actual counts at decades on the log(1 + counts) axis, retaining zero.
    internal static int Logarithmic(Span<double> destination, double maximumCoordinate, double minimumGap)
    {
        if (destination.IsEmpty) return 0;
        destination[0] = 0;
        var count = 1;
        var previousCoordinate = 0d;
        for (var exponent = 0; exponent <= maximumCoordinate && count < destination.Length; exponent++)
        {
            var value = Math.Pow(10, exponent);
            var coordinate = Math.Log10(1 + value);
            if (coordinate > maximumCoordinate) break;
            if (coordinate - previousCoordinate < minimumGap) continue;
            destination[count++] = value;
            previousCoordinate = coordinate;
        }
        return count;
    }

    private static double NiceStep(double target)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var fraction = target / magnitude;
        var factor = fraction switch { <= 1 => 1, <= 2 => 2, <= 5 => 5, _ => 10 };
        return factor * magnitude;
    }
}
