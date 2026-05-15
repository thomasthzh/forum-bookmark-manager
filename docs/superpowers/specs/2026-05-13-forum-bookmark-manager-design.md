# Forum Bookmark Manager Design

## Goal

Build a local tool that uses an isolated logged-in Edge automation profile to crawl all favorite posts from a user-provided forum favorite URL, store structured post metadata in SQLite, download up to nine post images locally, and expose a Chinese HTML database browser with type and status filters.

## Confirmed Requirements

- Use a separate Edge automation user data directory for login state. The tool must not export the user's main Edge cookies to plaintext.
- Crawl every favorite item across all favorite pages, then visit each original favorite post.
- For each post, store title, up to nine images, archive/extract password, all download links with nearby explanatory text, project type, favorite time, download count, visit count, favorite count, and original post URL.
- Download the first nine useful post images to local storage and keep both original URL and local path.
- Do not download resource files from the extracted download links.
- Project type detection order: page category/tag first, then keyword classification from title/body/link text, then `未分类`.
- Default item status is `未下载`.
- Status button cycles on each click: `未下载 -> 已下载 -> 链接失效 -> 未下载`.
- The HTML browser supports `全部`, status-only filtering, type-only filtering, combined type + status filtering such as `游戏 + 链接失效`, search, and favorite-time sorting.
- Crawling must be parallel and give visible progress feedback without blocking crawl throughput.

## Architecture

The app is a Python project with three clear surfaces:

1. **Crawler CLI:** launches Microsoft Edge through Playwright with a persistent user data directory, discovers favorite pages, parses favorite links, then crawls post detail pages concurrently.
2. **SQLite Repository:** stores crawl results, downloaded image records, extracted links, crawl errors, and user-controlled status flags.
3. **Local Web UI:** a FastAPI server serves Chinese HTML/CSS/JS that reads the SQLite database, shows crawl progress, filters items, and updates item status.

The crawler and web UI share only the repository layer. This keeps scraping logic independent from presentation and lets tests exercise parsing, storage, and status transitions without opening a browser.

## Concurrency Design

- `detail_concurrency` defaults to `8` in the example config and is configurable in `config/settings.toml`.
- `image_concurrency` defaults to `9` in the example config and is configurable.
- The crawler uses an asyncio queue for favorite/post URLs and a second bounded queue for image downloads.
- Each detail worker owns or borrows a Playwright page from a small page pool. This avoids launching one browser per post while still allowing parallel page visits.
- Progress events are pushed into a lightweight in-memory event buffer and persisted as crawl run counters in SQLite.
- UI progress polling reads counters from the database. It never waits on scraper tasks directly, so UI refresh cannot slow down crawling.
- Failed detail pages are retried with exponential backoff up to a configurable retry count, default `2`.

## Login Flow

- First run command: `python -m forum_bookmark_manager.cli login`
- This opens Edge using the dedicated profile directory `data/edge-profile`.
- The user logs in manually if needed, then closes the browser or presses Enter in the terminal.
- Crawl command: `python -m forum_bookmark_manager.cli crawl`
- The crawl command reuses `data/edge-profile` and checks whether the favorite page looks logged in. If it appears logged out, it records a clear error and asks the user to run login again.

## Data Model

SQLite file: `data/bookmarks.sqlite3`

### `posts`

- `id`: integer primary key
- `post_url`: unique text
- `favorite_url`: text
- `title`: text
- `project_type`: text, default `未分类`
- `status`: text, default `未下载`
- `favorite_time`: text, nullable
- `download_count`: integer, nullable
- `visit_count`: integer, nullable
- `favorite_count`: integer, nullable
- `extract_password`: text, nullable
- `body_text`: text, nullable
- `created_at`: ISO timestamp
- `updated_at`: ISO timestamp
- `last_crawled_at`: ISO timestamp, nullable

### `post_images`

- `id`: integer primary key
- `post_id`: foreign key to `posts.id`
- `position`: integer from 1 to 9
- `source_url`: text
- `local_path`: text, nullable
- `thumbnail_path`: text, nullable
- `download_status`: text

### `download_links`

- `id`: integer primary key
- `post_id`: foreign key to `posts.id`
- `position`: integer
- `url`: text
- `label`: text
- `context_text`: text

### `crawl_runs`

- `id`: integer primary key
- `started_at`: ISO timestamp
- `finished_at`: ISO timestamp, nullable
- `status`: text
- `total_favorites`: integer
- `processed_posts`: integer
- `successful_posts`: integer
- `failed_posts`: integer
- `message`: text, nullable

### `crawl_errors`

- `id`: integer primary key
- `run_id`: foreign key to `crawl_runs.id`
- `url`: text
- `stage`: text
- `message`: text
- `created_at`: ISO timestamp

## Parsing Design

Favorite list parsing:

- Read every favorite list page starting from the configured URL.
- Extract post links from anchors that point to Discuz thread/view URLs or forum post URLs.
- Extract favorite time from the favorite row/container text when present.
- Follow pagination until no next page is found or a repeated page URL is detected.

Post parsing:

- Title comes from common Discuz title selectors first, then `<title>`.
- Project type comes from breadcrumb/category text when present.
- Body text comes from the main post content container.
- Images come from `<img>` elements inside the post body, excluding avatars, icons, sprites, smilies, tracking pixels, and images smaller than a configurable threshold when dimensions are available.
- Download links are all anchors inside the main post body whose `href` or text indicates a resource link, including common netdisk, magnet, ed2k, torrent, attachment, and plain URL forms.
- Link explanation text is built from the anchor text plus the nearest surrounding paragraph/list/table row text.
- Extract password is detected with Chinese and English patterns such as `解压密码`, `提取码`, `密码`, `password`, and nearby text after separators.
- Counts are parsed from page text with labels for downloads, visits/views, and favorites/收藏.

## Web UI Design

The accepted direction is the Chinese left-filter layout:

- Left sidebar: status filters (`全部`, `未下载`, `已下载`, `链接失效`), type filters (`全部类型`, known types, `未分类`), and crawl progress.
- Top toolbar: search input, favorite-time sort buttons, and crawl/start controls.
- Main list: one row/card per post with type, title, up to nine thumbnails, extract password, download links with explanation text, body excerpt, stats, favorite time, and original post link.
- Right-side status button on each item updates the persisted status by cycling through the three states.
- Filtering combines status, type, search text, and sort order.
- UI is Chinese by default.

## Error Handling

- Login-required pages are detected and reported without deleting existing data.
- Network, timeout, and parse errors are written to `crawl_errors`.
- A failed post does not stop the whole crawl.
- If an image fails to download, the post is still stored and the image row records the failed status.
- Re-running crawl updates existing posts by `post_url` and preserves manually set `status`.

## Testing Strategy

- Unit tests for status cycling, type detection, password extraction, download link extraction, image filtering, and count parsing.
- Repository tests use a temporary SQLite database and verify upsert behavior preserves manual status.
- API tests verify filtering by status, type, combined filters, search, sorting, and status updates.
- CLI smoke tests verify commands parse settings and can initialize data directories without network.
- Manual verification covers login and real crawl because it depends on the user's forum account and live site.

## Non-Goals

- Do not auto-download files from extracted resource links.
- Do not bypass CAPTCHA, age verification, browser security interstitials, or forum anti-abuse checks.
- Do not scrape or export the user's main Edge cookie store.
- Do not build a packaged desktop installer in the first version.
