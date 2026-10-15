using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace DemoApi.Odoo;

public sealed class OdooOptions
{
    [Required, Url] public string Url { get; set; } = "";
    [Required] public string Database { get; set; } = "";
    [Required] public string Login { get; set; } = "";
    [Required] public string Password { get; set; } = "";
    [Required] public string DelayReasonField { get; set; } = "";
}

public sealed class OdooException(string message, bool accessDenied = false) : Exception(message)
{
    public bool AccessDenied { get; } = accessDenied;
}

// Holds the logged-in Odoo user id across requests; OdooClient itself is transient.
public sealed class OdooSession
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _uid;

    public async Task<int> GetUidAsync(Func<CancellationToken, Task<int>> login, CancellationToken ct)
    {
        var uid = Volatile.Read(ref _uid);
        if (uid > 0) return uid;

        await _lock.WaitAsync(ct);
        try
        {
            if (_uid == 0) Volatile.Write(ref _uid, await login(ct));
            return _uid;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate(int uid) => Interlocked.CompareExchange(ref _uid, 0, uid);
}

// Read-only by design: search_read and fields_get are the only model methods exposed.
public sealed class OdooClient(
    HttpClient http, IOptions<OdooOptions> options, OdooSession session, ILogger<OdooClient> logger)
{
    private readonly OdooOptions _options = options.Value;

    public async Task<JsonArray> SearchReadAsync(
        string model, JsonArray domain, string[] fields, CancellationToken ct, int? limit = null, string? order = null)
    {
        var kwargs = new JsonObject
        {
            ["fields"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        };
        if (limit is { } l) kwargs["limit"] = l;
        if (order is not null) kwargs["order"] = order;
        var result = await ExecuteAsync(model, "search_read", new JsonArray(domain), kwargs, ct);

        return result as JsonArray ?? throw new OdooException("Unexpected search_read response.");
    }

    public async Task<JsonObject> FieldsGetAsync(string model, CancellationToken ct)
    {
        var kwargs = new JsonObject { ["attributes"] = new JsonArray("string", "type") };
        var result = await ExecuteAsync(model, "fields_get", new JsonArray(), kwargs, ct);

        return result as JsonObject ?? throw new OdooException("Unexpected fields_get response.");
    }

    private async Task<JsonNode?> ExecuteAsync(string model, string method, JsonArray args, JsonObject kwargs, CancellationToken ct)
    {
        Task<JsonNode?> Execute(int asUid) => CallAsync("object", "execute_kw",
            new JsonArray(_options.Database, asUid, _options.Password, model, method, args.DeepClone(), kwargs.DeepClone()), ct);

        var uid = await session.GetUidAsync(LoginAsync, ct);
        try
        {
            return await Execute(uid);
        }
        catch (OdooException ex) when (ex.AccessDenied)
        {
            logger.LogWarning("Odoo rejected the cached session for uid {Uid}; logging in again.", uid);
            session.Invalidate(uid);
            return await Execute(await session.GetUidAsync(LoginAsync, ct));
        }
    }

    private async Task<int> LoginAsync(CancellationToken ct)
    {
        var login = await CallAsync("common", "login",
            new JsonArray(_options.Database, _options.Login, _options.Password), ct);
        if (login is not JsonValue value || !value.TryGetValue<int>(out var uid) || uid <= 0)
            throw new OdooException("Odoo login failed.");

        logger.LogInformation("Logged in to Odoo as uid {Uid}.", uid);
        return uid;
    }

    private async Task<JsonNode?> CallAsync(string service, string method, JsonArray args, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "call",
            ["params"] = new JsonObject { ["service"] = service, ["method"] = method, ["args"] = args },
        };

        // Retries and timeouts come from the resilience handler registered in Program.cs.
        using var response = await http.PostAsJsonAsync("jsonrpc", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (body?["error"] is JsonNode error)
            throw new OdooException(
                $"Odoo {service}.{method} failed: {error["data"]?["message"] ?? error["message"]}",
                error["data"]?["name"]?.ToString() == "odoo.exceptions.AccessDenied");

        return body?["result"];
    }
}
