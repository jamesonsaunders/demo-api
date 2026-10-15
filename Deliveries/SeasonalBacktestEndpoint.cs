using System.Globalization;
using DemoApi.Odoo;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DemoApi.Deliveries;

public sealed record SeasonalBacktest(
    string DataFrom,
    string DataThrough,
    int Years,
    double RecentWeight,
    bool WorkingDays,
    int Tests,
    BacktestScores Total,
    BacktestScores Late,
    BacktestScores OnTimeRate,
    LatestBacktest Latest);

public sealed record LatestBacktest(
    string TestFrom,
    string TestTo,
    string TrainedFrom,
    string TrainedThrough,
    int Years,
    IReadOnlyList<BacktestMonth> Months);

public sealed record BacktestMonth(string Month, BacktestPoint? Total, BacktestPoint? Late, BacktestPoint? OnTimeRate);

public sealed record BacktestPoint(double Actual, double Expected, double Low, double High, bool InRange);

public sealed record BacktestScores(
    int Months, double? Mae, double? Wape, double? Bias, double? Coverage, int CoverageMonths,
    double? SkillVsLastYear, double? SkillVsTrailing);

public static partial class DeliveryEndpoints
{
    private sealed record Scored(
        double Actual, double Expected, double Low, double High, double? LastYear, double Trailing,
        bool Calibrated, double Width);

    private sealed record Trial(DateOnly Origin, DateOnly Month, Scored? Total, Scored? Late, Scored? Rate);

    private sealed record Tuning(int Years, double RecentWeight, bool WorkingDays, List<Trial> Trials);

    // Misses holds sorted past miss sizes each scaled by Spread of its months ahead; ByAhead holds them unscaled per
    // month ahead (index 0 = 1 month). Ranges are symmetric so a lean in old misses can't skew them the wrong way
    // when the lean flips. Width would have put 80% of past actuals inside their ranges; it only widens, as calm
    // stretches otherwise narrow ranges right before volatile ones.
    private sealed record MetricCalibration(double[] Misses, double[][] ByAhead, double Width);

    private sealed record Calibration(MetricCalibration Total, MetricCalibration Late, MetricCalibration Rate);

    // Re-forecasts from every month with a year of history behind it, using only data known then,
    // and reports the settings that forecast total deliveries best.
    private static async Task<Results<Ok<SeasonalBacktest>, ProblemHttpResult>> GetSeasonalBacktest(
        string? branch, OdooClient odoo, DeliveryClock clock, CancellationToken ct)
    {
        if (await LatestDeliveryMonth(branch, odoo, clock, ct) is not { } dataThrough)
            return TypedResults.Problem("No delivery data found.", statusCode: 404);

        var (buckets, weekdays) = await MonthlyBuckets(
            dataThrough.AddMonths(-HistoryMonths), dataThrough, branch, odoo, clock, ct);
        if (buckets.Count == 0)
            return TypedResults.Problem("No delivery data found.", statusCode: 404);

        var firstUsable = FirstUsableMonth(buckets);
        if (Tune(buckets, weekdays, firstUsable, dataThrough) is not { } tuning)
            return TypedResults.Problem("At least 13 months of delivery history are needed to backtest.", statusCode: 404);

        var firstOrigin = firstUsable.AddMonths(11);
        var latestOrigin = dataThrough.AddMonths(-MaxProjectMonths);
        if (latestOrigin < firstOrigin) latestOrigin = firstOrigin;
        var latestFit = Fit(buckets, firstUsable, latestOrigin, tuning.Years)!;
        var latest = tuning.Trials.Where(t => t.Origin == latestOrigin).ToDictionary(t => t.Month);
        var months = Enumerable.Range(1, MonthsBetween(latestOrigin, dataThrough))
            .Select(i => latestOrigin.AddMonths(i))
            .Select(m => latest.TryGetValue(m, out var t)
                ? new BacktestMonth(YearMonth(m), Point(t.Total), Point(t.Late), Point(t.Rate))
                : new BacktestMonth(YearMonth(m), null, null, null))
            .ToList();

        return TypedResults.Ok(new SeasonalBacktest(
            YearMonth(firstUsable),
            YearMonth(dataThrough),
            tuning.Years,
            tuning.RecentWeight,
            tuning.WorkingDays,
            tuning.Trials.Select(t => t.Origin).Distinct().Count(),
            Summarize(tuning.Trials.Select(t => t.Total).OfType<Scored>().ToList(), isRate: false),
            Summarize(tuning.Trials.Select(t => t.Late).OfType<Scored>().ToList(), isRate: false),
            Summarize(tuning.Trials.Select(t => t.Rate).OfType<Scored>().ToList(), isRate: true),
            new LatestBacktest(
                YearMonth(latestOrigin.AddMonths(1)),
                YearMonth(dataThrough),
                YearMonth(latestFit.From),
                YearMonth(latestOrigin),
                latestFit.Years,
                months)));
    }

