using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MahoyoHDRepack.Utility;

internal sealed class PercentageConsoleProgressReporter : IProgressWithTotal<double>
{
    private readonly double testMultiplicand;
    private readonly int numDigits;
    private readonly bool showValues;
    private readonly string? label;

    public PercentageConsoleProgressReporter(string? label, int numDigits, bool showValues)
    {
        this.label = label;
        testMultiplicand = 100 * double.Exp10(numDigits);
        this.numDigits = numDigits;
        this.showValues = showValues;
    }

    private double progress;
    public double Total { get; set; }
    private string? statusString;

    public void AddProgress(double amount)
    {
        double oldProgress, newProgress;
        do
        {
            oldProgress = progress;
            newProgress = oldProgress + amount;
        }
        while (Interlocked.CompareExchange(ref progress, newProgress, oldProgress) != oldProgress);

        var total = Total;
        var oldPct = oldProgress / total;
        var newPct = newProgress / total;

        // check if the value has meaningfully changed
        var oldTest = double.Floor(oldPct * testMultiplicand);
        var newTest = double.Floor(newPct * testMultiplicand);
        if (!stopwatch.IsRunning || showValues || oldTest != newTest)
        {
            MaybePrintProgress(newPct, newProgress, total, statusString);
        }
    }

    public void SetStatusString(string statusString)
    {
        string? oldStatusString;
        do
        {
            oldStatusString = this.statusString;
        }
        while (Interlocked.CompareExchange(ref this.statusString, statusString, oldStatusString) != oldStatusString);

        if (!stopwatch.IsRunning || oldStatusString != statusString)
        {
            var progress = Volatile.Read(ref this.progress);
            var total = Total;

            MaybePrintProgress(progress / total, progress, total, statusString);
        }
    }

    public void Complete()
    {
        if (stopwatch.IsRunning)
        {
            stopwatch.Stop();

            var progress = Volatile.Read(ref this.progress);
            var total = Total;

            MaybePrintProgress(progress / total, progress, total, Volatile.Read(ref statusString));
        }
    }

    private readonly Stopwatch stopwatch = new();

    private void MaybePrintProgress(double newPct, double newProgress, double total, string? statusString)
    {
        if (stopwatch.IsRunning && stopwatch.ElapsedMilliseconds < 6)
        {
            // write updates only every 6 milliseconds or so
            return;
        }

        PrintCurrentProgress(newPct, newProgress, total, statusString);
    }

    private int printingProgress;

    private void PrintCurrentProgress(double newPct, double newProgress, double total, string? statusString)
    {
        if (Interlocked.CompareExchange(ref printingProgress, 1, 0) != 0) return;

        //lock (lockObj)
        {
            var resultStringBuilder = new DefaultInterpolatedStringHandler(
                Helpers.ResetLineStr.Length + 5 + (label?.Length + 2 ?? 0) + (statusString?.Length ?? 0),
                5);

            resultStringBuilder.AppendLiteral("\r");
            resultStringBuilder.AppendLiteral(Helpers.ResetLineStr);
            if (label is not null)
            {
                resultStringBuilder.AppendLiteral(label);
                resultStringBuilder.AppendLiteral(": ");
            }

            var align = numDigits == 0 ? 3 : 4 + numDigits;
            resultStringBuilder.AppendFormatted(newPct * 100, alignment: align, format: $"F{numDigits}");
            resultStringBuilder.AppendLiteral("% ");

            if (showValues)
            {
                var totalStr = total.ToString(CultureInfo.CurrentCulture);
                resultStringBuilder.AppendLiteral("(");
                resultStringBuilder.AppendFormatted(newProgress, alignment: totalStr.Length);
                resultStringBuilder.AppendLiteral("/");
                resultStringBuilder.AppendFormatted(totalStr);
                resultStringBuilder.AppendLiteral(") ");
            }

            if (statusString is not null)
            {
                resultStringBuilder.AppendFormatted(statusString);
            }

            Console.Write(resultStringBuilder.ToStringAndClear());

            stopwatch.Restart();
        }

        Volatile.Write(ref printingProgress, 0);
    }
}
