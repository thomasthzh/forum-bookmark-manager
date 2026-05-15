# Forum Bookmark Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local Chinese forum favorite crawler and database browser with persistent Edge login, parallel crawling, local image downloads, SQLite storage, and status/type filtering.

**Architecture:** Python package with focused modules for settings, parsing, persistence, crawling, CLI, and web UI. The crawler writes to SQLite through a repository layer; FastAPI reads the same repository and serves static HTML/JS/CSS. Tests cover deterministic parsing, status transitions, repository upsert behavior, and API filtering without needing live forum access.

**Tech Stack:** Python 3.13, Playwright, FastAPI, Uvicorn, BeautifulSoup4, HTTPX, Pillow, SQLite, Pytest.

---

## File Structure

- Create: `pyproject.toml` - package metadata, pytest config, console scripts.
- Create: `requirements.txt` - runtime and test dependencies.
- Create: `.gitignore` - local data, virtualenvs, caches, and brainstorm previews.
- Create: `config/settings.example.toml` - public default URL, concurrency, paths, retry settings.
- Create: `src/forum_bookmark_manager/__init__.py` - package marker.
- Create: `src/forum_bookmark_manager/settings.py` - load typed settings from TOML.
- Create: `src/forum_bookmark_manager/models.py` - dataclasses and status helpers.
- Create: `src/forum_bookmark_manager/parser.py` - deterministic HTML parsing helpers.
- Create: `src/forum_bookmark_manager/repository.py` - SQLite schema and data access.
- Create: `src/forum_bookmark_manager/crawler.py` - Playwright Edge crawler and image downloader.
- Create: `src/forum_bookmark_manager/cli.py` - `login`, `crawl`, and `serve` commands.
- Create: `src/forum_bookmark_manager/web.py` - FastAPI routes and static file setup.
- Create: `src/forum_bookmark_manager/static/index.html` - Chinese database browser UI.
- Create: `src/forum_bookmark_manager/static/styles.css` - UI layout and responsive styles.
- Create: `src/forum_bookmark_manager/static/app.js` - filters, sorting, status updates, progress polling.
- Create: `tests/test_models.py` - status cycle and settings behavior.
- Create: `tests/test_parser.py` - title, password, type, image, link, count parsing.
- Create: `tests/test_repository.py` - schema, upsert, status preservation.
- Create: `tests/test_web.py` - API filtering and status transition.

## Task 1: Project Scaffolding And Domain Helpers

**Files:**
- Create: `pyproject.toml`
- Create: `requirements.txt`
- Create: `.gitignore`
- Create: `config/settings.example.toml`
- Create: `src/forum_bookmark_manager/__init__.py`
- Create: `src/forum_bookmark_manager/models.py`
- Create: `src/forum_bookmark_manager/settings.py`
- Test: `tests/test_models.py`

- [ ] **Step 1: Write failing tests for status cycle and settings defaults**

Expected tests:

```python
from pathlib import Path

from forum_bookmark_manager.models import DownloadStatus, next_status
from forum_bookmark_manager.settings import load_settings


def test_next_status_cycles_through_three_manual_states():
    assert next_status(DownloadStatus.PENDING) == DownloadStatus.DOWNLOADED
    assert next_status(DownloadStatus.DOWNLOADED) == DownloadStatus.INVALID
    assert next_status(DownloadStatus.INVALID) == DownloadStatus.PENDING


def test_settings_loads_default_paths(tmp_path):
    config = tmp_path / "settings.toml"
    config.write_text(
        """
start_url = "https://example.test/favorites"
detail_concurrency = 12
image_concurrency = 16
retry_count = 2
database_path = "data/bookmarks.sqlite3"
edge_profile_dir = "data/edge-profile"
image_dir = "data/images"
thumbnail_dir = "data/thumbnails"
""",
        encoding="utf-8",
    )

    settings = load_settings(config)

    assert settings.start_url == "https://example.test/favorites"
    assert settings.detail_concurrency == 12
    assert settings.image_concurrency == 16
    assert settings.database_path == Path("data/bookmarks.sqlite3")
```

- [ ] **Step 2: Run tests to verify RED**

Run: `python -m pytest tests/test_models.py -v`

Expected: FAIL because `forum_bookmark_manager` does not exist yet.

- [ ] **Step 3: Implement minimal scaffolding and domain helpers**

Implement package metadata, dependency files, settings loader, `DownloadStatus`, and `next_status`.

- [ ] **Step 4: Run tests to verify GREEN**

Run: `python -m pytest tests/test_models.py -v`

Expected: 2 passed.

## Task 2: Parser

**Files:**
- Create: `src/forum_bookmark_manager/parser.py`
- Test: `tests/test_parser.py`

