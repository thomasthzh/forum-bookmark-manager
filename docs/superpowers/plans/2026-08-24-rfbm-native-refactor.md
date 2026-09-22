# R-fbm Native Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing native probe into a tested, recoverable Windows forum bookmark manager while preserving legacy data semantics and user-owned fields.

**Architecture:** A WPF shell depends on a deep session-based Collection module. Core owns pure Discuz parsing and normalized domain values; Data owns versioned SQLite and atomic state transitions; BrowserBridge owns the constrained loopback/extension boundary. Playwright and Edge MV3 are explicit acquisition adapters. Tests use fixtures, temporary SQLite, and local loopback servers.

**Tech Stack:** .NET 8, C# 12, WPF, Microsoft.Data.Sqlite 8, AngleSharp 1.7, Microsoft.Playwright 1.62, Edge Manifest V3, PowerShell packaging.

---

## Task 1: Establish rich domain and Discuz parsing

**Files:**
- Modify: `src/Rfbm.Core/Rfbm.Core.csproj`
- Replace: `src/Rfbm.Core/Models/BookmarkPost.cs`
- Create: `src/Rfbm.Core/Models/PostImage.cs`
- Create: `src/Rfbm.Core/Models/ResourceLink.cs`
- Create: `src/Rfbm.Core/Models/ThreadKey.cs`
- Create: `src/Rfbm.Core/Parsing/DiscuzPageParser.cs`
- Create: `src/Rfbm.Core/Parsing/Observed.cs`
- Create: `src/Rfbm.Core/Parsing/PageValidation.cs`
- Modify: `tests/Rfbm.Core.Tests/Program.cs`
- Create: `tests/Rfbm.Core.Tests/Fixtures/favorites-page-1.html`
- Create: `tests/Rfbm.Core.Tests/Fixtures/thread-page.html`
- Create: `tests/Rfbm.Core.Tests/Fixtures/login-page.html`

- [ ] Add failing console assertions for query and pretty-URL thread identity, mirror dedupe, favorite pagination, title/count/password/link context, nine-image cap, and login-page rejection.
- [ ] Run `dotnet run --project tests/Rfbm.Core.Tests/Rfbm.Core.Tests.csproj` and record the expected failure.
- [ ] Implement immutable domain values, `Observed<T>` field state, snapshot validation, favorite/detail parsers and normalized favorite time.
- [ ] Re-run the Core runner and build until green with zero warnings.

## Task 2: Replace probe storage with versioned SQLite

**Files:**
- Replace: `src/Rfbm.Data/Storage/BookmarkDatabase.cs`
- Replace: `src/Rfbm.Data/Storage/PostRepository.cs`
- Create: `src/Rfbm.Data/Storage/SchemaMigrator.cs`
- Create: `src/Rfbm.Data/Storage/PostQuery.cs`
- Create: `src/Rfbm.Data/Storage/CollectionRunStore.cs`
- Modify: `tests/Rfbm.Data.Tests/Program.cs`

- [ ] Add failing integration assertions for WAL/migrations, stable `(site_family, thread_id)` upsert, manual-field preservation, atomic child replacement, SQL filtering/paging, three-state bulk update, soft delete/restore and one-active-run enforcement.
- [ ] Run the Data runner and capture red.
- [ ] Implement schema migrations and short transactions. Keep compatibility columns needed for importing the legacy Python database; never mutate the source file during import.
- [ ] Re-run Data tests, including a rollback assertion that proves invalid snapshots cannot erase good child rows.

## Task 3: Secure extension-to-app boundary

**Files:**
- Replace: `src/Rfbm.BrowserBridge/Contracts/PageSnapshot.cs`
- Create: `src/Rfbm.BrowserBridge/Loopback/SnapshotReceiver.cs`
- Create: `src/Rfbm.BrowserBridge/Contracts/SelectorProfileMessage.cs`
- Modify: `extension/edge/manifest.json`
- Replace: `extension/edge/background.js`
- Replace: `extension/edge/content.js`
- Replace: `extension/edge/overlay.css`
- Modify: `tests/Rfbm.BrowserBridge.Tests/Program.cs`

- [ ] Add failing loopback tests for a valid full-document snapshot and rejection of wrong method/path/Host/Origin/nonce/content type and oversized payload.
- [ ] Run the BrowserBridge runner and capture red.
- [ ] Implement a cancellation-safe loopback receiver with random per-session nonce, bounded JSON parsing, no cookie/header persistence and explicit status replies.
- [ ] Implement MV3 manual snapshot and selector annotation overlay; never read cookies or authorization data.
- [ ] Re-run BrowserBridge tests plus `node --check` for both JavaScript files.

## Task 4: Build the resumable Collection deep module

