# MultiTeklaStructuresMonitor

A reference sample that shows how a single, **version‑agnostic** driver executable can talk to
**several versions of Tekla Structures at the same time** — without recompiling the driver against
each version's Tekla `.NET` NuGet packages.

The trick is *not* multi‑targeting. One driver binary is built once, then **deployed per version and
its `.exe.config` is patched at runtime** so the CLR loads that version's Tekla assemblies from the
correct install folder (GAC for older versions, code‑base / GAC‑less for 2024+).

This is the pattern an external tool — e.g. a Revit add‑in, a desktop launcher, or a CI agent — would
use to drive whatever Tekla Structures versions happen to be installed on a machine.

---

## Table of contents

- [How it works](#how-it-works)
- [Repository layout](#repository-layout)
- [The two projects](#the-two-projects)
- [The gRPC contract](#the-grpc-contract)
- [Prerequisites](#prerequisites)
- [Build](#build)
- [Run](#run)
- [Walkthrough: what happens on startup](#walkthrough-what-happens-on-startup)
- [Driver CLI reference](#driver-cli-reference)
- [Extending the API (worked example)](#extending-the-api-worked-example)
- [App.config patching: GAC vs GAC‑less](#appconfig-patching-gac-vs-gac-less)
- [Troubleshooting](#troubleshooting)
- [Limitations & notes](#limitations--notes)

---

## How it works

```
                          MultiTeklaStructuresMonitor (net8.0, Blazor Server)
                          ─────────────────────────────────────────────────
                          1. Read registry → find installed Tekla versions
                          2. Deploy + patch a driver copy per version
                          3. Start each driver on a free port
                          4. Open a gRPC client per driver
                                        │
        ┌───────────────────────────────┼───────────────────────────────┐
        │ gRPC (HTTP/2, localhost:portA) │ ...:portB                     │ ...:portC
        ▼                                ▼                                ▼
 TeklaGrpcApiService.exe          TeklaGrpcApiService.exe          TeklaGrpcApiService.exe
 (copy patched for 2022.0)        (copy patched for 2023.0)        (copy patched for 2026.0)
        │                                │                                │
        ▼                                ▼                                ▼
 Tekla.Structures.Model           Tekla.Structures.Model           Tekla.Structures.Model
 → Tekla Structures 2022          → Tekla Structures 2023          → Tekla Structures 2026
```

The **same** compiled `TeklaGrpcApiService.exe` runs three times above; only its `.exe.config`
differs per copy, redirecting assembly resolution to each version's `bin` folder.

---

## Repository layout

```
MultiTeklaStructuresMonitor.sln
│
├── Proto/
│   └── TeklaGrpcApiService.proto        # Shared gRPC contract (Server + Client codegen)
│
├── TeklaGrpcApiService/                 # The driver / gRPC server  (net48, Exe)
│   ├── Program.cs                       # CLI parsing + gRPC server host
│   ├── Options.cs                       # -p / -l / -t command‑line options
│   └── Services/
│       └── TeklaGrpcApiService.cs       # RPC implementations (Ping, GetOpenModel, StopServer)
│
├── MultiTeklaStructuresMonitor/         # The monitor / host UI  (net8.0, Blazor Server)
│   ├── Program.cs                       # Web host; builds the GrpcServiceSink singleton
│   ├── Helpers/
│   │   └── RegistryHelpers.cs           # Discover installed Tekla versions from the registry
│   ├── Interop/
│   │   ├── DriverStarter.cs             # Deploy, patch, start drivers; manage pid/port
│   │   ├── GrpcServiceSink.cs           # Owns all driver clients; fan‑out queries
│   │   ├── GrpcServiceClient.cs         # Thin gRPC client wrapper
│   │   └── InstallDirData.cs            # (TSVersionDir, MainDir, ProductVersion)
│   └── Components/Pages/
│       ├── Home.razor                   # Landing page
│       └── Counter.razor                # /modelinformation — query & restart drivers
│
└── BuildDrop/                           # Build output (driver is copied into the monitor output)
```

---

## The two projects

### `TeklaGrpcApiService` — the driver (gRPC **server**)

- Targets **`net48`** because it loads the classic‑framework `Tekla.Structures.Model` API.
- Built **once** against `Tekla.Structures.Model` `2021.0.0`; the assembly *binding* is fixed up later
  per version by config patching, not by rebuilding.
- A console `Exe` that hosts a `Grpc.Core` server on `localhost:<port>` and logs to a file.
- Generates **server** stubs from `Proto/TeklaGrpcApiService.proto`.

### `MultiTeklaStructuresMonitor` — the monitor (gRPC **client** + UI)

- Targets **`net8.0`**, an ASP.NET Core **Blazor Server** app.
- Generates **client** stubs from the same `.proto`.
- Copies the driver's `net48` build output into its own output folder (see the `<None Include="..\BuildDrop\TeklaGrpcApiService\net48\**">` item), so the driver travels with the monitor.
- On startup it constructs a single `GrpcServiceSink`, which discovers versions, starts drivers, and
  exposes `GetAllOpenModels()` / `RestartDrivers()` to the UI.

> **Build order matters:** the monitor consumes the driver's output, so the driver must build first.
> The solution already declares this dependency, so building the `.sln` does the right thing.

---

## The gRPC contract

`Proto/TeklaGrpcApiService.proto` is intentionally tiny:

```proto
syntax = "proto3";
option csharp_namespace = "TeklaService";

service TeklaServiceApi {
  rpc Ping        (StringRequest) returns (StringReply);
  rpc GetOpenModel(StringRequest) returns (StringReply);
  rpc StopServer  (StringRequest) returns (StringReply);
}

message StringRequest { string name    = 1; }
message StringReply   { string message = 1; }
```

| RPC            | Purpose                                                                 |
|----------------|------------------------------------------------------------------------|
| `Ping`         | Liveness probe. Replies with `Hello, <name> im node: <pid>` — also used to verify the right process answered. |
| `GetOpenModel` | Returns the currently open model as `"<ModelName> : <ModelPath>"`, or a "not running" message. |
| `StopServer`   | Gracefully shuts the gRPC server down.                                  |

The driver project compiles this with `GrpcServices="Server"`; the monitor with `GrpcServices="Client"`.

---

## Prerequisites

- **Windows** (the sample reads the Windows registry and patches Windows `.exe.config` files).
- **.NET 8 SDK** (monitor) and the **.NET Framework 4.8 developer pack** (driver).
- **Visual Studio 2022** (17.10+) or the `dotnet` CLI / MSBuild.
- One or more installed **Tekla Structures** versions (2021+). Without any, the monitor runs but finds
  no drivers to start.

---

## Build

From Visual Studio: open `MultiTeklaStructuresMonitor.sln` and build the solution (driver builds first).

From the command line:

```powershell
# Build everything (recommended — respects the project dependency order)
dotnet build MultiTeklaStructuresMonitor.sln -c Debug
```

Outputs land under `BuildDrop/`:

- `BuildDrop/TeklaGrpcApiService/net48/` — the driver binary the monitor will deploy.
- `BuildDrop/MultiTeklaStructuresMonitor/net8.0/` — the monitor, with the driver copied into a
  `TeklaGrpcApiService/` subfolder.

> The driver's `<TeklaVersion>` property defaults to `2026.0 Daily` in **Debug** — adjust if your dev
> box uses a different daily/version.

---

## Run

```powershell
dotnet run --project MultiTeklaStructuresMonitor/MultiTeklaStructuresMonitor.csproj
```

Then open the URL the console prints (e.g. `https://localhost:5001`) and:

1. Go to **Model Information** (`/modelinformation`).
2. Click **Get All Models** — each running Tekla reports its open model (or "not running").
3. Click **Restart Drivers** — stops every driver, re‑discovers versions, and starts them again.

For the most useful demo, have **two or more** Tekla Structures versions open with different models
before clicking *Get All Models*.

---

## Walkthrough: what happens on startup

The interesting code path, end to end:

**1. The monitor builds the sink once, as a singleton** (`MultiTeklaStructuresMonitor/Program.cs`):

```csharp
var driversSink = GrpcServiceSink.CreateInstance(logger);
builder.Services.AddSingleton(driversSink);
```

**2. The sink discovers installed versions from the registry**
(`GrpcServiceSink.CreateInstance` → `RegistryHelpers.ReadInstalledApplications`). It enumerates
`HKLM\SOFTWARE\Trimble\Tekla Structures\<version>\setup` and reads `MainDir`, `TSVersionDir`, and
`ProductVersion` into `InstallDirData`.

**3. For each version, the sink starts a driver** (`DriverStarter.StartDriverAndServer`):

- If a `pid.txt` exists *and* that process is still alive → reuse it (return a client).
- Otherwise: create a per‑version deployment folder, **copy** the base driver into it,
  **patch** its `.exe.config` for that version, pick a **free TCP port**, launch the `.exe` with
  `-p <port> -l <log>`, and persist `"<pid>:<port>"` to `pid.txt`.
- A `Ping("Server")` call confirms the *expected* process answered before the client is accepted.

**4. The UI fans out across all clients** (`GrpcServiceSink.GetAllOpenModels`):

```csharp
foreach (var client in clients)
{
    var reply = client.GetOpenModel("GetOpenModel");
    openModels.Add($"Client: {client.InstallData.TSVersionDir} : " +
                   $"{client.InstallData.ProductVersion} : Status: {reply}");
}
```

---

## Driver CLI reference

`TeklaGrpcApiService.exe` (parsed by `CommandLineParser` in `Options.cs`):

| Flag | Name             | Description                                                            |
|------|------------------|-----------------------------------------------------------------------|
| `-p` | Port             | TCP port for the gRPC server to listen on (`localhost`).              |
| `-l` | Log file         | Path to the trace log file (recreated on each start).                 |
| `-t` | Test connection  | Probe the Tekla API once, print the open model, and exit (`0`/`1`).   |

Run a driver standalone (handy for debugging a single version's binding):

```powershell
# Verify this patched copy can reach a running Tekla Structures, then exit
TeklaGrpcApiService.exe -t -l ".\probe-log.txt"

# Host the gRPC server on port 50111
TeklaGrpcApiService.exe -p 50111 -l ".\50111-log.txt"
```

> `SESSIONNAME` is forced to `Console` when the monitor launches a driver, so the driver's session
> matches a Tekla Structures started from the Start menu. Adjust in `DriverStarter` if you use exotic
> sessions.

---

## Extending the API (worked example)

Adding a capability is three steps. Here's a complete example that returns the number of selected
objects in the open model.

**1. Declare the RPC in `Proto/TeklaGrpcApiService.proto`:**

```proto
service TeklaServiceApi {
  rpc Ping            (StringRequest) returns (StringReply);
  rpc GetOpenModel    (StringRequest) returns (StringReply);
  rpc StopServer      (StringRequest) returns (StringReply);
  rpc GetSelectedCount (StringRequest) returns (StringReply);   // <-- new
}
```

**2. Implement it on the server** (`TeklaGrpcApiService/Services/TeklaGrpcApiService.cs`):

```csharp
public override Task<StringReply> GetSelectedCount(StringRequest request, ServerCallContext context)
{
    try
    {
        var selector = new Tekla.Structures.Model.UI.ModelObjectSelector();
        var count = selector.GetSelectedObjects().GetSize();
        return Task.FromResult(new StringReply { Message = $"Selected objects: {count}" });
    }
    catch (Exception ex)
    {
        Trace.WriteLine($"GetSelectedCount failed: {ex.Message}");
        return Task.FromResult(new StringReply { Message = "Tekla Structures not running" });
    }
}
```

**3. Call it from the client wrapper** (`MultiTeklaStructuresMonitor/Interop/GrpcServiceClient.cs`):

```csharp
public string GetSelectedCount(string name)
{
    var reply = client.GetSelectedCount(new StringRequest { Name = name });
    return reply.Message;
}
```

Rebuild — `Grpc.Tools` regenerates the server and client stubs from the proto automatically. Then
surface the new call from `GrpcServiceSink` and a Blazor page just like `GetAllOpenModels`.

---

## App.config patching: GAC vs GAC‑less

`DriverStarter.DeployDriverToVersionSpecifFolders` (using `TsPatchHelpers` from the
`Tekla.AppRedirect.Helpers` / `TSAppConfigPatcherTask` packages) rewrites the driver copy's
`.exe.config` so it binds to the *target version's* Tekla assemblies:

```csharp
var version = Version.Parse(dirData.ProductVersion);

if (version.Major < 224)   // pre‑2024: assemblies resolved via the GAC
    TsPatchHelpers.PatchExeFileUsingBinFolderUsingGac(driverDeploymentExePath, binDir);
else                       // 2024+: GAC‑less, resolved via <codeBase> to the bin folder
    TsPatchHelpers.PatchExeFileUsingBinFolder(driverDeploymentExePath, binDir);
```

The config is re‑applied on **every** startup (the old `.config` is deleted and recreated) so a
user‑corrupted config self‑heals. This is the core idea that lets one binary serve many versions.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| "No Tekla Structures are installed in the machine" | The registry key `HKLM\SOFTWARE\Trimble\Tekla Structures` is missing, or you're on a non‑Windows OS. |
| A version is skipped silently | `TeklaStructures.exe.config` wasn't found in that version's `bin` (`DeployDriverToVersionSpecifFolders` returns `false`). |
| Driver starts but `GetOpenModel` says "not running" | That Tekla version isn't open, or the session/config binding is wrong — run the driver with `-t` and read its log. |
| Stale driver after upgrading | Delete the per‑version deployment folder (and its `pid.txt`) under the monitor output, then **Restart Drivers**. |
| Port conflicts | Ports are chosen dynamically via a free‑port probe; a stale `pid.txt` pointing at a dead process is re‑used safely (liveness is checked). |

Each driver writes a `\<port\>-log.txt` (via `Trace`) next to its deployment — start there.

---

## Limitations & notes

- **Windows‑only** by design (registry + `.exe.config` patching).
- gRPC uses **insecure** localhost credentials — this is a local‑machine sample, not a network service.
- `clients` in `GrpcServiceSink` is `static`; the sample assumes a single sink for the app's lifetime.
- This is a **demonstration of a technique**, not a production service — error handling and lifecycle
  management are deliberately minimal so the mechanism stays readable.
