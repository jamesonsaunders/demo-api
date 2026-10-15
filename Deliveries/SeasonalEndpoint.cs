using System.Globalization;
using DemoApi.Odoo;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DemoApi.Deliveries;

public sealed record SeasonalPattern(
    string DataFrom,
    string DataThrough,
    int Years,
    IReadOnlyList<SeasonalHistoryMonth> History,
    IReadOnlyList<SeasonalMonth> Months,
    SeasonalHighlights Highlights,
    IReadOnlyList<SeasonalProjection> Projection);

public sealed record SeasonalHistoryMonth(
    DateOnly PeriodStart,
    int DeliveriesPromised,
    int OnTime,
    int Late,
    double? OnTimeRate,
    bool Complete);

public sealed record SeasonalMonth(int Month, int Years, MonthStat Total, MonthStat Late, MonthStat OnTimeRate);

public sealed record MonthStat(double? Average, double? Typical, double? Index, int YearsAbove, int YearsBelow);

public sealed record SeasonalHighlights(Highlights Total, Highlights Late, Highlights OnTimeRate);

public sealed record Highlights(int? HighMonth, int? LowMonth, Season? HighSeason, Season? LowSeason);

public sealed record Season(int StartMonth, int EndMonth, double Index);

public sealed record SeasonalProjection(string Month, Projection? Total, Projection? Late, Projection? OnTimeRate);

public sealed record Projection(double Expected, double Low, double High);

// TODO: move the forecasting maths (Fit, Analyze, Project, Calibrate, Tune) into a service that takes a TimeProvider,
// and cover it with a test project run in CI.
public static partial class DeliveryEndpoints
{
    private const int DefaultSeasonalYears = 3;
    private const int MaxSeasonalYears = 10;
    private const int MaxProjectMonths = 12;
    private const int PartialMonthDays = 7;
    // Enough to try every history length with at least a year left to test against.
    private const int HistoryMonths = 12 * (MaxSeasonalYears + 1);
    private const int MinCalibrationSamples = 10;
    // Each tail outside the expected range, so the range targets 80% of outcomes.
    private const double RangeTail = 0.1;
    // The trailing 12-month average is centred this many months before the forecast origin.
    private const double TrailingCentre = 5.5;
    private const int RecentMonths = 3;
    // Candidate weights on the recent level vs the trailing average; the backtest picks one.
    private static readonly double[] RecentWeights = [0, 0.25, 0.5, 0.75, 1];

    private sealed record MetricSeries(MonthStat[] Stats, List<double>[] Deviations, double LevelShift);

    private sealed record SeasonalFit(
        int Years, DateOnly From, List<SeasonalHistoryMonth> History,
        MetricSeries Total, MetricSeries Late, MetricSeries OnTimeRate,
        (double Total, double Late, double Rate)? Trailing,
        (double? Total, double? Late, double? Rate) Recent,
        double[]? Weekdays);

