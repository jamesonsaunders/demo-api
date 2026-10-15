using DemoApi.Deliveries;
using DemoApi.Odoo;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OdooOptions>()
    .Bind(builder.Configuration.GetSection("Odoo"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DeliveryOptions>()
    .Bind(builder.Configuration.GetSection("Deliveries"))
    .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "Deliveries:TimeZone is not a known time zone id.")
    .ValidateOnStart();

builder.Services.AddSingleton<DeliveryClock>();
builder.Services.AddSingleton<OdooSession>();
builder.Services.AddHttpClient<OdooClient>((sp, client) =>
    {
        client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<OdooOptions>>().Value.Url.TrimEnd('/') + "/");
        // The resilience handler's timeouts govern instead.
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    // All Odoo calls are read-only, so retrying the JSON-RPC POSTs is safe.
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
    });

builder.Services.AddExceptionHandler<OdooExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

// App Service terminates TLS at its front end and forwards the original scheme.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

// TODO: add rate limiting (AddRateLimiter) on /api/deliveries; seasonal and backtest are CPU-heavy per request.

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
// App Service health probes arrive over plain HTTP and must not be redirected.
app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/health"), b => b.UseHttpsRedirection());
app.UseCors();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapHealthChecks("/health");

// TODO: require authentication (e.g. Entra ID JWT) on the delivery endpoints; the deployed API is public.
app.MapDeliveryEndpoints();
if (app.Environment.IsDevelopment()) app.MapDeliveryFieldDiscovery();

app.Run();
