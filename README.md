# Demo API

ASP.NET Core (.NET 10) minimal API reporting on outgoing deliveries (`stock.picking`) read from Odoo over JSON-RPC.
Access to Odoo is read-only (`search_read` and `fields_get` only).

## Running locally

```sh
dotnet user-secrets set "Odoo:Url" "https://yourcompany.odoo.com"
dotnet user-secrets set "Odoo:Database" "yourdb"
dotnet user-secrets set "Odoo:Login" "api-user@yourcompany.com"
dotnet user-secrets set "Odoo:Password" "your-password-or-api-key"
dotnet run
```

The app fails at startup if any required setting is missing or invalid.

## Configuration

| Key | Required | Default | Notes |
| --- | --- | --- | --- |
| `Odoo:Url` | yes | | Base URL of the Odoo instance. |
| `Odoo:Database` | yes | | Odoo database name. |
| `Odoo:Login` | yes | | Odoo user (read access to inventory is enough). |
| `Odoo:Password` | yes | | Password or API key. Keep in user-secrets / App Service settings, never in `appsettings.json`. |
| `Odoo:DelayReasonField` | yes | `x_delay_reason` | Custom field on `stock.picking` holding the delay reason. |
| `Deliveries:TimeZone` | no | `UTC` | IANA or Windows id (e.g. `America/Chicago`). Days and months are bucketed in this zone. |
| `Cors:AllowedOrigins` | no | none | Front-end origins allowed to call the API. |

In Azure App Service, set these as app settings using `__` as the separator (e.g. `Odoo__Password`).

## Endpoints

All delivery endpoints are `GET`, take an optional `branch` (warehouse name), and return
[problem details](https://www.rfc-editor.org/rfc/rfc9457) on errors: 400 for invalid input, 404 when there is not
enough data, 502/503 when Odoo fails or is unreachable.

| Route | Query | Returns |
| --- | --- | --- |
| `/api/deliveries/branches` | | Warehouse names. |
| `/api/deliveries/delay-reasons` | `start?`, `end?` (dates), `branch?` | Count of deliveries per delay reason. |
| `/api/deliveries/trends` | `months` (1-24), `compareTo` (`previousMonth`, `previous3Months`, `sameMonthLastYear`), `end?`, `branch?` | Late rates per branch and delay-reason counts per month, compared to a baseline, plus the biggest movers. |
| `/api/deliveries/seasonal` | `projectMonths?` (0-12, default 0), `branch?` | Monthly seasonal pattern, highlights and an optional projection with ranges, all from the setup that backtested best. |
| `/api/deliveries/seasonal/backtest` | `branch?` | Accuracy of past seasonal projections and the settings chosen for them. |
| `/api/deliveries/fields` | `search?` | Development only: `stock.picking` field names, to find custom fields. |
| `/health` | | Liveness check for App Service. |
| `/openapi/v1.json` | | Development only: OpenAPI document. |

## Deployment

Pushes to `master` build and deploy to the `demo-app888` Azure Web App via
[.github/workflows/master_demo-app888.yml](.github/workflows/master_demo-app888.yml). Set the App Service health check
path to `/health`.