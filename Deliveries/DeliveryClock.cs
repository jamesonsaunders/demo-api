using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DemoApi.Deliveries;

public sealed class DeliveryOptions
{
    // IANA (e.g. "America/Chicago") or Windows time zone id.
    public string TimeZone { get; set; } = "UTC";
}

// Odoo stores datetimes in UTC; days and months are reported in the configured business time zone.
public sealed class DeliveryClock(IOptions<DeliveryOptions> options)
{
    private const string OdooFormat = "yyyy-MM-dd HH:mm:ss";

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone));

    public DateOnly? ParseDate(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s)
        && DateTime.TryParseExact(s, OdooFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, _zone))
            : null;

    // The Odoo (UTC) datetime at which the local day starts.
    public string StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        // Some zones skip midnight on DST change days.
        while (_zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, _zone).ToString(OdooFormat, CultureInfo.InvariantCulture);
    }
}