    private static async Task<Results<Ok<SeasonalPattern>, ProblemHttpResult>> GetSeasonal(
        string? branch, int? years, int? projectMonths,
        OdooClient odoo, DeliveryClock clock, CancellationToken ct)
    {
        var requestedYears = years ?? DefaultSeasonalYears;
        var horizon = projectMonths ?? 0;
        if (requestedYears is < 1 or > MaxSeasonalYears)
            return TypedResults.Problem($"years must be between 1 and {MaxSeasonalYears}.", statusCode: 400);
        if (horizon is < 0 or > MaxProjectMonths)
            return TypedResults.Problem($"projectMonths must be between 0 and {MaxProjectMonths}.", statusCode: 400);

        if (await LatestDeliveryMonth(branch, odoo, clock, ct) is not { } dataThrough)
            return TypedResults.Problem("No delivery data found.", statusCode: 404);

        var (buckets, weekdays) = await MonthlyBuckets(
            dataThrough.AddMonths(-HistoryMonths), dataThrough, branch, odoo, clock, ct);
        if (buckets.Count == 0
            || Fit(buckets, FirstUsableMonth(buckets), dataThrough, requestedYears) is not { } fit)
            return TypedResults.Problem("Not enough delivery history yet.", statusCode: 404);

        List<SeasonalProjection> projection = [];
        if (horizon > 0)
        {
            // Project with the settings that backtested best, and ranges from their past misses.
            var firstUsable = FirstUsableMonth(buckets);
            var tuning = Tune(buckets, weekdays, firstUsable, dataThrough);
            var projectionFit = tuning is null ? fit
                : Fit(buckets, firstUsable, dataThrough, tuning.Years, tuning.WorkingDays ? weekdays : null) ?? fit;
            projection = Forecast(projectionFit, dataThrough, horizon,
                tuning is null ? null : Calibrate(tuning.Trials), tuning?.RecentWeight ?? 0);
        }

        var blocks = fit.History.Chunk(12).Take(fit.Years).ToList();
        var months = Enumerable.Range(0, 12)
            .Select(i => new SeasonalMonth(
                i + 1,
                blocks.Count(block => block.Any(h => h.Complete && h.PeriodStart.Month == i + 1)),
                fit.Total.Stats[i], fit.Late.Stats[i], fit.OnTimeRate.Stats[i]))
            .ToList();

        return TypedResults.Ok(new SeasonalPattern(
            fit.From.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            dataThrough.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            fit.Years,
            fit.History,
            months,
            new SeasonalHighlights(
                FindHighlights(fit.Total.Stats), FindHighlights(fit.Late.Stats), FindHighlights(fit.OnTimeRate.Stats)),
            projection));
    }

    private static async Task<DateOnly?> LatestDeliveryMonth(
        string? branch, OdooClient odoo, DeliveryClock clock, CancellationToken ct)
    {
        var currentMonth = PeriodStart(clock.Today, Period.Month);
        var latest = await odoo.SearchReadAsync(Model, BuildDomain(clock, null, currentMonth.AddDays(-1), branch),
            ["scheduled_date"], ct, limit: 1, order: "scheduled_date desc");
        if (latest.Count == 0 || clock.ParseDate(latest[0]?["scheduled_date"]) is not { } date) return null;

        // Data ending well before month end means that month was only partly loaded.
        var month = PeriodStart(date, Period.Month);
        return date < month.AddMonths(1).AddDays(-PartialMonthDays) ? month.AddMonths(-1) : month;
    }

    // Weekdays holds the average deliveries scheduled on each day of the week, indexed by DayOfWeek.
    private static async Task<(Dictionary<DateOnly, Bucket> Buckets, double[] Weekdays)> MonthlyBuckets(
        DateOnly fromMonth, DateOnly throughMonth, string? branch, OdooClient odoo, DeliveryClock clock, CancellationToken ct)
    {
        var today = clock.Today;
        var rows = await odoo.SearchReadAsync(Model,
            BuildDomain(clock, fromMonth, throughMonth.AddMonths(1).AddDays(-1), branch),
            ["scheduled_date", "date_done"], ct);

        var buckets = new Dictionary<DateOnly, Bucket>();
        var byWeekday = new int[7];
        foreach (var row in rows)
        {
            if (clock.ParseDate(row?["scheduled_date"]) is not { } scheduled) continue;

            var key = PeriodStart(scheduled, Period.Month);
            buckets[key] = Tally(buckets.GetValueOrDefault(key), scheduled, clock.ParseDate(row?["date_done"]), today);
            byWeekday[(int)scheduled.DayOfWeek]++;
        }

        var occurrences = new int[7];
        if (buckets.Count > 0)
            for (var d = buckets.Keys.Min(); d < throughMonth.AddMonths(1); d = d.AddDays(1))
                occurrences[(int)d.DayOfWeek]++;

        return (buckets, [.. byWeekday.Select((n, i) => occurrences[i] == 0 ? 0 : (double)n / occurrences[i])]);
    }