**Files:**
- Create: `src/Rfbm.Collection/Rfbm.Collection.csproj`
- Create: `src/Rfbm.Collection/Contracts/CollectionContracts.cs`
- Create: `src/Rfbm.Collection/Engine/CollectionEngine.cs`
- Create: `src/Rfbm.Collection/Engine/PaginationFrontier.cs`
- Create: `src/Rfbm.Collection/Acquisition/IPageAcquisitionAdapter.cs`
- Create: `src/Rfbm.Collection/Media/AtomicImageStore.cs`
- Create: `tests/Rfbm.Collection.Tests/Rfbm.Collection.Tests.csproj`
- Create: `tests/Rfbm.Collection.Tests/Program.cs`
- Modify: `R-fbm.sln`

- [ ] Add failing fixture-based tests for single active run, event ordering, declared and link-discovered pagination closure, mirror page dedupe, per-item failure continuation, partial result, explicit cancellation and resume.
- [ ] Run the Collection runner and capture red.
- [ ] Implement `OpenAsync`/`FindResumeCandidateAsync`, explicit session cancellation, durable frontier/work items, bounded channels and atomic commit calls.
- [ ] Implement image download limits, MIME checks, temporary write plus atomic move. Resource links must never enter this downloader.
- [ ] Re-run tests and prove observer cancellation/disposal does not cancel the collection.

## Task 5: Add Playwright Edge and optional Mihomo adapters

**Files:**
- Create: `src/Rfbm.Collection/Acquisition/PlaywrightEdgeAcquisitionAdapter.cs`
- Create: `src/Rfbm.Collection/Network/MihomoNetworkRecoveryAdapter.cs`
- Create: `tests/Rfbm.Collection.Tests/Fixtures/fixture-server.cs`
- Modify: `src/Rfbm.Collection/Rfbm.Collection.csproj`
- Modify: `tests/Rfbm.Collection.Tests/Program.cs`

- [ ] Add a local HTTP fixture integration test for visible-verification state, headless snapshot acquisition, redirect/origin validation, transient mirror retry and honest final state.
- [ ] Add Microsoft.Playwright 1.62 and implement a dedicated browser profile outside the repository. Never use the user's default browser profile.
- [ ] Return `WaitingForUser` on login/challenge pages and expose a visible Edge verification window; do not automate challenge interaction.
- [ ] Add an opt-in Mihomo REST adapter that runs only after transient failures across configured origins.
- [ ] Run tests. Keep real-site login as manual acceptance, not automated CI.

## Task 6: Turn the WPF probe into the product shell

**Files:**
- Replace: `src/Rfbm.Native/MainWindow.xaml`
- Replace: `src/Rfbm.Native/MainWindow.xaml.cs`
- Replace: `src/Rfbm.Native/ViewModels/MainWindowViewModel.cs`
- Replace: `src/Rfbm.Native/ViewModels/PostListItem.cs`
- Create: `src/Rfbm.Native/ViewModels/Commands.cs`
- Create: `src/Rfbm.Native/Styles/Theme.xaml`
- Modify: `src/Rfbm.Native/App.xaml`
- Modify: `src/Rfbm.Native/Rfbm.Native.csproj`
- Modify: `tests/Rfbm.Native.Tests/Program.cs`

- [ ] Add failing view-model assertions for empty first run, debounced SQL search, combined filters, selection/bulk status, soft delete/restore, star/note/tag persistence, random rediscovery and collection progress mapping.
- [ ] Implement the ink/amber Chinese desktop layout with top actions, smart-filter sidebar, virtualized list, detail panel and persistent bottom progress surface.
- [ ] Remove automatic fake-data insertion. Wire application-scoped database, receiver and Collection engine lifetimes.
- [ ] Re-run Native tests and launch the real executable for visual/accessibility inspection.

## Task 7: Legacy import, packaging and privacy verification

**Files:**
- Create: `src/Rfbm.Data/Import/LegacyPythonDatabaseImporter.cs`
- Create: `scripts/publish.ps1`
- Replace: `README.md`
- Replace: `start_manager.bat`
- Modify: `.gitignore`
- Modify: `docs/ai/tasks.md`
- Modify: `docs/ai/progress.md`

- [ ] Add a temporary legacy-schema fixture and test that import copies rows/links/images/status without changing the source database.
- [ ] Implement dry-run and import result reporting, resolving duplicates through stable thread identity.
- [ ] Publish a self-contained or framework-dependent Windows package as documented, excluding all local data/profile/media paths.
- [ ] Run fresh restore, Debug/Release build, every runner, extension checks, publish, old Python tests and privacy grep.
- [ ] Launch the packaged executable and inspect empty-state, filters, list/detail, recovery and progress surfaces.
- [ ] Request a final spec and code-quality review; fix all important findings before handoff.
