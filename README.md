# ENLU SDK for Unity

**Accounts, cloud data, leaderboards, cloud functions and dedicated game servers for Unity, on your own ENLU WebService.**

[![Unity 2021.3+](https://img.shields.io/badge/Unity-2021.3%2B-000000?logo=unity&logoColor=white)](https://unity.com/releases/editor/archive)
[![UPM](https://img.shields.io/badge/UPM-com.enlu.sdk%201.0.0-2f74c0)](https://github.com/OlumideOsuntuyi/ENLU-Unity-SDK/releases)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE.md)

---

## Features

- **Player accounts**: device, custom id and email login; profiles and display names; data export and account deletion.
- **Cloud data**: per-player data (private, public, server-owned) and game-wide title data.
- **Statistics and leaderboards**: max/min/sum/last aggregation, periodic resets, top-N and around-me.
- **Events**: single, batched, and an offline queue that flushes itself.
- **Cloud functions**: call JavaScript handlers on the server with typed results.
- **Match servers**: request a server from the client, or allocate from your backend.
- **Dedicated server SDK (GSDK)**: a simple server lifecycle (`ReadyForPlayers`, `OnAllocated`, health and shutdown) for servers on ENLU fleets, with a local mode in the Editor.
- **Sign in from the Editor**: browser sign-in (OAuth + PKCE) and a window to manage apps, statistics, title data, scripts and fleets.
- **Runs everywhere**: `UnityWebRequest` and `async`/`await`, WebGL-safe, no third-party dependencies (no Newtonsoft).
- **Secure by default**: tickets and tokens are never logged, the OS credential store holds Editor tokens, and API keys stay out of client builds.

## Requirements

| | |
|---|---|
| Unity | **2021.3 LTS** or newer (tested on Unity 6) |
| Game clients | Windows, macOS, Linux, Android, iOS, **WebGL** |
| Dedicated servers | **Linux** (Dedicated Server build target, in a Docker image) |
| Editor sign-in | Windows (DPAPI), macOS (Keychain), Linux (Secret Service via `secret-tool`) |
| Server | An ENLU WebService control node, reachable over `https://` |
| API compatibility | .NET Standard 2.1 (default). See [Troubleshooting](#troubleshooting) for .NET Framework. |

## Installation

### Package Manager (Git URL)

**Window > Package Manager > + > Add package from git URL…**

Latest:

```
https://github.com/OlumideOsuntuyi/ENLU-Unity-SDK.git
```

A pinned release (recommended for production):

```
https://github.com/OlumideOsuntuyi/ENLU-Unity-SDK.git#v1.0.0
```

### `manifest.json`

```json
{
  "dependencies": {
    "com.enlu.sdk": "https://github.com/OlumideOsuntuyi/ENLU-Unity-SDK.git#v1.0.0"
  }
}
```

### Local folder or tarball

- **Folder:** clone the repository, then **Add package from disk…** and pick its `package.json`,
  or use `"com.enlu.sdk": "file:../ENLU-Unity-SDK"` in the manifest.
- **Tarball:** `npm pack` in the repository (or download a release `.tgz`), then **Add package from tarball…**.

## Quick start

1. **Tools > ENLU > Account**: sign in with your browser, create or pick an app, and click **Use this app**.
   This writes the server URL and app into `Assets/Resources/EnluSettings.asset`.
   (Prefer to do it by hand? **Tools > ENLU > Settings**, fill in *Base Url* and *App*, and press *Test connection*.)
2. Add this to any GameObject and press Play:

```csharp
using Enlu;
using UnityEngine;

public class Hello : MonoBehaviour
{
    async void Start()
    {
        var enlu = EnluClient.Default;
        if (!enlu.IsLoggedIn) await enlu.LoginWithDeviceIdAsync();
        await enlu.SetDataAsync("coins", 120);
        await enlu.UpdateStatisticAsync("score", 4200);
        foreach (var e in (await enlu.GetLeaderboardAsync("score")).Entries) Debug.Log(e);
    }
}
```

The `score` statistic must exist and be client-writable. Define it under **Statistics** in the
Account window. Every call throws `EnluException` on failure; see [Errors](#errors-and-retries).

## Sign in from the Editor

**Tools > ENLU > Account** (also reachable from the `EnluSettings` inspector) signs you in as a developer:

1. Click **Sign in with browser**. Your browser opens the ENLU dashboard; sign in and approve **Unity Editor**.
   Unity never sees your password.
2. The browser returns to a one-time listener on `127.0.0.1`, and Unity exchanges the code using PKCE.
3. You stay signed in across Editor restarts. The refresh token lives in the OS credential store
   (DPAPI, Keychain or Secret Service), never in your project.

Once signed in you can:

| Section | What you can do |
|---|---|
| Account | See your email, role, team, plan and (for owners and admins) the credit balance. |
| Apps | List apps; **create** one (its server API key is shown once, with a Copy button); **Use this app** to fill `EnluSettings`. |
| Workloads | See each workload with live instance counts (standby, allocated, running…). |
| Statistics | List and define statistics (aggregation, reset period, sort, client-writable). |
| Title data | Load, edit as JSON and save game-wide configuration (internal keys stay internal). |
| Cloud scripts | Push a `.js` file as the live revision; run any function with JSON arguments and see result, logs and time. |
| Dedicated server fleet | Create or update a `game-server` workload from an image tag, port and fleet size; scale it. |

Actions your team role does not allow show the server's message instead. Details and the security
model: [Documentation~/editor-sign-in.md](Documentation~/editor-sign-in.md).

## Client API

| `EnluClient` | Route (`/client/v1/:app/…`) |
|---|---|
| `LoginWithDeviceIdAsync()` | `POST login/custom` with `device:<id>` (a GUID in PlayerPrefs where no device id exists) |
| `LoginWithCustomIdAsync(id, createAccount)` | `POST login/custom` |
| `RegisterWithEmailAsync(email, password, displayName)` / `LoginWithEmailAsync` | `POST register` / `POST login/email` |
| `LogoutAsync()` | `POST logout` (the local session is always cleared) |
| `GetProfileAsync()` / `UpdateDisplayNameAsync(name)` | `GET` / `PATCH me` |
| `ExportMyDataAsync()` / `DeleteMyAccountAsync()` | `GET me/export` / `DELETE me` |
| `GetDataAsync(keys)` / `SetDataAsync(data, visibility)` | `GET` / `PUT data` (a null value deletes a key) |
| `GetPlayerPublicDataAsync(playerId)` | `GET players/:player/data` |
| `GetTitleDataAsync(keys)` | `GET title-data` |
| `UpdateStatisticsAsync(dict)` / `UpdateStatisticAsync(name, value)` | `POST statistics` |
| `GetLeaderboardAsync(name, top, aroundMe)` | `GET leaderboards/:name` |
| `WriteEventAsync` / `WriteEventsAsync` / `QueueEvent` + `FlushAsync` | `POST events` |
| `ExecuteFunctionAsync<T>(name, args)` | `POST functions/:fn` → `FunctionResult<T>` |
| `RequestSessionAsync(workload, regions, metadata)` | `POST sessions` → `SessionAllocation` |

Also: `IsLoggedIn`, `PlayerId`, `SessionExpiresAt`, `PendingEventCount`, and the events `LoggedIn`,
`LoggedOut` and `SessionExpired`. Every method accepts a `CancellationToken`.

Values can be dictionaries, lists, primitives, enums, `DateTime` or your own `[Serializable]`
classes (through `JsonUtility`):

```csharp
await enlu.SetDataAsync(new Dictionary<string, object> { ["save"] = mySave, ["level"] = 3 });
MySave save = (await enlu.GetDataAsync("save"))["save"].GetValue<MySave>();

var res = await enlu.ExecuteFunctionAsync<Reward>("grantDailyReward", new Dictionary<string, object> { ["day"] = 3 });
if (res.Ok) Debug.Log(res.Result.item); else Debug.LogWarning(res.Error);
```

### Errors and retries

```csharp
try { await enlu.GetProfileAsync(); }
catch (EnluException e) when (e.IsUnauthorized) { /* show login */ }
catch (EnluException e) when (e.IsNetworkError) { /* offline */ }
catch (EnluException e) { Debug.LogWarning($"{e.Code} (HTTP {e.HttpStatus}): {e.Message}"); }
```

The SDK retries with exponential backoff, honouring `Retry-After`:
- **429** on every call;
- **503** and network errors only on idempotent calls (reads, data writes, logins) and on match server requests.

Statistics, events, functions and registration never run twice.

## Dedicated servers (GSDK)

Your server process starts on an ENLU node with environment variables (`ENLU_GSDK_URL`,
`ENLU_PORT_<NAME>`, …). `EnluGameServer` heartbeats to the node agent every second, and raises
events on the main thread:

```
Start() → Initializing → ReadyForPlayers() → StandingBy → (allocated) OnAllocated → Active
        → UpdateConnectedPlayers(...) → Shutdown()  (or OnShutdown when the platform stops it)
```

```csharp
using Enlu;
using UnityEngine;

public class ServerBoot : MonoBehaviour
{
    async void Awake()
    {
        EnluGameServer.OnHealthCheck += () => transportRunning;          // false → replaced
        EnluGameServer.OnShutdown += () => Debug.Log("Stopping");
        EnluGameServer.Start();

        StartTransport(EnluGameServer.GetContainerPort("game", 7777));   // 7777 in the Editor
        EnluGameServer.ReadyForPlayers();

        SessionInfo session = await EnluGameServer.WaitForAllocationAsync();
        LoadMap(session.GetMetadata("map", "default"));
    }

    void OnPlayerJoined(string id) { players.Add(id); EnluGameServer.UpdateConnectedPlayers(players); }
    void OnMatchOver() => EnluGameServer.Shutdown();                     // the fleet starts a fresh server
}
```

Players connect to `EnluGameServer.PublicAddress : GetPublicPort("game")`. That is the same
`address:port` that `SessionAllocation.GetEndpoint("game")` returns to clients. In the Editor
(no `ENLU_GSDK_URL`) the SDK runs in **local mode**: nothing goes over the network, and the server
is allocated to a `local-…` session two seconds after `ReadyForPlayers()`.

### Docker image

Build the **Linux Server** (Dedicated Server) target to `Build/LinuxServer/` with the executable
`GameServer.x86_64`, then use the Dockerfile from the *Dedicated Server* sample:

```dockerfile
FROM ubuntu:24.04
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
RUN useradd --create-home --uid 10001 game
WORKDIR /home/game/server
ARG EXECUTABLE=GameServer.x86_64
ENV EXECUTABLE=${EXECUTABLE}
COPY --chown=game:game Build/LinuxServer/ ./
RUN chmod +x "./${EXECUTABLE}"
USER game
EXPOSE 7777/udp
ENTRYPOINT ["/bin/sh", "-c", "exec \"./${EXECUTABLE}\" -batchmode -nographics -logFile -"]
```

```
docker build -t registry.example.com/my-game/arena:1.0.0 .
docker push registry.example.com/my-game/arena:1.0.0
```

### Fleet (`workload.json`)

```json
{
  "name": "arena",
  "spec": {
    "kind": "game-server",
    "runtime": "docker",
    "image": "registry.example.com/my-game/arena:1.0.0",
    "ports": [{ "name": "game", "container": 7777, "protocol": "udp", "public": true }],
    "resources": { "cpu": 1, "memoryMB": 1024 },
    "regions": ["eu-west", "us-east"],
    "fleet": { "standby": 2, "maxServers": 20 },
    "health": { "type": "gsdk", "graceSeconds": 60, "intervalSeconds": 10, "failuresBeforeRestart": 3 },
    "env": {
      "ENLU_API_URL": "https://api.example.com",
      "ENLU_API_KEY": "${secret:GAME_SERVER_API_KEY}"
    },
    "stopTimeoutSeconds": 30
  }
}
```

Deploy it with `enlu deploy my-game workload.json`, or with the **Dedicated server fleet** section of
the Account window. `fleet.standby` servers wait ready for matches, up to `fleet.maxServers` in total
(your plan may cap this). Try an allocation with `enlu allocate my-game arena --region eu-west`.

### Allocation flow

```
players ─► your matchmaker ─► EnluServerApi.AllocateAsync(sessionId = matchId) ─► control node
                    ◄─────────────── { sessionId, address, ports } ◄──── takes a StandingBy server
players ◄── address:port                                  server: OnAllocated(sessionId, metadata, players)
```

Allocation is **idempotent per session id**, so a retry after a timeout returns the same server. A
503 `unavailable` means no standby server is free yet; the SDK retries and honours `Retry-After`. Games
without a backend can enable client allocations for the app and call
`EnluClient.RequestSessionAsync` instead.

## Server API

`EnluServerApi` calls `/v1/apps/:app/…` with an API key, for **dedicated servers and backends you control**:

| Method | Purpose |
|---|---|
| `AllocateAsync`, `GetAllocationAsync`, `ListAllocationsAsync`, `EndSessionAsync` | Match servers |
| `GetPlayerAsync`, `SetPlayerDataAsync(playerId, data, access, visibility)` | Server-authoritative player data (`ReadOnly`, `Internal`) |
| `UpdatePlayerStatisticsAsync`, `GetLeaderboardAsync` | Statistics that clients cannot fake |
| `GetTitleDataAsync`, `WriteEventAsync`, `ExecuteFunctionAsync<T>(…, playerId)` | Config, analytics, cloud functions with logs |

```csharp
var api = new EnluServerApi(settings);   // key from the ENLU_API_KEY environment variable
var alloc = await api.AllocateAsync(new AllocationRequest { Workload = "arena", SessionId = matchId, Regions = { "eu-west" } });
```

> **Never ship an API key in a client build.** Anything inside a game can be extracted, and an API
> key has full server access to your app. Give it to servers through an environment variable mapped
> from an ENLU secret, with only the scopes it needs. `EnluServerApi` warns if it runs in a
> non-server player build.

## Samples

In **Window > Package Manager**, select **ENLU SDK**, open the **Samples** tab and click **Import**:

| Sample | Contents |
|---|---|
| Client Quick Start | A MonoBehaviour that logs in, saves and loads data, submits a score, shows the leaderboard, calls a cloud function, queues events and requests a match server, with an on-screen log. |
| Dedicated Server | A GSDK server lifecycle component, the Dockerfile and `workload.json`. |

## WebGL notes

- The client needs no threads: `UnityWebRequest` plus frame-based retry delays.
- The control node sends CORS headers for `/client/v1/…`, which is all a browser game needs.
  `EnluServerApi` and `EnluGameServer` are not for WebGL (the GSDK runs in local mode there).
- Serve the game over `https://`; browsers block `http://` API calls from `https://` pages.
- WebGL has no device id, so device logins use a random id stored in PlayerPrefs (IndexedDB).
  Clearing site data creates a new guest; offer email registration.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `No ENLU settings found` | Use **Use this app** in the Account window, or **Tools > ENLU > Settings**. |
| `bad_response` | The base URL is not an ENLU control node (a web page, a proxy). Use **Test connection**. |
| 404 "App not found." | Wrong app name or id. |
| 403 on `UpdateStatisticAsync` | The statistic is undefined or not client-writable. |
| 403 on `RequestSessionAsync` | Client allocations are off for the app; allocate from your backend. |
| 503 `unavailable` on allocation | No standby server yet: raise `fleet.standby`, and check that servers call `ReadyForPlayers()`. |
| Servers restart in a loop | Call `EnluGameServer.Start()` early, and raise `health.graceSeconds`. |
| "Insecure connection not allowed" | Unity 2022+ blocks plain HTTP. Use `https://`, or for a local server set **Player > Allow downloads over HTTP**. |
| Editor sign-in: the browser shows "Sign-in failed" | The link belonged to an older attempt. Click **Sign in** again. |
| Editor sign-in: "signed out … an old token was used" | A copied refresh token was reused, so the server ended the sign-in for safety. Sign in again. |
| Editor sign-in does not survive a restart (Linux) | Install `secret-tool` (package `libsecret-tools`) and a Secret Service such as GNOME Keyring. |
| `System.Net.Http` missing | Set Api Compatibility Level to .NET Standard 2.1, or add `-r:System.Net.Http.dll` to `Assets/csc.rsp`. |

## Security

- **API keys** never go in client builds, `EnluSettings`, scenes or source control. Use environment
  variables on your servers.
- **Player session tickets** stay in memory and, if you enable it, in PlayerPrefs or your own
  `IEnluKeyValueStore` (Keychain or Keystore). They are never logged, never put in URLs, and cleared on 401.
- **Editor tokens**: the access token lives in memory and `SessionState`. The refresh token lives in
  the OS credential store, rotates on every use, and the sign-in ends everywhere if an old one reappears.
- **Server authority**: client-writable data and statistics can be set to anything by a modified
  client. Use server-written statistics, read-only data and cloud functions for anything valuable.
- **Transport**: use `https://`. The Editor warns about plain `http://` except for localhost.
- Report security issues privately to the maintainers; do not open a public issue.

## Documentation

- [Documentation~/index.md](Documentation~/index.md): overview of assemblies, menus and tests.
- [Documentation~/editor-sign-in.md](Documentation~/editor-sign-in.md): how Editor sign-in works.
- [ENLU WebService documentation](https://github.com/OlumideOsuntuyi/ENLU-Webservice/tree/main/docs) (private repository; ask the owner for access).
- [Changelog](CHANGELOG.md).

## Support and contributing

- **Questions and bugs:** open an issue with your Unity version, platform, SDK version and the
  `EnluException` code and message. Never paste tokens, tickets or API keys.
- **Pull requests** are welcome. Keep changes focused, add or update EditMode tests (they must run
  without a server), keep the package free of third-party dependencies, and add an entry to
  `CHANGELOG.md`.

## License

Apache License 2.0, see [LICENSE.md](LICENSE.md) and [NOTICE](NOTICE).
