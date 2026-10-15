using System.Globalization;
using System.Text.Json.Nodes;
using DemoApi.Odoo;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DemoApi.Deliveries;

public sealed record DeliveryTrends(
    IReadOnlyList<string> Months,
    string CompareTo,
    int MinDeliveries,
    IReadOnlyList<Mover> Movers,
    IReadOnlyList<BranchTrend> Branches,
    IReadOnlyList<ReasonTrend> Reasons);

public sealed record Mover(string Kind, string Name, double From, double To);

public sealed record BranchMonth(int Deliveries, int Late, double? LateRate);

public sealed record BranchTrend(
    string Branch,
    IReadOnlyList<BranchMonth> Months,
    double? BaselineRate,
    double? Change,
    string? Trend,
    bool LowVolume);

public sealed record ReasonTrend(
    string Reason,
    IReadOnlyList<int> Counts,
    double? Baseline,
    double? Change,
    double? ChangeRate,
    double Share,
    double? ShareChange,
    string? Trend);

public static partial class DeliveryEndpoints
{
    private const int MaxTrendMonths = 24;
    private const int MinTrendDeliveries = 20;
    private const int MaxMovers = 5;
    private const double BranchMoverChange = 0.05;
    private const double ReasonMoverChangeRate = 0.25;
    private const int ReasonMoverMinCount = 3;
    private const int TrendWindow = 4;
    private const double TrendMinRateSlope = 0.01;
    private const double TrendMinCountSlope = 0.10;

    private static readonly string[] CompareModes = ["previousMonth", "previous3Months", "sameMonthLastYear"];

