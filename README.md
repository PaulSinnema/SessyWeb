# SessyWeb

**SessyWeb** is an open-source home energy management system (HEMS) for households with one or more [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container on a NAS, mini-PC or Raspberry Pi and uses a Mixed Integer Linear Program (MILP) to plan battery charging and discharging against dynamic day-ahead electricity prices, your solar forecast and your household consumption.

> ⚡ Charge cheap. Sell expensive. Let the sun do the rest.

Everything runs locally. The database is a SQLite file on your own disk; the only outbound calls are for prices and weather.

> [!IMPORTANT]
> SessyWeb actively controls your batteries and can throttle or shut down your solar inverter. Run it at your own risk. Start with **Charged in control** ticked — SessyWeb then only watches and records while your batteries keep running on their own schedule — and hand control over once the charts look right.
>
> Note that **Manual override** is *not* an off switch: it means SessyWeb drives the batteries on the hours you picked by hand instead of on the plan.

---

## Table of contents

- [What you need before you start](#what-you-need-before-you-start)
- [Installation](#installation) — the step-by-step guide
  - [Step 1 — Get your API keys](#step-1--get-your-api-keys)
  - [Step 2 — Find your device IP addresses and passwords](#step-2--find-your-device-ip-addresses-and-passwords)
  - [Step 3 — Create the folders](#step-3--create-the-folders)
  - [Step 4 — Write `appsettings.json`](#step-4--write-appsettingsjson)
  - [Step 5 — Write `secrets.json`](#step-5--write-secretsjson)
  - [Step 6 — Build and start the container](#step-6--build-and-start-the-container)
  - [Step 7 — Open the web interface](#step-7--open-the-web-interface)
  - [Step 8 — First-run checklist inside the UI](#step-8--first-run-checklist-inside-the-ui)
- [Installing on a Synology NAS](#installing-on-a-synology-nas)
- [Configuration reference](#configuration-reference)
- [Features](#features)
- [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)
- [How the planner works](#how-the-planner-works)
- [Tech stack](#tech-stack)
- [License](#license)

---

## What you need before you start

**Hardware**

| | Required? | Notes |
|---|---|---|
| Sessy home battery (1–3) | Yes | Must be on your local network with the **Open API / local API enabled** in the Sessy app |
| Sessy P1 dongle (or another networked DSMR P1 reader) | Yes | SessyWeb refuses to start the meter service without one |
| Solar inverter | Optional | Only if you have solar panels — see the supported list below |

**Accounts (all free)**

| | Required? | What it is for |
|---|---|---|
| [WeerLive](https://weerlive.nl/delen.php) API key | Yes | Solar radiation and temperature forecast |
| [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/) token | Recommended | Fallback source for day-ahead prices |
| [Enever](https://enever.nl/token-aanmaken/) token | Optional | Daily gas price, used only for heat-pump savings figures |

**Software**

- Docker, or Docker Desktop, or Synology Container Manager. Nothing else — you do not need .NET installed to run SessyWeb.
- About 4 GB of memory available for the container (the MILP solver is the hungry part).

**Skill level**: you need to be able to edit a text file and run one or two commands in a terminal. No programming required.

---

## Installation

### Step 1 — Get your API keys

**WeerLive (required).** Go to <https://weerlive.nl/delen.php>, fill in your e-mail address and you get a key immediately. Keep it — you will paste it in Step 5.

**ENTSO-E (recommended).** SessyWeb reads day-ahead prices from your Sessy batteries first, because they already receive them. ENTSO-E is the fallback for when the batteries are unreachable — without it, a battery outage also means no prices and no plan.

1. Create an account at <https://transparency.entsoe.eu/>.
2. E-mail **transparency@entsoe.eu** with the subject *"Restful API access"* and your account e-mail address in the body.
3. They reply within a few working days. Then log in and copy your **Security token** from *My Account Settings → Web Api Security Token*.

**Enever (optional).** Free token at <https://enever.nl/token-aanmaken/>. Only affects the heat-pump savings page. Skip it if you have no heat pump.

### Step 2 — Find your device IP addresses and passwords

You need, for **each** Sessy battery and for the P1 dongle:

- the **IP address** — visible in the Sessy app, or in your router's DHCP client list;
- the **username and password** — printed on the *installation card* that came with the device, and also shown in the Sessy app under the device's local API settings.

> [!TIP]
> Give every Sessy device and your inverter a **fixed IP address** (a DHCP reservation) in your router. If an address changes later, SessyWeb silently loses that device.

If you have solar panels, also note your inverter's IP address and Modbus TCP port (SolarEdge uses `1502`; Modbus TCP must be enabled in the inverter's own settings).

### Step 3 — Create the folders

SessyWeb keeps its configuration and its database outside the container, so an update never destroys your data. Create two folders next to where you will run it:

```bash
mkdir -p sessyweb/config
mkdir -p sessyweb/data
```

On a Synology NAS these are typically `/volume1/SessyController/Config` and `/volume1/SessyController/Data`.

### Step 4 — Write `appsettings.json`

Create a file called **`appsettings.json`** in the `config` folder. This file holds infrastructure only: where your devices are and where the database lives. Everything you will want to tune later (strategy, cycle cost, reserve, efficiency) is set from the web interface, not here.

Start from this example and replace every `<...>` placeholder:

```jsonc
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning"
    }
  },

  "ConnectionStrings": {
    "SQLiteConnection": "Data Source=/SessyController/Data/Sessy.db"
  },

  "AllowedHosts": "*",

  // EIC code of your bidding zone. 10YNL----------L is the Netherlands.
  "ENTSO-E:InDomain": "10YNL----------L",
  "ENTSO-E:ResolutionFormat": "PT15M",

  // One entry per battery, numbered "1", "2", "3".
  "Sessy:Batteries": {
    "Batteries": {
      "1": {
        "Name": "Battery 1",
        "BaseUrl": "http://<battery-1-ip>",
        "MaxCharge": 2200,
        "MaxDischarge": 1700,
        "Capacity": 5400
      },
      "2": {
        "Name": "Battery 2",
        "BaseUrl": "http://<battery-2-ip>",
        "MaxCharge": 2200,
        "MaxDischarge": 1700,
        "Capacity": 5400
      }
    }
  },

  // Your P1 dongle.
  "Sessy:Meters": {
    "Endpoints": {
      "P1": {
        "Name": "P1",
        "BaseUrl": "http://<p1-dongle-ip>"
      }
    }
  },

  // Solar inverter. Remove this whole section if you have no solar panels.
  // The outer key ("SolarEdge") selects the driver — see the supported list.
  // The inner key ("1") is the inverter number; add "2" for a second inverter.
  "PowerSystems": {
    "Endpoints": {
      "SolarEdge": {
        "1": {
          "Interface": "Modbus",
          "IpAddress": "<inverter-ip>",
          "Port": 1502,
          "SlaveId": 1,
          "InverterMaxCapacity": 5000,
          "SolarPanels": {
            "1": {
              "PanelCount": 10,
              "Tilt": 35,
              "PeakPowerPerPanel": 340,
              "Efficiency": 0.82,
              "TotalArea": 17,
              "Orientation": 78,
              "HighestDailySolarProduction": 15500
            }
          }
        }
      }
    }
  },

  "WeerOnline": {
    "BaseUrl": "https://weerlive.nl/api/weerlive_api_v2.php",
    "Location": "<latitude,longitude>"
  },

  "ManagementSettings": {
    "Timezone": "Europe/Amsterdam",
    "DatabaseBackupDirectory": "/SessyController/Data/Backups"
  }
}
```

Notes on the values:

- **`MaxCharge` / `MaxDischarge` / `Capacity`** are **per battery**, in Watts and Watt-hours. A Sessy 5.2 is roughly `2200` / `1700` / `5400`. SessyWeb adds them up itself — do not enter system totals.
- **`Location`** is `latitude,longitude` for the weather forecast, e.g. `52.2185,5.947`. Look yours up on any map site.
- **`Tilt`** is the roof angle in degrees, **`Orientation`** is the compass bearing the panels face (180 = due south). Add one numbered entry under `SolarPanels` per group of panels with a different roof face.
- **`HighestDailySolarProduction`** is the most Wh your array has ever made in a day. A rough guess is fine — it is a scaling hint for the forecast, and you can refine it later.
- **`Interface`, `Port`, `SlaveId`** stay as shown for SolarEdge.
- `//` comments are allowed in these files; the loader ignores them.

Supported values for the driver key under `PowerSystems:Endpoints`:

`SolarEdge` · `Sma` · `Enphase` · `Victron` · `Huawei` · `Sungrow` · `Solis` · `GoodWe`

Spelling matters — the key must match exactly, or the inverter is silently skipped.

### Step 5 — Write `secrets.json`

Passwords and API keys go in a **separate** file called **`secrets.json`**, in the same `config` folder. It is listed in `.gitignore`, so it never ends up in a commit if you fork this project.

```json
{
  "Sessy:Batteries": {
    "Batteries": {
      "1": { "UserId": "<battery-1-username>", "Password": "<battery-1-password>" },
      "2": { "UserId": "<battery-2-username>", "Password": "<battery-2-password>" }
    }
  },

  "Sessy:Meters": {
    "Endpoints": {
      "P1": { "UserId": "<p1-username>", "Password": "<p1-password>" }
    }
  },

  "WeerOnline": {
    "APIKey": "<your-weerlive-key>"
  },

  "ENTSO-E:SecurityToken": "<your-entsoe-token>",

  "Enever:Token": "<your-enever-token>"
}
```

The numbering must line up with `appsettings.json`: battery `"1"` here is battery `"1"` there. The two files are merged, so credentials land on the right device.

> [!WARNING]
> The P1 username and password are **not optional**. Leave them out and the meter service throws `Password for P1 configuration with id P1 is empty` at startup.

### Step 6 — Build and start the container

There is no pre-built image yet, so you build it once from the source. Clone the repository:

```bash
git clone https://github.com/PaulSinnema/SessyWeb.git
cd SessyWeb
```

Create a file called **`docker-compose.yml`** in that folder:

```yaml
services:
  sessyweb:
    build:
      context: .
      dockerfile: SessyWeb/Dockerfile
    container_name: sessyweb
    restart: unless-stopped
    ports:
      - "8101:80"
    volumes:
      # Left of the colon: the folders you made in Step 3. Use full paths if
      # they are not next to this file.
      - ./sessyweb/config:/SessyController/Config
      - ./sessyweb/data:/SessyController/Data
    environment:
      - ASPNETCORE_URLS=http://+:80
      - ASPNETCORE_HTTP_PORTS=80
      - CONFIG_PATH=/SessyController/Config
    mem_limit: 4g
```

Then build and start it:

```bash
docker compose up -d --build
```

The first build downloads the .NET SDK image and takes several minutes. Later starts are instant. Watch it come up with:

```bash
docker compose logs -f
```

SessyWeb creates the SQLite database and applies its migrations on the first start; you do not have to prepare anything.

### Step 7 — Open the web interface

Browse to **`http://<ip-of-your-machine>:8101`**.

The container speaks plain HTTP. If you want HTTPS, put it behind a reverse proxy (Synology's built-in one, Nginx Proxy Manager, Caddy). Do not expose SessyWeb directly to the internet — it has no login screen.

### Step 8 — First-run checklist inside the UI

The container is running, but the planner does not know your household yet. Everything below is on the **Settings** page, split over its tabs. Work through them once, in this order:

1. **Management Settings** — fill in:
   - **Latitude** and **Longitude** of your house (used for sunrise and sunset);
   - **Annual production (kWh)** of your solar array;
   - **Optimization strategy** — pick one:
     - *Profit maximization* — trade as hard as the prices allow,
     - *Balanced* — the usual choice, trades but keeps reserve for the house,
     - *Self consumption* — mostly store your own solar,
     - *Battery saving* — fewest cycles, gentlest on the battery;
   - leave **Night reserve cap**, **Throttle fallback**, **Round-trip efficiency fallback** and the whole *Planning* block at their defaults for now — SessyWeb measures better values from your own data within a few weeks.
   - Further down the same tab you set your **monthly household consumption** per month, in kWh. Read them off last year's energy bill, or divide your yearly total and adjust winter up and summer down.
2. **Taxes** — enter the energy tax, surcharges and VAT from your energy contract, and whether you have **netting (saldering)**. Prices are meaningless until this is filled in: SessyWeb plans on the all-in price you actually pay, not the raw market price.
3. **Tips & Checks** — this tab tells you what is still missing or misconfigured. Work until it is quiet.
4. **Charging hours** (in the menu, not in Settings) — after a few minutes you should see prices, a solar forecast and a plan. If the chart stays empty, go to [Troubleshooting](#troubleshooting).
5. Only then hand over control: on **Management Settings**, untick **Charged in control** and leave **Manual override** off. SessyWeb now drives.

Leave it running for a day before trusting it. The planner needs measurements to learn your battery's real charging behaviour and efficiency.

---

## Installing on a Synology NAS

Container Manager cannot build an image from source, so build it elsewhere first (on a PC with Docker) and export it:

```bash
docker build -t sessyweb:latest -f SessyWeb/Dockerfile .
docker save sessyweb:latest -o sessyweb.tar
```

Copy `sessyweb.tar` to the NAS, then in **Container Manager → Image → Add → Add from file**, import it and create a container with these settings:

| Setting | Value |
|---|---|
| Container name | `sessyweb` |
| Enable auto-restart | ✅ |
| Memory limit | 4096 MB |

**Port settings**

| Local port | Container port | Type |
|---|---|---|
| `8101` | `80` | TCP |

**Volume settings**

| NAS folder | Mount path |
|---|---|
| `/volume1/SessyController/Config` | `/SessyController/Config` |
| `/volume1/SessyController/Data` | `/SessyController/Data` |

**Environment**

| Variable | Value |
|---|---|
| `ASPNETCORE_URLS` | `http://+:80` |
| `ASPNETCORE_HTTP_PORTS` | `80` |
| `CONFIG_PATH` | `/SessyController/Config` |

Put `appsettings.json` and `secrets.json` in the Config folder before starting the container.

---

## Configuration reference

Two separate systems, and it helps to know which is which:

| | Where | Contains | Changing it |
|---|---|---|---|
| **Infrastructure** | `appsettings.json` + `secrets.json` in `CONFIG_PATH` | Device addresses, credentials, API keys, timezone, database path | Edit the file; most keys are re-read live, a restart is always safe |
| **Operation** | The `Settings` row in the database | Strategy, cycle cost, reserve %, efficiency, consumption profile, manual override | The **Settings** page in the UI; takes effect immediately, no restart |

`CONFIG_PATH` tells SessyWeb which folder to read; it defaults to the working directory. Every key can also be supplied as an environment variable by replacing `:` with `__`, e.g. `ManagementSettings__Timezone=Europe/Amsterdam`.

Optional sections in `appsettings.json`:

```jsonc
// SolarEdge cloud fallback, used when the inverter is unreachable over Modbus.
"SolarEdgeCloud": { "SiteId": "", "ApiKey": "" },

// Heat pump savings tracking, compared against your old gas bill.
"HeatPumpConfig": {
  "AnnualGasConsumptionM3": 950,
  "GasPriceEurPerM3": 1.45,
  "GasStandingChargeEurPerYear": 185.0,
  "AnnualElectricityConsumptionKWh": 2300,
  "InstallationDate": "2024-03-01"
}
```

---

## Features

### Planning

- **MILP optimiser** — plans charge, discharge and zero-net-home windows over a 72-hour horizon on quarter-hour resolution
- **Dynamic prices** — day-ahead quarter-hour prices from your batteries, with ENTSO-E as fallback
- **Solar and consumption forecast** — avoids buying at night what the roof will make tomorrow
- **Netting / saldering aware** — handles both netting-on and netting-off contracts
- **Curtailment** — throttles or shuts down the inverter when the selling price goes negative
- **Self-measuring** — learns your batteries' real charging taper, efficiency curve and forecast error from your own history instead of assuming datasheet numbers
- **Automatic re-planning** — rebuilds on price updates, large SOC deviations or settings changes

### Hardware

| Category | Supported |
|---|---|
| Batteries | Sessy (1–3 units, local Open API) |
| Smart meters | P1 / DSMR (Sessy P1 dongle and compatible readers) |
| Solar inverters | SolarEdge, SMA, Enphase, Victron, Huawei, Sungrow, Solis, GoodWe (Modbus TCP / SunSpec) |
| Weather | WeerLive |

### Monitoring

- **Charging hours** — plan versus actual, with charge, discharge, solar, prices and SOC over three days
- **Solar power** — realised versus forecast production
- **Consumption** — estimated household use per quarter
- **EPEX prices** — day-ahead prices with the full buy/sell breakdown
- **Energy statistics** — daily, monthly and yearly totals
- **Financial results** — realised savings and revenue
- **Investments** — track what you spent and what the payback looks like
- **Batteries** — live SOC, power and state per unit
- **Plan history** — every rebuild with its reason and expected profit

---

## Troubleshooting

```bash
docker compose logs -f          # follow the log
docker compose restart          # restart after editing appsettings.json
```

| Symptom | Cause and fix |
|---|---|
| `⚠️ Warning: appsettings.json missing!` in the log | The config volume is not mounted where SessyWeb looks. Check that your Config folder maps to `/SessyController/Config` and that `CONFIG_PATH` matches. |
| `Password for P1 configuration with id P1 is empty` | The P1 credentials are missing from `secrets.json`, or the key numbering does not match `appsettings.json`. |
| No prices, empty chart | Batteries unreachable **and** no ENTSO-E token. Check the log for `Day-ahead prices now come from ENTSO-E`, verify the token, and confirm the container can reach your batteries (`docker exec sessyweb curl -u <user>:<password> http://<battery-ip>/api/v1/power/status`). |
| Prices look wrong / profit makes no sense | The **Taxes** page has not been filled in. The planner works on the all-in price, not the market price. |
| Inverter not found | Wrong provider key under `PowerSystems:Endpoints` (case-sensitive), wrong IP, or Modbus TCP not enabled in the inverter itself. |
| Battery does not react | On **Settings → Management Settings**, check **Charged in control** — if it is ticked, SessyWeb deliberately sends no commands. Refused writes are logged as warnings, so the log tells you which mode blocked them. |
| SOC deviation warnings | Normal. The planner corrects every quarter. |
| Container restarts or is killed | Raise the memory limit; the solver needs room. |
| Everything is slow | Look for `DbHelper: slow`, `ThreadPool busy` or `UI blocked` in the log and open an issue with those lines. |

---

## For developers

Target framework is **net10.0** across all projects.

```powershell
# Build
dotnet build SessyController.sln

# Run locally (SessyWeb is the only executable project)
dotnet run --project SessyWeb\SessyWeb.csproj

# Tests (xunit v3)
dotnet test SessyUnitTests\SessyUnitTests.csproj
```

Running locally still reads `appsettings.json` from `CONFIG_PATH` (default: the working directory), not from the project folder.

### Project layout

| Project | Role |
|---|---|
| `SessyWeb` | Blazor Server UI (Radzen), `Program.cs` with all dependency injection, API controllers, EF migrations |
| `SessyController` | Domain and background services: MILP planner, hardware polling, state machine, inverter drivers |
| `SessyData` | EF Core / SQLite: `ModelContext`, entities, one data service per entity |
| `SessyCommon` | Configuration POCOs, extensions, time zone service, service locator |
| `Djohnnie.SolarEdge.ModBus.TCP` | Vendored SolarEdge Modbus library |
| `SessyUnitTests` | xunit v3 + Moq |

### Database migrations

`ModelContext` lives in SessyData while the startup project is SessyWeb, so both flags are required. Build first — `dotnet ef` reads the compiled assembly.

```powershell
dotnet build SessyController.sln
dotnet ef migrations add <Name> --project SessyData --startup-project SessyWeb
```

Migrations are applied automatically at startup, preceded by an automatic `VACUUM INTO` backup into `DatabaseBackupDirectory` whenever there are pending ones.

An API browser is available at `/swagger`.

---

## How the planner works

Every minute (and on every price or settings change) SessyWeb rebuilds its picture of the next 72 hours:

1. **Gather** one record per quarter-hour: price, solar forecast, consumption forecast, mode and SOC.
2. **Solve** with Google OR-Tools, maximising discharge revenue minus charge cost minus cycle degradation cost, subject to capacity, reserve and efficiency constraints.
3. **Decide** the actual battery mode and inverter setpoint in the state machine, where curtailment can override the plan.
4. **Execute** one action per quarter through the Sessy local API.

The plan is only rebuilt when something material changed — a new price set, a SOC that drifted more than 20 % from plan, or a settings change.

> [!NOTE]
> A battery that never reaches 100 % is usually the planner being right, not wrong. Filling the battery costs money; it only pays if the energy can be sold higher later. Judge SessyWeb on the **Financial results** page, not on the SOC.

---

## Tech stack

- [.NET 10 / ASP.NET Core](https://dotnet.microsoft.com/) — Blazor Server
- [Radzen Blazor](https://blazor.radzen.com/) — UI components
- [Google OR-Tools](https://developers.google.com/optimization) — MILP solver
- [Entity Framework Core](https://learn.microsoft.com/ef/core/) + SQLite (WAL mode)
- [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/) — day-ahead prices
- [WeerLive](https://weerlive.nl/) — weather and radiation data
- [Enever](https://enever.nl/) — daily gas price

---

## License

See [LICENSE.txt](LICENSE.txt).
