# Changelog

All notable changes to the ENLU SDK for Unity are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[semantic versioning](https://semver.org/).

## [1.1.0] - 2026-11-11

### Added

- Economy on `EnluClient`:
  - Calls: `GetCatalogAsync(storeId)`, `GetCurrenciesAsync`, `GetInventoryAsync`, `PurchaseItemAsync`, `ConsumeItemAsync`, `ValidateReceiptAsync` and `GetAchievementsAsync`.
  - Typed models: `CatalogItem`, `Currency`, `Inventory`, `ItemInstance`, `PurchaseResult`, `ConsumeResult`, `ReceiptResult` and `AchievementProgress`.
  - Purchases with virtual currency are never retried after network errors (they are not idempotent). Receipt validation is retried, because the server deduplicates by transaction id.
- `EnluException` helpers for economy errors: `IsInsufficientFunds`, `IsPriceChanged` (with `NewPrice` from the details) and `IsLimitReached`.
- Economy on `EnluServerApi`:
  - Inventory and grants: `GetInventoryAsync`, `GrantAsync`, `AddCurrencyAsync`, `ConsumeAsync`, `RevokeAsync`, `TransferAsync` and `PurchaseForPlayerAsync`.
  - Achievements and segments: `UnlockAchievementAsync`, `GetPlayerAchievementsAsync` and `GetPlayerSegmentsAsync`.
- `EnluStoreReceipt.Parse`: reads Unity IAP's unified receipt into the provider and receipt value ENLU expects (Google purchase token, Apple transaction id, fake store as `test`).
- Optional `Enlu.Purchasing` assembly, compiled only when Unity IAP 4.x (`com.unity.purchasing`) is installed. It provides `ValidateAsync(Product)` and `ValidateAndConfirmAsync(controller, product)`, which confirms a pending purchase only after ENLU validates it.
- Account window: an **Economy** section with currencies, a simple catalog item editor and a grant-to-player tester.
- Sample: **Economy**, with a wallet, a shop, purchases with error toasts, an inventory with consume, and achievements.
- Tests for the economy models, error helpers, request bodies, retry rules and receipt parsing.

### Fixed

- The package samples (**Client Quick Start**, **Dedicated Server** and **Economy**) are now included. Version 1.0.0 listed them in `package.json`, but the `Samples~` folder was left out of the repository, so *Import* in the Package Manager found nothing.

## [1.0.0] - 2026-11-08

First public release.

### Added

- `EnluClient`: the player API (`/client/v1/:app/…`) with async/await on `UnityWebRequest`
  (WebGL-safe, no threads): custom id, device id and email logins, registration, logout, profile,
  display name, data export, account deletion, player data (read, write, delete, public data of
  other players), title data, statistics, leaderboards (top and around me), events (single,
  batched and an offline queue with automatic flushing), cloud functions with typed results, and
  client-side match server requests.
- Session ticket handling: kept in memory, optionally persisted in PlayerPrefs or a pluggable
  secure store; never logged; cleared on HTTP 401 with a `SessionExpired` event.
- `EnluException` with `Code`, `HttpStatus`, `RetryAfter` and `Details`; retries with exponential
  backoff and jitter for 429, 503 and network errors on idempotent calls, honouring Retry-After.
- `EnluServerApi` for dedicated servers and trusted backends (API key from `ENLU_API_KEY`):
  allocations, player data with server authority, statistics, events, functions, leaderboards and
  title data.
- `EnluGameServer`: the game server SDK (GSDK) with its server lifecycle (`Start`,
  `ReadyForPlayers`, `OnAllocated`, `WaitForAllocationAsync`, `UpdateConnectedPlayers`,
  `OnHealthCheck`, `OnShutdown`, `Shutdown`), heartbeats on a background thread, events on the main
  thread, port and environment helpers, and a local mode for the Editor.
- `EnluJson`: a dependency-free JSON parser and serializer with typed conversion helpers and a
  `JsonUtility` fallback for `[Serializable]` classes.
- Developer sign-in from the Editor (**Tools > ENLU > Account**): OAuth 2.0 for native apps with
  PKCE through the system browser and a loopback redirect; access token in memory and
  `SessionState`, rotating refresh token in the OS credential store (Windows DPAPI, macOS Keychain,
  Linux Secret Service); single-flight refresh; reuse detection; sign-out with server-side revocation.
- Account window: account, plan and balance (by role), apps (create with a one-time server key,
  "Use this app" for `EnluSettings`), workloads with live instance counts, statistics, a title
  data editor, cloud script push and function runner, and a game-server fleet helper (create,
  update, scale).
- `EnluSettings` asset with an inspector, validation and a "Test connection" button;
  **Assets > Create > ENLU > Settings** and **Tools > ENLU > Settings** menus.
- Samples: client quick start, and a dedicated server with a Dockerfile and a workload spec.
- Edit mode tests for JSON, URL building, models, the client (fake transport), the GSDK, PKCE, the
  loopback callback, credential stores and the Editor sign-in session.
