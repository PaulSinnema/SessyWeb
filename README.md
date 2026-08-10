# SessyWeb

[![Publish Docker image](https://github.com/PaulSinnema/SessyWeb/actions/workflows/docker-publish.yml/badge.svg)](https://github.com/PaulSinnema/SessyWeb/actions/workflows/docker-publish.yml)

**SessyWeb** is an open-source home energy management system (HEMS) for households with one or more [Sessy](https://www.sessy.nl) home batteries. It runs as a Docker container on a NAS, mini-PC or Raspberry Pi and plans battery charging and discharging against dynamic day-ahead electricity prices, your solar forecast and your household consumption.

> ⚡ Charge cheap. Sell expensive. Let the sun do the rest.

Everything runs locally. The database is a SQLite file on your own disk; the only outbound calls are for prices and weather.

What changed per version is in [CHANGELOG.md](CHANGELOG.md).

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
  - [Step 6 — Start the container](#step-6--start-the-container)
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
| SolarEdge solar inverter | Optional | Only if you have solar panels. **SolarEdge over Modbus TCP is the only inverter that is actually supported.** |

**Accounts (all free)**

| | Required? | What it is for |
|---|---|---|
| [WeerLive](https://weerlive.nl/delen.php) API key | Yes | Solar radiation and temperature forecast |
| [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/) token | Recommended | Fallback source for day-ahead prices |
| [Enever](https://enever.nl/token-aanmaken/) token | Optional | Daily gas price, used only for heat-pump savings figures |

**Software**

- Docker, or Docker Desktop, or Synology Container Manager. Nothing else — there is a ready-made image, so you need neither .NET nor the source code to run SessyWeb.
- **No Docker Hub account.** The image lives in GitHub's own registry and is public, so pulling it needs no login at all. "Docker" the software and "Docker Hub" the website are two different things; you only need the first.
- Memory to spare for the container. The example below gives it 4 GB; day to day it uses far less, but a NAS that hands out 512 MB will make it struggle.

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

If you have a SolarEdge inverter, also note its IP address. Modbus TCP must be switched on in the inverter itself (SetApp → Site Communication → Modbus TCP); it listens on port `1502`.

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

  // SolarEdge inverter over Modbus TCP. Remove this whole section if you have
  // no solar panels. The outer key must be exactly "SolarEdge"; the inner key
  // ("1") is the inverter number, add "2" for a second inverter.
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
- **`Interface`, `Port`, `SlaveId`** stay as shown.
- `//` comments are allowed in these files; the loader ignores them.

> [!NOTE]
> **Only SolarEdge is supported.** The key under `PowerSystems:Endpoints` must be spelled exactly `SolarEdge`, or the inverter is silently skipped.
>
> The source also contains skeleton drivers named `Sma`, `Enphase`, `Victron`, `Huawei`, `Sungrow`, `Solis` and `GoodWe`. They are thin subclasses of a generic SunSpec reader, have never been tested against real hardware, and are not supported. Use them only if you are prepared to debug them yourself — see [For developers](#for-developers).

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

### Step 6 — Start the container

A ready-made image is published on every change, so there is nothing to build and you do not need the source code. It is public, so no `docker login` is required anywhere in this guide.

Create a file called **`docker-compose.yml`** next to the two folders you made in Step 3:

```yaml
services:
  sessyweb:
    image: ghcr.io/paulsinnema/sessyweb:latest
    # Always check the registry on start. Without this, Docker sees that it
    # already has something tagged "latest" and keeps running the old version.
    pull_policy: always
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

Then start it:

```bash
docker compose up -d
```

The first start downloads the image, which takes a minute or two. Watch it come up with:

```bash
docker compose logs -f
```

Images are published for both `linux/amd64` and `linux/arm64`, so the same line works on an Intel NAS, an ARM NAS and a Raspberry Pi. Docker picks the right one for your machine by itself; to see what is on offer, run `docker buildx imagetools inspect ghcr.io/paulsinnema/sessyweb:latest`.

**Updating later** is two commands, and your configuration and database are untouched because they live in the mounted folders:

```bash
docker compose pull
docker compose up -d
```

`pull_policy: always` above makes a plain `docker compose up -d` do the same thing, which matters most in a GUI like Synology's Container Manager where there is no separate pull button.

To pin a specific version instead of following `latest`, replace the tag with a version number, for example `ghcr.io/paulsinnema/sessyweb:v1.0.61`. Every published version is listed on the [package page](https://github.com/PaulSinnema/SessyWeb/pkgs/container/sessyweb).

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

Use **Container Manager → Project**, not the Registry tab. The Registry tab searches Docker Hub and cannot find an image on ghcr.io; a project simply takes the image address, so this is both easier and less error-prone.

1. Create the two folders on the NAS, for example `/volume1/SessyController/Config` and `/volume1/SessyController/Data`, and put your `appsettings.json` and `secrets.json` in the Config one.
2. **Container Manager → Project → Create**. Give it the name `sessyweb`, pick a path, and choose *Create docker-compose.yml*.
3. Paste this:

```yaml
services:
  sessyweb:
    image: ghcr.io/paulsinnema/sessyweb:latest
    pull_policy: always
    container_name: sessyweb
    restart: unless-stopped
    ports:
      - "8101:80"
    volumes:
      - /volume1/SessyController/Config:/SessyController/Config
      - /volume1/SessyController/Data:/SessyController/Data
    environment:
      - ASPNETCORE_URLS=http://+:80
      - ASPNETCORE_HTTP_PORTS=80
      - CONFIG_PATH=/SessyController/Config
    mem_limit: 4g
```

4. Build the project. Container Manager pulls the image and starts it.

To update later, open the project and press **Build** again. Your database and configuration are on the mounted folders, so they survive every update.

> [!IMPORTANT]
> **`pull_policy: always` is what makes that Build an actual update.** Without it, Docker sees a local image already tagged `latest`, reuses it, and you keep running the old version — Build and Clean both succeed while nothing changes. Check the version in the bottom-left corner of the UI after updating; if it did not move, the old image was reused.
>
> On an older Compose that rejects `pull_policy`, either update over SSH with `docker compose pull && docker compose up -d`, or pin an explicit version tag such as `:v1.0.62` — a tag you do not have locally must be fetched.

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

- **Greedy arbitrage planner** — plans charge, discharge and zero-net-home windows over a 72-hour horizon on quarter-hour resolution, deterministically: the same inputs always give the same plan
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
| Solar inverters | SolarEdge over Modbus TCP — the only supported inverter, and the only one running in production |
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
| Inverter not found | The key under `PowerSystems:Endpoints` must be exactly `SolarEdge` (case-sensitive), the IP must be right, and Modbus TCP must be enabled in the inverter itself. |
| Battery does not react | On **Settings → Management Settings**, check **Charged in control** — if it is ticked, SessyWeb deliberately sends no commands. Refused writes are logged as warnings, so the log tells you which mode blocked them. |
| SOC deviation warnings | Normal. The planner corrects every quarter. |
| Container restarts or is killed | Out of memory — raise `mem_limit` (or the NAS memory limit) and check whether anything else on the machine is competing for RAM. |
| `denied` or `unauthorized` when pulling | You are pulling a tag that does not exist. The image itself is public and needs no login: check the spelling of `ghcr.io/paulsinnema/sessyweb` (all lowercase) and pick a tag from the [package page](https://github.com/PaulSinnema/SessyWeb/pkgs/container/sessyweb). |
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
| `SessyController` | Domain and background services: planner (`Services/Optimization/BatteryGreedyPlanner.cs` plus one class per strategy), hardware polling, state machine, inverter drivers |
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

### Building and publishing the image

Every push to `master` triggers `.github/workflows/docker-publish.yml`, which builds for `linux/amd64` and `linux/arm64` and pushes to `ghcr.io/paulsinnema/sessyweb`. The image tag is read straight from `SessyCommon/AppInfo.cs`, so bumping the version there is what names the release; `latest` moves along with it. A cold run takes about five minutes; after that the BuildKit cache makes it noticeably quicker.

Publishing therefore needs no manual step at all — no `docker build`, no `docker push`, no registry login. Some details that are easy to trip over if you fork this repository:

- **No secrets to configure.** The workflow signs in to ghcr.io with the token GitHub hands the job, which is created and discarded per run. There is no Docker Hub account involved anywhere.
- **Actions must be enabled.** A repository with Actions switched off lists the workflow as *active* but never starts a run, which looks exactly like a workflow that is being ignored. Check *Settings → Actions → General → Allow all actions*.
- **Run it by hand** from the workflow page — the `workflow_dispatch` trigger puts a **Run workflow** button there. Useful for the first run, or after fixing the workflow itself without wanting a new commit.
- **Package visibility is inherited from the repository.** Because this repo is public the image is public too, so no visibility switch has to be flipped after the first publish. Fork it privately and the image is private as well, and then anything pulling it must log in.

To build the same image locally:

```powershell
docker build -f SessyWeb/Dockerfile -t sessyweb:test .
```

The Dockerfile copies the five `.csproj` files before restoring, so that layer stays cached until a package or project reference changes. It cross-compiles rather than emulating: the SDK stage always runs on the host architecture and `dotnet publish -a $TARGETARCH` targets the other one, which is why an arm64 image costs almost nothing extra to produce.

### Adding another inverter brand

`SessyController/Services/InverterServices/` holds `SunspecInverterService`, a generic SunSpec Modbus reader, plus one small subclass per brand that does nothing but pass its provider name. Only `SolarEdgeInverterService` has brand-specific code and real-world mileage; the others are placeholders. Making one of them work means checking that brand's register map against the SunSpec base and overriding what differs. Pull requests welcome — say which inverter you tested against.

---

## How the planner works

Every minute (and on every price or settings change) SessyWeb rebuilds its picture of the next 72 hours:

1. **Gather** one record per quarter-hour: price, solar forecast, consumption forecast, mode and SOC.
2. **Plan** in two passes. First a baseline that simply covers the house from solar and battery where that is free. Then arbitrage: the planner repeatedly takes the single most profitable 0.1 kWh block it can still fit — buy cheap now and sell dear later, sell now and buy back cheaper later, or sell energy it already holds — and stops when no block is worth more than the round-trip loss plus the cycle wear it costs.
3. **Decide** the actual battery mode and inverter setpoint in the state machine, where curtailment can override the plan.
4. **Execute** one action per quarter through the Sessy local API.

The planner is deterministic and greedy rather than a general-purpose solver: it is fast enough to rerun every minute, and every euro in the plan can be traced back to the block that earned it.

The plan is only rebuilt when something material changed — a new price set, a SOC that drifted more than 20 % from plan, or a settings change.

> [!NOTE]
> A battery that never reaches 100 % is usually the planner being right, not wrong. Filling the battery costs money; it only pays if the energy can be sold higher later. Judge SessyWeb on the **Financial results** page, not on the SOC.

---

## Tech stack

- [.NET 10 / ASP.NET Core](https://dotnet.microsoft.com/) — Blazor Server
- [Radzen Blazor](https://blazor.radzen.com/) — UI components
- [Entity Framework Core](https://learn.microsoft.com/ef/core/) + SQLite (WAL mode)
- [NModbus](https://github.com/NModbus/NModbus) — inverter communication
- [NodaTime](https://nodatime.org/) and [SolCalc](https://github.com/Yeah69/SolCalc) — time zones and sun position
- [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/) — day-ahead prices
- [WeerLive](https://weerlive.nl/) — weather and radiation data
- [Enever](https://enever.nl/) — daily gas price

---

## License

See [LICENSE.txt](LICENSE.txt).
