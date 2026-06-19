using System.Globalization;
using NCalc;
using NCalc.Handlers;

namespace ErsatzTV.Infrastructure.Streaming.Graphics;

public static class OpacityExpressionHelper
{
    public static void EvaluateFunction(string name, FunctionEventArgs args)
    {
        switch (name)
        {
            case "LinearFadePoints":
            {
                if (args.Parameters.Count != 5)
                {
                    throw new ArgumentException("LinearFadePoints() requires 5 arguments.");
                }

                var time = Convert.ToDouble(args.Parameters.Evaluate(0), CultureInfo.CurrentCulture);
                var start = Convert.ToDouble(args.Parameters.Evaluate(1), CultureInfo.CurrentCulture);
                var peakStart = Convert.ToDouble(args.Parameters.Evaluate(2), CultureInfo.CurrentCulture);
                var peakEnd = Convert.ToDouble(args.Parameters.Evaluate(3), CultureInfo.CurrentCulture);
                var end = Convert.ToDouble(args.Parameters.Evaluate(4), CultureInfo.CurrentCulture);

                args.Result = LinearFadePoints(time, start, peakStart, peakEnd, end);
                break;
            }
            case "LinearFadeDuration":
            {
                if (args.Parameters.Count != 4)
                {
                    throw new ArgumentException("LinearFadeDuration() requires 4 arguments.");
                }

                var time = Convert.ToDouble(args.Parameters.Evaluate(0), CultureInfo.CurrentCulture);
                var start = Convert.ToDouble(args.Parameters.Evaluate(1), CultureInfo.CurrentCulture);
                var fadeSeconds = Convert.ToDouble(args.Parameters.Evaluate(2), CultureInfo.CurrentCulture);
                var peakSeconds = Convert.ToDouble(args.Parameters.Evaluate(3), CultureInfo.CurrentCulture);

                args.Result = LinearFadeDuration(time, start, fadeSeconds, peakSeconds);
                break;
            }
        }
    }

    private static double LinearFadePoints(double time, double start, double peakStart, double peakEnd, double end)
    {
        if (time < start || time >= end)
        {
            return 0;
        }

        // fade in
        if (time < peakStart)
        {
            return (time - start) / (peakStart - start);
        }

        // solid
        if (time < peakEnd)
        {
            return 1.0;
        }

        // fade out
        return (end - time) / (end - peakEnd);
    }

    private static double LinearFadeDuration(double time, double start, double fadeSeconds, double peakSeconds)
    {
        // edge case with no fade
        if (fadeSeconds <= 0)
        {
            double noFadeEnd = start + peakSeconds;
            return time >= start && time < noFadeEnd ? 1.0 : 0.0;
        }

        double peakStart = start + fadeSeconds;
        double peakEnd = peakStart + peakSeconds;
        double end = peakEnd + fadeSeconds;

        return LinearFadePoints(time, start, peakStart, peakEnd, end);
    }

    public static float GetOpacity(
        Expression expression,
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime)
    {
        expression.Parameters["content_seconds"] = contentTime.TotalSeconds;
        expression.Parameters["content_total_seconds"] = contentTotalTime.TotalSeconds;
        expression.Parameters["channel_seconds"] = channelTime.TotalSeconds;
        expression.Parameters["time_of_day_seconds"] = timeOfDay.TotalSeconds;

        object expressionResult = expression.Evaluate();
        return Convert.ToSingle(expressionResult, CultureInfo.InvariantCulture);
    }
}