    // A month's expected deliveries at an average level, given its mix of weekdays.
    private static double Capacity(DateOnly month, double[] weekdays)
    {
        var capacity = 0d;
        for (var d = month; d < month.AddMonths(1); d = d.AddDays(1)) capacity += weekdays[(int)d.DayOfWeek];
        return capacity;
    }

    // The go-live month is partial, so usable history starts the month after.
    private static DateOnly FirstUsableMonth(Dictionary<DateOnly, Bucket> buckets) =>
        buckets.Keys.Min().AddMonths(1);

    // With weekdays, counts are modelled per unit of Capacity, so extra working days aren't read as demand.
    private static SeasonalFit? Fit(
        Dictionary<DateOnly, Bucket> buckets,
        DateOnly firstUsable, DateOnly through, int requestedYears, double[]? weekdays = null)
    {
        var span = MonthsBetween(firstUsable, through) + 1;
        if (span < 1) return null;

        var years = Math.Min(requestedYears, span / 12);
        var from = through.AddMonths(1 - (years > 0 ? 12 * years : span));

        SeasonalHistoryMonth HistoryMonth(DateOnly m)
        {
            var b = buckets.GetValueOrDefault(m);
            return new SeasonalHistoryMonth(m, b.Promised, b.OnTime, b.Late,
                b.Promised == 0 ? (double?)null : Rate(b.OnTime, b.Promised), b.Promised > 0);
        }

        var history = new List<SeasonalHistoryMonth>();
        for (var m = from; m <= through; m = m.AddMonths(1)) history.Add(HistoryMonth(m));
        // The year before the window only anchors the trend through the window's first year.
        var prior = years > 0 && from.AddMonths(-12) >= firstUsable
            ? Enumerable.Range(-12, 12).Select(i => HistoryMonth(from.AddMonths(i))).ToArray()
            : null;

        double Per(SeasonalHistoryMonth h) => weekdays is null ? 1 : Capacity(h.PeriodStart, weekdays);
        Func<SeasonalHistoryMonth, double> promised = h => h.DeliveriesPromised / Per(h);
        Func<SeasonalHistoryMonth, double> lateCount = h => h.Late / Per(h);
        Func<SeasonalHistoryMonth, double> onTimeRate = h => (double)h.OnTime / h.DeliveriesPromised;

        var blocks = history.Chunk(12).Take(years).ToList();
        var total = Analyze(blocks, prior, promised, isRate: false);
        var late = Analyze(blocks, prior, lateCount, isRate: false);
        var rate = Analyze(blocks, prior, onTimeRate, isRate: true);
        var trailing = history.TakeLast(12).Where(h => h.Complete).ToList();
        var recent = history.TakeLast(RecentMonths).Where(h => h.Complete).ToList();
        return new SeasonalFit(
            years, from, history, total, late, rate,
            trailing.Count == 0 ? null : (
                trailing.Average(promised),
                trailing.Average(lateCount),
                (double)trailing.Sum(h => h.OnTime) / trailing.Sum(h => h.DeliveriesPromised)),
            (RecentLevel(recent, total, promised, isRate: false),
                RecentLevel(recent, late, lateCount, isRate: false),
                RecentLevel(recent, rate, onTimeRate, isRate: true)),
            weekdays);
    }

    // Recent months with each month's seasonal effect removed, comparable to the trailing average.
    private static double? RecentLevel(
        List<SeasonalHistoryMonth> recent, MetricSeries series, Func<SeasonalHistoryMonth, double> value, bool isRate)
    {
        var levels = recent
            .Select(h => (Value: value(h), Center: series.Deviations[h.PeriodStart.Month - 1] is { Count: > 0 } devs
                ? Median(devs)
                : series.Stats[h.PeriodStart.Month - 1].Index))
            .Where(x => x.Center is { } c && (isRate || c > -1))
            .Select(x => isRate ? x.Value - x.Center!.Value : x.Value / (1 + x.Center!.Value))
            .ToList();
        return levels.Count == 0 ? null : levels.Average();
    }