- [ ] **Step 1: Write failing parser tests**

Tests cover favorite links, title, password, counts, image filtering, download link context, and type fallback.

- [ ] **Step 2: Run parser tests to verify RED**

Run: `python -m pytest tests/test_parser.py -v`

Expected: FAIL because parser functions do not exist.

- [ ] **Step 3: Implement parser helpers**

Implement `parse_favorite_page`, `parse_post_page`, `detect_project_type`, `extract_password`, `extract_counts`, `extract_download_links`, and `extract_images`.

- [ ] **Step 4: Run parser tests to verify GREEN**

Run: `python -m pytest tests/test_parser.py -v`

Expected: all parser tests pass.

## Task 3: SQLite Repository

**Files:**
- Create: `src/forum_bookmark_manager/repository.py`
- Test: `tests/test_repository.py`

- [ ] **Step 1: Write failing repository tests**

Tests initialize schema, upsert posts with images and links, preserve manual status when a crawl updates the same post, and query with combined status/type filters.

- [ ] **Step 2: Run repository tests to verify RED**

Run: `python -m pytest tests/test_repository.py -v`

Expected: FAIL because repository does not exist.

- [ ] **Step 3: Implement schema and data access**

Implement `Repository.initialize`, `upsert_post`, `list_posts`, `update_status`, `start_crawl_run`, `update_crawl_run`, and `record_error`.

- [ ] **Step 4: Run repository tests to verify GREEN**

Run: `python -m pytest tests/test_repository.py -v`

Expected: all repository tests pass.

## Task 4: Web API And Chinese UI

**Files:**
- Create: `src/forum_bookmark_manager/web.py`
- Create: `src/forum_bookmark_manager/static/index.html`
- Create: `src/forum_bookmark_manager/static/styles.css`
- Create: `src/forum_bookmark_manager/static/app.js`
- Test: `tests/test_web.py`

- [ ] **Step 1: Write failing web tests**

Tests create a temporary database, seed posts, call FastAPI routes, verify combined filters, and verify status cycle persistence.

- [ ] **Step 2: Run web tests to verify RED**

Run: `python -m pytest tests/test_web.py -v`

Expected: FAIL because web routes do not exist.

- [ ] **Step 3: Implement FastAPI routes**

Implement:

- `GET /api/posts`
- `POST /api/posts/{post_id}/cycle-status`
- `GET /api/types`
- `GET /api/progress`
- `POST /api/crawl`
- static root page serving `index.html`

- [ ] **Step 4: Implement Chinese frontend**

Implement left sidebar filters, search, sort, status button cycling, thumbnails, download link context display, original post links, and progress polling.

- [ ] **Step 5: Run web tests to verify GREEN**

Run: `python -m pytest tests/test_web.py -v`

Expected: all web tests pass.

## Task 5: Crawler And CLI

**Files:**
- Create: `src/forum_bookmark_manager/crawler.py`
- Create: `src/forum_bookmark_manager/cli.py`

- [ ] **Step 1: Add crawler smoke tests where deterministic**

Use existing parser/repository tests for deterministic behavior. Avoid live-site tests in CI because login and the forum state are user-specific.

- [ ] **Step 2: Implement Edge login command**

Implement `python -m forum_bookmark_manager.cli login` using Playwright persistent context with `channel="msedge"` and `user_data_dir=data/edge-profile`.

- [ ] **Step 3: Implement parallel crawl command**

Implement `python -m forum_bookmark_manager.cli crawl` with configurable detail and image concurrency, retry handling, local image downloads, SQLite writes, and progress counters.

- [ ] **Step 4: Implement serve command**

Implement `python -m forum_bookmark_manager.cli serve` to initialize the database and run Uvicorn.

- [ ] **Step 5: Run full automated verification**

Run: `python -m pytest -v`

Expected: all tests pass. Live crawl is verified manually after login because it depends on the user's account.

## Task 6: Manual Verification And Handoff

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Write usage docs**

Document install, login, crawl, serve, concurrency settings, data paths, and the fact that resource files are not automatically downloaded.

- [ ] **Step 2: Run verification commands**

Run:

```powershell
python -m pytest -v
python -m forum_bookmark_manager.cli --help
python -m forum_bookmark_manager.cli serve --help
```

- [ ] **Step 3: Start local server**

Run: `python -m forum_bookmark_manager.cli serve --port 53102`

Expected: HTML UI available at `http://127.0.0.1:53102`.

- [ ] **Step 4: Report manual crawl instructions**

Tell the user to run login first, then crawl, then open the server URL. State that the crawler uses configurable detail and image workers and can be tuned in `config/settings.toml`.