    private static async Task<Results<Ok<DeliveryTrends>, ProblemHttpResult>> GetTrends(
        string? branch, int? months, string? compareTo, DateOnly? end,
        OdooClient odoo, DeliveryClock clock, IOptions<OdooOptions> options, CancellationToken ct)
    {
        if (months is not (>= 1 and <= MaxTrendMonths))
            return TypedResults.Problem($"months must be between 1 and {MaxTrendMonths}.", statusCode: 400);
        var mode = CompareModes.FirstOrDefault(m => string.Equals(m, compareTo, StringComparison.OrdinalIgnoreCase));
        if (mode is null)
            return TypedResults.Problem($"compareTo must be one of: {string.Join(", ", CompareModes)}.", statusCode: 400);

        var today = clock.Today;
        var lastComplete = PeriodStart(today, Period.Month).AddMonths(-1);
        if (end is { } e && PeriodStart(e, Period.Month) > lastComplete)
            return TypedResults.Problem("end must be before the current month.", statusCode: 400);

        // The month containing end is the latest (compared) month.
        var latest = end is { } endDate ? PeriodStart(endDate, Period.Month) : lastComplete;
        DateOnly[] baselineMonths = mode switch
        {
            "previousMonth" => [latest.AddMonths(-1)],
            "previous3Months" => [latest.AddMonths(-3), latest.AddMonths(-2), latest.AddMonths(-1)],
            _ => [latest.AddMonths(-12)],
        };
        var firstShown = latest.AddMonths(1 - months.Value);

        // Always fetch the full trend window, even when fewer months are shown.
        var fetchFrom = new[] { firstShown, latest.AddMonths(1 - TrendWindow), baselineMonths[0] }.Min();
        var count = MonthsBetween(fetchFrom, latest) + 1;
        var last = count - 1;
        var shown = Enumerable.Range(MonthsBetween(fetchFrom, firstShown), months.Value).ToArray();
        var baseline = baselineMonths.Select(m => MonthsBetween(fetchFrom, m)).ToArray();

        var field = options.Value.DelayReasonField;
        var typesTask = odoo.SearchReadAsync("stock.picking.type",
            new JsonArray(Leaf("active", "in", new JsonArray(true, false))), ["warehouse_id"], ct);
        var warehousesTask = odoo.SearchReadAsync("stock.warehouse", new JsonArray(), ["name"], ct);
        var rowsTask = odoo.SearchReadAsync(Model, BuildDomain(clock, fetchFrom, latest.AddMonths(1).AddDays(-1), null),
            ["scheduled_date", "date_done", "picking_type_id", field], ct);
        await Task.WhenAll(typesTask, warehousesTask, rowsTask);

        var warehouseByType = new Dictionary<int, string>();
        foreach (var type in typesTask.Result)
            if (ParseId(type?["id"]) is { } id && DisplayValue(type?["warehouse_id"]) is { } name)
                warehouseByType[id] = name;

        var branchBuckets = new Dictionary<string, Bucket[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in warehousesTask.Result.Select(w => DisplayValue(w?["name"])).OfType<string>())
            branchBuckets.TryAdd(name, new Bucket[count]);

        var reasonCounts = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var reasonTotals = new int[count];
        var scopeDeliveries = new int[count];
        var branchFilter = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();

        foreach (var row in rowsTask.Result)
        {
            if (clock.ParseDate(row?["scheduled_date"]) is not { } scheduled) continue;
            var i = MonthsBetween(fetchFrom, scheduled);
            if (i < 0 || i > last) continue;

            string? warehouse = row?["picking_type_id"] is JsonArray { Count: 2 } type && ParseId(type[0]) is { } typeId
                ? warehouseByType.GetValueOrDefault(typeId)
                : null;

            if (warehouse is not null)
            {
                if (!branchBuckets.TryGetValue(warehouse, out var buckets))
                    branchBuckets[warehouse] = buckets = new Bucket[count];
                buckets[i] = Tally(buckets[i], scheduled, clock.ParseDate(row?["date_done"]), today);
            }

            if (branchFilter is not null && !string.Equals(warehouse, branchFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            scopeDeliveries[i]++;
            if (DisplayValue(row?[field]) is not { } reason) continue;
            if (!reasonCounts.TryGetValue(reason, out var counts))
                reasonCounts[reason] = counts = new int[count];
            counts[i]++;
            reasonTotals[i]++;
        }

        var branches = branchBuckets
            .Select(kv =>
            {
                var b = kv.Value;
                var rates = b.Select(m => m.Promised == 0 ? (double?)null : (double)m.Late / m.Promised).ToArray();
                var basePromised = baseline.Sum(i => b[i].Promised);
                double? baselineRate = basePromised == 0 ? null : (double)baseline.Sum(i => b[i].Late) / basePromised;
                return new BranchTrend(
                    kv.Key,
                    shown.Select(i => new BranchMonth(b[i].Promised, b[i].Late, Round(rates[i]))).ToList(),
                    Round(baselineRate),
                    Round(rates[last] - baselineRate),
                    Trend(rates, TrendMinRateSlope),
                    b[last].Promised < MinTrendDeliveries);
            })
            .OrderBy(b => b.Change is null)
            .ThenByDescending(b => b.Change)
            .ThenBy(b => b.Branch, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var baseScope = baseline.Sum(i => scopeDeliveries[i]);
        var baseTotal = baseline.Sum(i => reasonTotals[i]);
        var reasons = reasonCounts
            .Where(kv => shown.Concat(baseline).Any(i => kv.Value[i] > 0))
            .Select(kv =>
            {
                var c = kv.Value;
                var baseSum = baseline.Sum(i => c[i]);
                double? baseCount = baseScope == 0 ? null : (double)baseSum / baseline.Length;
                double? change = c[last] - baseCount;
                var share = reasonTotals[last] == 0 ? 0 : (double)c[last] / reasonTotals[last];
                double? shareChange = baseTotal == 0 ? null : share - (double)baseSum / baseTotal;
                return new ReasonTrend(
                    kv.Key,
                    shown.Select(i => c[i]).ToList(),
                    Round(baseCount),
                    Round(change),
                    baseCount > 0 ? Round(change / baseCount) : null,
                    Round(share),
                    Round(shareChange),
                    Trend(c.Select((v, i) => scopeDeliveries[i] == 0 ? (double?)null : v).ToArray(),
                        TrendMinCountSlope, relative: true));
            })
            .OrderBy(r => r.Change is null)
            .ThenByDescending(r => r.Change)
            .ThenBy(r => r.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Scored relative to each kind's threshold so rate and count moves can be ranked together.
        var movers = branches
            .Where(b => !b.LowVolume && b.Change is { } c && Math.Abs(c) >= BranchMoverChange)
            .Select(b => (Score: Math.Abs(b.Change!.Value) / BranchMoverChange,
                Mover: new Mover("branch", b.Branch, b.BaselineRate!.Value, b.Months[^1].LateRate!.Value)))
            .Concat(reasons
                .Where(r => r.Baseline > 0 && r.ChangeRate is { } cr && Math.Abs(cr) >= ReasonMoverChangeRate
                    && Math.Abs(r.Change!.Value) >= ReasonMoverMinCount)
                .Select(r => (Score: Math.Abs(r.ChangeRate!.Value) / ReasonMoverChangeRate,
                    Mover: new Mover("reason", r.Reason, r.Baseline!.Value, r.Counts[^1]))))
            .OrderByDescending(x => x.Score)
            .Take(MaxMovers)
            .Select(x => x.Mover)
            .ToList();

        return TypedResults.Ok(new DeliveryTrends(
            shown.Select(i => fetchFrom.AddMonths(i).ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToList(),
            mode,
            MinTrendDeliveries,
            movers,
            branches,
            reasons));
    }

    // Least-squares slope per month over the window; relative divides by the window mean.
    private static string? Trend(double?[] values, double minSlope, bool relative = false)
    {
        var recent = values.TakeLast(TrendWindow).ToArray();
        if (recent.Length < TrendWindow || recent.Any(v => v is null)) return null;
        var ys = recent.Select(v => v!.Value).ToArray();
        var xMean = (ys.Length - 1) / 2.0;
        var yMean = ys.Average();
        double num = 0, den = 0;
        for (var x = 0; x < ys.Length; x++)
        {
            num += (x - xMean) * (ys[x] - yMean);
            den += (x - xMean) * (x - xMean);
        }
        var slope = num / den;
        if (relative)
        {
            if (yMean == 0) return "stable";
            slope /= yMean;
        }
        return slope >= minSlope ? "rising" : slope <= -minSlope ? "falling" : "stable";
    }

    private static int? ParseId(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var id) ? id : null;
}