    private static List<SeasonalProjection> Forecast(
        SeasonalFit fit, DateOnly through, int horizon, Calibration? calibration, double recentWeight)
    {
        if (fit.Years == 0 || fit.Trailing is not { } t) return [];

        double Level(double trailing, double? recent) =>
            recent is { } r ? trailing + recentWeight * (r - trailing) : trailing;
        var total = Level(t.Total, fit.Recent.Total);
        var late = Level(t.Late, fit.Recent.Late);
        var rate = Level(t.Rate, fit.Recent.Rate);

        return Enumerable.Range(1, horizon)
            .Select(i =>
            {
                var month = through.AddMonths(i);
                var idx = month.Month - 1;
                var capacity = fit.Weekdays is { } w ? Capacity(month, w) : 1;
                return new SeasonalProjection(
                    month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    Project(fit.Total, idx, total * capacity, isRate: false, calibration?.Total, i),
                    Project(fit.Late, idx, late * capacity, isRate: false, calibration?.Late, i),
                    Project(fit.OnTimeRate, idx, rate, isRate: true, calibration?.Rate, i));
            })
            .ToList();
    }

    // Each block is one 12-month year of history; only complete months contribute. Months are measured against a
    // trend drawn straight between yearly means, so growth during a year isn't read as season.
    private static MetricSeries Analyze(
        List<SeasonalHistoryMonth[]> blocks, SeasonalHistoryMonth[]? prior,
        Func<SeasonalHistoryMonth, double> value, bool isRate)
    {
        var samples = new List<SeasonalHistoryMonth>[12];
        var deviations = new List<double>[12];
        for (var i = 0; i < 12; i++) { samples[i] = []; deviations[i] = []; }
        var above = new int[12];
        var below = new int[12];

        var start = blocks.Count == 0 ? default : blocks[0][0].PeriodStart;
        double Offset(SeasonalHistoryMonth h) => MonthsBetween(start, h.PeriodStart);
        var points = (prior is null ? blocks : blocks.Prepend(prior))
            .Select(block => block.Where(h => h.Complete).ToList())
            .Where(complete => complete.Count > 0)
            .Select(complete => (At: complete.Average(Offset), Mean: complete.Average(value)))
            .ToList();

        double Trend(double at)
        {
            if (points.Count == 1) return points[0].Mean;
            var j = Math.Clamp(points.FindLastIndex(p => p.At <= at), 0, points.Count - 2);
            var (a, b) = (points[j], points[j + 1]);
            return a.Mean + (b.Mean - a.Mean) * (at - a.At) / (b.At - a.At);
        }

        foreach (var h in blocks.SelectMany(block => block).Where(h => h.Complete))
        {
            var i = h.PeriodStart.Month - 1;
            var v = value(h);
            var trend = Trend(Offset(h));
            samples[i].Add(h);
            if (v > trend) above[i]++;
            else if (v < trend) below[i]++;
            if (isRate) deviations[i].Add(v - trend);
            else if (trend > 0) deviations[i].Add(v / trend - 1);
        }

        var typicals = samples.Select(s => s.Count == 0 ? (double?)null : Median(s.Select(value))).ToArray();
        var known = typicals.OfType<double>().ToList();
        double? baseline = known.Count == 0 ? null : known.Average();

        var stats = new MonthStat[12];
        for (var i = 0; i < 12; i++)
        {
            var s = samples[i];
            double? average = s.Count == 0 ? null
                : isRate ? (double)s.Sum(h => h.OnTime) / s.Sum(h => h.DeliveriesPromised)
                : s.Average(value);
            double? index = typicals[i] is not { } t || baseline is not { } b ? null
                : isRate ? t - b
                : b == 0 ? null : t / b - 1;
            stats[i] = new MonthStat(Round(average), Round(typicals[i]), Round(index), above[i], below[i]);
        }

        return new MetricSeries(stats, deviations, LevelShift(blocks, value, isRate));
    }