    // Picks the history length, recent-level weight and working-day adjustment whose forecasts missed total
    // deliveries least; ties go to fewer years, then less weight, then no adjustment.
    // TODO: cache the result per branch and month (it only changes when a month closes); this runs up to
    // 100 configurations, each re-forecasting from every past month, on every request.
    private static Tuning? Tune(
        Dictionary<DateOnly, Bucket> buckets, double[] weekdays,
        DateOnly firstUsable, DateOnly dataThrough)
    {
        var firstOrigin = firstUsable.AddMonths(11);
        if (firstOrigin >= dataThrough) return null;

        var maxYears = Math.Min(MaxSeasonalYears, MonthsBetween(firstUsable, dataThrough) / 12);
        return
            (from years in Enumerable.Range(1, maxYears)
             from weight in RecentWeights
             from workingDays in new[] { false, true }
             select new Tuning(years, weight, workingDays, RunTrials(
                 buckets, workingDays ? weekdays : null, firstUsable, firstOrigin, dataThrough, years, weight)))
            .Where(t => t.Trials.Any(x => x.Total is not null))
            .MinBy(t => TotalWape(t.Trials));
    }

    // Each origin's ranges are calibrated only on misses already known at that origin.
    private static List<Trial> RunTrials(
        Dictionary<DateOnly, Bucket> buckets, double[]? weekdays,
        DateOnly firstUsable, DateOnly firstOrigin, DateOnly dataThrough, int years, double recentWeight)
    {
        var trials = new List<Trial>();
        for (var origin = firstOrigin; origin < dataThrough; origin = origin.AddMonths(1))
        {
            if (Fit(buckets, firstUsable, origin, years, weekdays) is not { Trailing: not null } fit) continue;

            // The naive trailing baseline uses raw counts whatever the model settings.
            var last12 = Enumerable.Range(0, 12)
                .Select(i => buckets.GetValueOrDefault(origin.AddMonths(-i)))
                .Where(b => b.Promised > 0)
                .ToList();
            var trailing = (
                Total: last12.Average(b => b.Promised),
                Late: last12.Average(b => b.Late),
                Rate: (double)last12.Sum(b => b.OnTime) / last12.Sum(b => b.Promised));

            var known = Calibrate(trials.Where(t => t.Month <= origin));
            var forecast = Forecast(
                fit, origin, Math.Min(MaxProjectMonths, MonthsBetween(origin, dataThrough)), known, recentWeight);
            for (var i = 0; i < forecast.Count; i++)
            {
                var p = forecast[i];
                var month = origin.AddMonths(i + 1);
                var actual = buckets.GetValueOrDefault(month);
                if (actual.Promised == 0) continue;

                var lastYear = month.AddMonths(-12);
                var prior = lastYear >= firstUsable ? buckets.GetValueOrDefault(lastYear) : default;
                var hasPrior = prior.Promised > 0;
                trials.Add(new Trial(
                    origin,
                    month,
                    Score(p.Total, actual.Promised, hasPrior ? prior.Promised : null, trailing.Total, known.Total),
                    Score(p.Late, actual.Late, hasPrior ? prior.Late : null, trailing.Late, known.Late),
                    Score(p.OnTimeRate, (double)actual.OnTime / actual.Promised,
                        hasPrior ? (double)prior.OnTime / prior.Promised : null, trailing.Rate, known.Rate)));
            }
        }
        return trials;
    }

