using System.Text.Json.Nodes;
using DemoApi.Odoo;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DemoApi.Deliveries;

public enum Period { Day, Week, Month, Year }

public sealed record DelayReasonCount(string Reason, int Count);

public sealed record FieldInfo(string Name, string Label, string Type);

public static partial class DeliveryEndpoints
{
    private const string Model = "stock.picking";

    private record struct Bucket(int Promised, int OnTime, int Late);

    public static void MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/deliveries");
        group.MapGet("/delay-reasons", GetDelayReasons);
        group.MapGet("/branches", GetBranches);
        group.MapGet("/seasonal", GetSeasonal);
        group.MapGet("/seasonal/backtest", GetSeasonalBacktest);
        group.MapGet("/trends", GetTrends);
    }

    // Field metadata only, to find custom field names (e.g. the delay reason).
    public static void MapDeliveryFieldDiscovery(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/deliveries/fields", async (string? search, OdooClient odoo, CancellationToken ct) =>
        {
            var fields = await odoo.FieldsGetAsync(Model, ct);
            var result = fields
                .Select(f => new FieldInfo(f.Key, f.Value?["string"]?.ToString() ?? "", f.Value?["type"]?.ToString() ?? ""))
                .Where(f => string.IsNullOrWhiteSpace(search)
                    || f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || f.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name)
                .ToList();
            return TypedResults.Ok(result);
        });

    private static async Task<Results<Ok<List<DelayReasonCount>>, ProblemHttpResult>> GetDelayReasons(
        DateOnly? start, DateOnly? end, string? branch,
        OdooClient odoo, DeliveryClock clock, IOptions<OdooOptions> options, CancellationToken ct)
    {
        if (start > end) return TypedResults.Problem("start must be on or before end.", statusCode: 400);

        var field = options.Value.DelayReasonField;
        var domain = BuildDomain(clock, start, end, branch);
        domain.Add(Leaf(field, "!=", false));

        var rows = await odoo.SearchReadAsync(Model, domain, [field], ct);

        var result = rows
            .Select(row => DisplayValue(row?[field]))
            .OfType<string>()
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DelayReasonCount(g.Key, g.Count()))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Reason)
            .ToList();

        return TypedResults.Ok(result);
    }

    private static async Task<Ok<List<string>>> GetBranches(OdooClient odoo, CancellationToken ct)
    {
        var rows = await odoo.SearchReadAsync("stock.warehouse", new JsonArray(), ["name"], ct);

        var result = rows
            .Select(row => DisplayValue(row?["name"]))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(result);
    }

    // Outgoing, non-cancelled pickings, filtered on scheduled date in the business time zone. Branch = warehouse name.
    private static JsonArray BuildDomain(DeliveryClock clock, DateOnly? start, DateOnly? end, string? branch)
    {
        var domain = new JsonArray(
            Leaf("picking_type_code", "=", "outgoing"),
            Leaf("state", "!=", "cancel"));

        if (start is { } s) domain.Add(Leaf("scheduled_date", ">=", clock.StartOfDay(s)));
        if (end is { } e) domain.Add(Leaf("scheduled_date", "<", clock.StartOfDay(e.AddDays(1))));
        if (!string.IsNullOrWhiteSpace(branch)) domain.Add(Leaf("picking_type_id.warehouse_id.name", "=", branch.Trim()));

        return domain;
    }

    // Not done and scheduled in the past counts as late.
    private static Bucket Tally(Bucket b, DateOnly scheduled, DateOnly? done, DateOnly today)
    {
        b.Promised++;
        if (done is { } d) { if (d <= scheduled) b.OnTime++; else b.Late++; }
        else if (scheduled < today) b.Late++;
        return b;
    }

    private static JsonArray Leaf(string field, string op, JsonNode? value) => new(field, op, value);

    // Odoo returns false for empty values and [id, name] for many2one fields.
    private static string? DisplayValue(JsonNode? node) => node switch
    {
        JsonArray { Count: 2 } pair when pair[1] is JsonValue name && name.TryGetValue<string>(out var s) => s.Trim(),
        JsonValue value when value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s.Trim(),
        _ => null,
    };

    private static DateOnly PeriodStart(DateOnly date, Period period) => period switch
    {
        Period.Day => date,
        Period.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        Period.Month => new DateOnly(date.Year, date.Month, 1),
        _ => new DateOnly(date.Year, 1, 1),
    };

    private static double Rate(int part, int total) => total == 0 ? 0 : Math.Round((double)part / total, 4);
}