    // Largest year-over-year change in level, from months complete in both years.
    private static double LevelShift(
        List<SeasonalHistoryMonth[]> blocks, Func<SeasonalHistoryMonth, double> value, bool isRate)
    {
        var shift = 0d;
        for (var y = 1; y < blocks.Count; y++)
        {
            var pairs = blocks[y].Zip(blocks[y - 1]).Where(p => p.First.Complete && p.Second.Complete).ToList();
            if (pairs.Count == 0) continue;

            var current = pairs.Average(p => value(p.First));
            var prior = pairs.Average(p => value(p.Second));
            if (isRate) shift = Math.Max(shift, Math.Abs(current - prior));
            else if (prior > 0) shift = Math.Max(shift, Math.Abs(current / prior - 1));
        }
        return shift;
    }

    private static Highlights FindHighlights(MonthStat[] stats)
    {
        var indexed = stats
            .Select((s, i) => (Month: i + 1, s.Index))
            .Where(x => x.Index is not null)
            .ToList();
        if (indexed.Count < 2) return new Highlights(null, null, null, null);

        var seasons = Enumerable.Range(0, 12)
            .Where(start => Enumerable.Range(start, 3).All(m => stats[m % 12].Index is not null))
            .Select(start => new Season(
                start + 1,
                (start + 2) % 12 + 1,
                Round(Enumerable.Range(start, 3).Average(m => stats[m % 12].Index!.Value))))
            .ToList();

        return new Highlights(
            indexed.MaxBy(x => x.Index).Month,
            indexed.MinBy(x => x.Index).Month,
            seasons.MaxBy(s => s.Index),
            seasons.MinBy(s => s.Index));
    }

    // With enough past misses, the range is expected plus or minus the size of 80% of them: from misses this many
    // months ahead when there are enough, else all misses scaled to this distance; then widened by Width.
    // Otherwise it spans this month's spread across past years plus the largest past yearly level shift.
    private static Projection? Project(
        MetricSeries series, int month, double baseline, bool isRate, MetricCalibration? calibration, int ahead)
    {
        if (series.Stats[month].Index is not { } index) return null;

        double Apply(double value, double level, double d) =>
            isRate ? Math.Clamp(value + level + d, 0, 1) : Math.Max(0, value * (1 + level) * (1 + d));

        var devs = series.Deviations[month];
        var (center, low, high) = devs.Count == 0 ? (index, index, index) : (Median(devs), devs.Min(), devs.Max());
        var expected = Apply(baseline, 0, center);
        var shift = series.LevelShift;
        if (calibration is not { Misses.Length: >= MinCalibrationSamples } c)
            return new Projection(
                Round(expected), Round(Apply(baseline, -shift, low)), Round(Apply(baseline, shift, high)));

        var half = (c.ByAhead[ahead - 1] is { Length: >= MinCalibrationSamples } misses
            ? Quantile(misses, 1 - 2 * RangeTail)
            : Quantile(c.Misses, 1 - 2 * RangeTail) * Spread(ahead)) * c.Width;
        return new Projection(Round(expected), Round(Apply(expected, 0, -half)), Round(Apply(expected, 0, half)));
    }

    // Misses grow with the square root of distance from the trailing average's centre, as level drift would.
    private static double Spread(int ahead) => Math.Sqrt(ahead + TrailingCentre);

    private static double Quantile(double[] sorted, double p)
    {
        var pos = p * (sorted.Length - 1);
        var i = (int)pos;
        return i + 1 < sorted.Length ? sorted[i] + (sorted[i + 1] - sorted[i]) * (pos - i) : sorted[i];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static int MonthsBetween(DateOnly from, DateOnly to) => (to.Year - from.Year) * 12 + to.Month - from.Month;

    private static double Round(double value) => Math.Round(value, 4);

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 4) : null;
}