    // Misses are actual / expected - 1 for counts and actual - expected for the rate.
    private static Calibration Calibrate(IEnumerable<Trial> trials)
    {
        var list = trials.ToList();

        MetricCalibration Metric(Func<Trial, Scored?> pick, bool isRate)
        {
            var misses = list
                .Select(t => pick(t) is { } s && (isRate || s.Expected > 0)
                    ? (Ahead: MonthsBetween(t.Origin, t.Month),
                        Miss: Math.Abs(isRate ? s.Actual - s.Expected : s.Actual / s.Expected - 1))
                    : ((int Ahead, double Miss)?)null)
                .OfType<(int Ahead, double Miss)>()
                .ToList();
            double[] needed =
            [
                .. list.Select(pick).OfType<Scored>().Where(s => s.Calibrated)
                    .Select(NeededWidth).OfType<double>().Order(),
            ];
            return new MetricCalibration(
                [.. misses.Select(m => m.Miss / Spread(m.Ahead)).Order()],
                [
                    .. Enumerable.Range(1, MaxProjectMonths)
                        .Select(ahead => misses.Where(m => m.Ahead == ahead).Select(m => m.Miss).Order().ToArray()),
                ],
                needed.Length >= MinCalibrationSamples ? Math.Max(1, Quantile(needed, 1 - 2 * RangeTail)) : 1);
        }

        return new Calibration(Metric(t => t.Total, false), Metric(t => t.Late, false), Metric(t => t.Rate, true));
    }

    // The width multiplier that would have just included the actual, given the one the range was built with.
    private static double? NeededWidth(Scored s)
    {
        var side = s.Actual >= s.Expected ? s.High - s.Expected : s.Expected - s.Low;
        return side > 0 ? Math.Abs(s.Actual - s.Expected) / side * s.Width : null;
    }

    private static double TotalWape(List<Trial> trials)
    {
        var scored = trials.Select(t => t.Total).OfType<Scored>().ToList();
        return scored.Sum(s => Math.Abs(s.Expected - s.Actual)) / scored.Sum(s => s.Actual);
    }

    private static Scored? Score(Projection? p, double actual, double? lastYear, double trailing, MetricCalibration cal) =>
        p is null ? null : new Scored(
            Round(actual), p.Expected, p.Low, p.High, lastYear, trailing,
            cal.Misses.Length >= MinCalibrationSamples, cal.Width);

    private static BacktestPoint? Point(Scored? s) =>
        s is null ? null : new BacktestPoint(s.Actual, s.Expected, s.Low, s.High, s.Actual >= s.Low && s.Actual <= s.High);

    private static string YearMonth(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // Mae/Bias are in units for counts and in rate points for the on-time rate; Wape is counts only.
    // Coverage skips warm-up forecasts whose range wasn't yet based on past misses.
    private static BacktestScores Summarize(List<Scored> s, bool isRate)
    {
        if (s.Count == 0) return new BacktestScores(0, null, null, null, null, 0, null, null);

        var sumActual = s.Sum(x => x.Actual);
        var ranged = s.Where(x => x.Calibrated).ToList();
        return new BacktestScores(
            s.Count,
            Round(s.Average(x => Math.Abs(x.Expected - x.Actual))),
            isRate || sumActual == 0 ? null : Round(s.Sum(x => Math.Abs(x.Expected - x.Actual)) / sumActual),
            Round(s.Average(x => x.Expected - x.Actual)),
            ranged.Count == 0 ? null
                : Round((double)ranged.Count(x => x.Actual >= x.Low && x.Actual <= x.High) / ranged.Count),
            ranged.Count,
            Skill(s.Where(x => x.LastYear is not null).ToList(), x => x.LastYear!.Value),
            Skill(s, x => x.Trailing));
    }

    // 1 - model error / naive error: above 0 means the seasonal model beats the naive forecast.
    private static double? Skill(List<Scored> s, Func<Scored, double> naive)
    {
        var naiveError = s.Sum(x => Math.Abs(naive(x) - x.Actual));
        if (naiveError == 0) return null;
        return Round(1 - s.Sum(x => Math.Abs(x.Expected - x.Actual)) / naiveError);
    }
}
