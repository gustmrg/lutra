namespace Lutra.Core.Health;

public static class StatisticsHelper
{
    public static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;

        var sum = 0.0;
        for (var i = 0; i < values.Count; i++)
            sum += values[i];

        return sum / values.Count;
    }

    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return 0;

        var mean = Mean(values);
        var sumSquaredDiffs = 0.0;
        for (var i = 0; i < values.Count; i++)
        {
            var diff = values[i] - mean;
            sumSquaredDiffs += diff * diff;
        }

        return Math.Sqrt(sumSquaredDiffs / (values.Count - 1));
    }

    public static double LinearRegressionSlope(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return 0;

        var n = values.Count;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumXy = 0.0;
        var sumX2 = 0.0;

        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXy += i * values[i];
            sumX2 += i * i;
        }

        var denominator = n * sumX2 - sumX * sumX;
        if (denominator == 0)
            return 0;

        return (n * sumXy - sumX * sumY) / denominator;
    }

    public static double PercentChange(double oldValue, double newValue)
    {
        if (oldValue == 0)
            return newValue == 0 ? 0 : 100;

        return (newValue - oldValue) / Math.Abs(oldValue) * 100;
    }
}
