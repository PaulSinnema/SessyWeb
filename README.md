# SessyWeb

**SessyWeb** is an open-source home energy management system (HEMS) built for households with one or more [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container and uses a Mixed Integer Linear Program (MILP) to optimise battery charge and discharge scheduling based on dynamic day-ahead electricity prices (EPEX Spot via ENTSO-E).

> ⚡ Charge cheap. Sell expensive. Let the sun do the rest.

---

## Features

### Smart battery planning
- **MILP-based optimiser** — plans charge, discharge and zero-net-home windows over a 72-hour horizon to maximise financial return
- **Dynamic EPEX Spot pricing** — fetches day-ahead quarter-hour prices and plans around them automatically
- **Solar forecast integration** — factors in predicted solar production to avoid unnecessary grid charging on sunny days
- **Netting / saldering support** — handles both netting-on (pre-2027 NL) and netting-off contracts correctly
- **SOC-aware planning** — respects configurable night reserve, cycle costs and battery capacity limits
- **Automatic re-planning** — rebuilds the plan on price updates, significant SOC deviations or settings changes

### Hardware integrations

| Category | Supported |
|---|---|
| **Batteries** | Sessy (1–3 units via Open API) |
| **Smart meters** | P1 (DSMR) |
| **Solar inverters** | SolarEdge (Modbus TCP), SMA, Enphase, Victron, Huawei, Sungrow, Solis, GoodWe, Sunspec-compatible |
| **Weather** | WeerLive API (radiation & temperature for consumption forecasting) |

### Monitoring & visualisation
- **Charging hours chart** — full plan-vs-actual view with charge, discharge, solar, prices and SOC over a 3-day window
- **Solar power chart** — realised vs forecast solar production
- **Consumption chart** — estimated household consumption per quarter
- **EPEX prices page** — day-ahead quarter-hour prices with buy/sell breakdown
- **Energy statistics** — daily, monthly and yearly energy totals
- **Financial results** — realised savings and revenue tracking
- **Investment tracker** — track battery/solar investments and estimated payback periods
- **Batteries status** — live SOC, power and system state per battery unit
- **Plan history** — full log of every plan rebuild with reason and expected profit

### Configuration
- All operational settings (cycle cost, reserve %, efficiency factors, consumption profile, manual override hours) managed via the built-in Settings page — no config file edits needed at runtime
- `appsettings.json` only requires infrastructure settings (timezone, backup path, battery IPs)

---

## Screenshots

> 📸 *Screenshots coming soon — contributions welcome.*

---

## Requirements

- Docker (or compatible container runtime)
- Sessy battery with firmware supporting Open API / Power Strategy API
- P1 smart meter with network-connected DSMR reader
- Solar inverter supported by one of the integrations above
- ENTSO-E Transparency Platform API key (free, for day-ahead prices)
- WeerLive API key (free, for weather data)

---

## Quick start

### 1. Clone the repository

```bash
git clone https://github.com/PaulSinnema/SessyWeb.git
cd SessyWeb
```

### 2. Configure `appsettings.json`

Copy the example below to `/SessyController/Config/appsettings.json` on your NAS (or wherever you mount the config volume) and fill in your values.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },

  "ConnectionStrings": {
    "SQLiteConnection": "Data Source=Sessy.db"
  },

  "AllowedHosts": "*",

  "Sessy:Batteries": {
    "Batteries": {
      "1": {
        "Name": "Battery 1",
        "BaseUrl": "http://<Battery1-IP>",
        "MaxCharge": 2200,
        "MaxDischarge": 1700,
        "Capacity": 5400
      },
      "2": {
        "Name": "Battery 2",
        "BaseUrl": "http://<Battery2-IP>",
        "MaxCharge": 2200,
        "MaxDischarge": 1700,
        "Capacity": 5400
      }
    }
  },

  "Sessy:Meters": {
    "Endpoints": {
      "P1": {
        "Name": "P1",
        "BaseUrl": "http://<P1-Meter-IP>"
      }
    }
  },

  "PowerSystems": {
    "Endpoints": {
      "SolarEdge": {
        "Interface": "Modbus",
        "IpAddress": "<SolarEdge-Inverter-IP>",
        "Port": 1502,
        "SlaveId": 1,
        "Latitude": 52.0,
        "Longitude": 5.0,
        "TimezoneOffset": 1
      }
    }
  },

  "WeerOnline": {
    "BaseUrl": "https://weerlive.nl/api/weerlive_api_v2.php",
    "Location": "<lat,lon>"
  },

  "ManagementSettings": {
    "Timezone": "Europe/Amsterdam",
    "DatabaseBackupDirectory": "/data/backups"
  }
}
```

All other settings (cycle cost, reserve percentage, efficiency factors, consumption profile, manual override hours, solar correction) are configured at runtime via the **Settings page** in the UI.

### 3. Deploy on Synology NAS (Docker / Container Manager)

| Setting | Value |
|---|---|
| Container name | `sessyweb` |
| Auto-restart | ✅ |
| Memory limit | 4096 MB |

**Ports**

| Host port | Container port |
|---|---|
| `8101` | `80` |
| `8100` | `443` |

**Volumes**

| NAS path | Container path |
|---|---|
| `/SessyController/Config` | `/SessyController/Config` |
| `/SessyController/data` | `/data` |

**Environment variables**

| Variable | Value |
|---|---|
| `ASPNETCORE_URLS` | `http://+:80` |
| `ASPNETCORE_HTTP_PORTS` | `80` |
| `CONFIG_PATH` | `/SessyController/Config` |

### 4. Access the UI

- **HTTP**: `http://<NAS-IP>:8101`
- **HTTPS**: `https://<NAS-IP>:8100`

---

## Architecture

```
SessyWeb          Blazor Server UI (Radzen components)
SessyController   Background services: MILP planner, hardware polling, solar forecasting
SessyData         EF Core / SQLite data layer
SessyCommon       Shared models, extensions, configuration
SessyUnitTests    Unit tests
```

The database is SQLite with WAL mode enabled. All data (measurements, plans, prices, settings) is stored locally — no cloud dependency beyond the ENTSO-E and WeerLive API calls.

---

## How the optimiser works

Every quarter-hour (or on price/SOC changes) SessyWeb runs a MILP solve over the next 72 hours:

1. **Fetch** current SOC, solar forecast, consumption forecast and EPEX prices
2. **Build** a tariff context: buy price, sell price and net load per quarter
3. **Solve** two plan segments (split search for best horizon split) using Google OR-Tools
4. **Post-process** the plan: enforce SOC limits, night reserve, curtailment rules
5. **Execute** one action per quarter via the Sessy Open API

The optimiser maximises total revenue (discharge profit minus charge cost minus cycle degradation cost) over the planning horizon.

---

## Troubleshooting

```bash
# View container logs
docker logs sessyweb

# Follow live
docker logs -f sessyweb
```

Common issues:
- **No prices loaded** — check your ENTSO-E API key and network access from the container
- **SOC deviation warnings** — normal; the planner auto-corrects every quarter
- **Inverter not found** — verify the IP address and Modbus port in `appsettings.json`

---

## Tech stack

- [.NET 9 / ASP.NET Core](https://dotnet.microsoft.com/) — Blazor Server
- [Radzen Blazor](https://blazor.radzen.com/) — UI components
- [Google OR-Tools](https://developers.google.com/optimization) — MILP solver
- [Entity Framework Core](https://docs.microsoft.com/en-us/ef/core/) + SQLite
- [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/) — day-ahead prices
- [WeerLive](https://weerlive.nl/) — weather & radiation data

---

## License

See [LICENSE.txt](LICENSE.txt).