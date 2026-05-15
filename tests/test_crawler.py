from pathlib import Path
import asyncio
import base64

from playwright.async_api import Error as PlaywrightError

from forum_bookmark_manager.crawler import (
    AsyncRateLimiter,
    ImageDownloadJob,
    NetworkStandby,
    build_context_launch_options,
    image_targets,
    launch_edge_context,
    open_clean_visible_page,
    pending_favorite_page_urls,
    prepare_post_page_for_parsing,
    wait_for_manual_close,
)
from forum_bookmark_manager.parser import FavoritePage
from forum_bookmark_manager.settings import Settings
from forum_bookmark_manager.crawler import ForumCrawler
from forum_bookmark_manager.models import FavoriteItem, PostImage
from forum_bookmark_manager.routing import SiteMirrorRouter


def make_settings(tmp_path) -> Settings:
    return Settings(
        start_url="https://example.test/favorites",
        detail_concurrency=12,
        image_concurrency=16,
        favorite_page_concurrency=6,
        retry_count=2,
        database_path=tmp_path / "bookmarks.sqlite3",
        edge_profile_dir=tmp_path / "edge-profile",
        image_dir=tmp_path / "images",
        thumbnail_dir=tmp_path / "thumbnails",
        edge_profile_mode="managed",
        edge_profile_wait_seconds=1,
    )


def test_image_targets_are_stable_local_and_browser_paths(tmp_path):
    settings = make_settings(tmp_path)

    targets = image_targets(
        settings,
        post_url="https://example.test/thread-1001.html",
        source_url="https://cdn.example.test/pic/photo.JPG?token=123",
    )

    assert targets.image_path.parent == Path(settings.image_dir)
    assert targets.thumbnail_path.parent == Path(settings.thumbnail_dir)
    assert targets.image_path.suffix == ".jpg"
    assert targets.thumbnail_path.suffix == ".jpg"
    assert targets.image_web_path.startswith("/media/images/")
    assert targets.thumbnail_web_path.startswith("/media/thumbnails/")
    assert image_targets(settings, "https://example.test/thread-1001.html", "https://cdn.example.test/pic/photo.JPG?token=123") == targets


def test_crawl_uses_headless_edge_options_and_login_is_visible(tmp_path):
    settings = make_settings(tmp_path)

    crawl_options = build_context_launch_options(settings, headless=True)
    login_options = build_context_launch_options(settings, headless=False)

    assert crawl_options["channel"] == "msedge"
    assert crawl_options["headless"] is True
    assert login_options["headless"] is False
    assert crawl_options["user_data_dir"] == str(settings.edge_profile_dir)
    assert "args" not in crawl_options


def test_crawl_uses_configured_browser_proxy(tmp_path):
    settings = make_settings(tmp_path)
    settings = Settings(**{**settings.__dict__, "proxy_server": "http://127.0.0.1:7890"})

    crawl_options = build_context_launch_options(settings, headless=True)

    assert crawl_options["proxy"] == {"server": "http://127.0.0.1:7890"}


def test_crawl_can_use_real_edge_default_profile(tmp_path):
    settings = Settings(
        start_url="https://example.test/favorites",
        detail_concurrency=24,
        image_concurrency=32,
        favorite_page_concurrency=8,
        retry_count=2,
        database_path=tmp_path / "bookmarks.sqlite3",
        edge_profile_dir=tmp_path / "edge-profile",
        image_dir=tmp_path / "images",
        thumbnail_dir=tmp_path / "thumbnails",
        edge_profile_mode="system",
        edge_user_data_dir=tmp_path / "Microsoft" / "Edge" / "User Data",
        edge_profile_directory="Default",
        edge_profile_wait_seconds=1,
    )

    crawl_options = build_context_launch_options(settings, headless=True)

    assert crawl_options["channel"] == "msedge"
    assert crawl_options["headless"] is True
    assert crawl_options["user_data_dir"] == str(settings.edge_user_data_dir)
    assert crawl_options["args"] == ["--profile-directory=Default"]


def test_launch_edge_context_waits_for_real_profile_to_be_closed(tmp_path):
    user_data_dir = tmp_path / "Microsoft" / "Edge" / "User Data"
    user_data_dir.mkdir(parents=True)
    settings = Settings(
        start_url="https://example.test/favorites",
        detail_concurrency=24,
        image_concurrency=32,
        favorite_page_concurrency=8,
        retry_count=2,
        database_path=tmp_path / "bookmarks.sqlite3",
        edge_profile_dir=tmp_path / "edge-profile",
        image_dir=tmp_path / "images",
        thumbnail_dir=tmp_path / "thumbnails",
        edge_profile_mode="system",
        edge_user_data_dir=user_data_dir,
        edge_profile_directory="Default",
        edge_profile_wait_seconds=1,
    )
    fake_playwright = FakePlaywrightContext([PlaywrightError("Target page, context or browser has been closed")])
    messages: list[str] = []

    context = asyncio.run(
        launch_edge_context(
            fake_playwright,
            settings,
            headless=True,
            wait_for_profile=True,
            on_wait=messages.append,
            retry_delay=0,
        )
    )

    assert context == "context"
    assert fake_playwright.chromium.calls == 2
    assert "真实 Edge 登录缓存正在被占用" in messages[0]


def test_open_clean_visible_page_closes_restored_tabs_before_navigating():
    context = FakeVisibleContext([FakeVisiblePage("https://old.example/1"), FakeVisiblePage("https://old.example/2")])

    page = asyncio.run(open_clean_visible_page(context, "https://example.test/favorites"))

    assert page.url == "https://example.test/favorites"
    assert page.goto_calls == ["https://example.test/favorites"]
    assert [old.closed for old in context.restored_pages] == [True, True]


def test_wait_for_manual_close_disables_playwright_default_timeout():
    page = FakeManualClosePage()

    asyncio.run(wait_for_manual_close(page))

    assert page.wait_calls == [("close", 0)]


def test_network_standby_uses_one_probe_until_target_recovers():
    async def scenario():
        page = FakeProbePage(failures=2)
        messages = []
        standby = NetworkStandby(probe_interval=0, probe_timeout=1000)

        recovered = await standby.recover(page, "https://example.test/thread-1.html", messages.append)

        assert recovered is True
        assert page.goto_calls == [
            "https://example.test/thread-1.html",
            "https://example.test/thread-1.html",
            "https://example.test/thread-1.html",
        ]
        assert any("断联待机" in message for message in messages)
        assert any("链接恢复" in message for message in messages)

    asyncio.run(scenario())


def test_prepare_post_page_for_parsing_scrolls_and_reveals_lazy_images(tmp_path):
    page = FakeRenderablePage()
    settings = make_settings(tmp_path)

    asyncio.run(prepare_post_page_for_parsing(page, settings))

    assert page.waits
    assert page.evaluated_scripts
    assert page.lazy_image_revealed is True


def test_rate_limiter_can_be_disabled():
    limiter = AsyncRateLimiter(0)

    asyncio.run(limiter.wait())

    assert limiter.interval_seconds == 0


def test_pending_favorite_page_urls_prefers_all_discovered_pagination_urls():
    parsed = FavoritePage(
        items=[],
        next_url="https://example.test/favorite&page=2",
        page_urls=[
            "https://example.test/favorite&page=1",
            "https://example.test/favorite&page=2",
            "https://example.test/favorite&page=3",
        ],
        max_page=3,
    )

    urls = pending_favorite_page_urls(
        parsed,
        seen_pages={"https://example.test/favorite&page=1"},
        queued_pages=set(),
    )

    assert urls == [
        "https://example.test/favorite&page=2",
        "https://example.test/favorite&page=3",
    ]


def test_site_mirror_router_switches_hosts_after_failure():
    router = SiteMirrorRouter(("https://primary.example.test", "https://mirror.example.test"))
    url = "https://primary.example.test/forum.php?mod=viewthread&tid=2743445"

    assert router.candidate_urls(url) == [
        "https://primary.example.test/forum.php?mod=viewthread&tid=2743445",
        "https://mirror.example.test/forum.php?mod=viewthread&tid=2743445",
    ]

    router.mark_failure(url)

    assert router.candidate_urls(url) == [
        "https://mirror.example.test/forum.php?mod=viewthread&tid=2743445",
        "https://primary.example.test/forum.php?mod=viewthread&tid=2743445",
    ]


def test_download_image_tries_mirror_with_referer_after_first_host_fails(tmp_path):
    async def scenario():
        settings = Settings(
            **{
                **make_settings(tmp_path).__dict__,
                "site_base_urls": ("https://primary.example.test", "https://mirror.example.test"),
            }
        )
        context = FakeImageContext(
            [
                FakeImageResponse(502, b"bad gateway", "text/plain"),
                FakeImageResponse(200, ONE_PIXEL_PNG, "image/png"),
            ]
        )
        crawler = ForumCrawler(settings, repository=object())
        image = PostImage(
            source_url="https://primary.example.test/remote/data/attachment/forum/real-1.jpg",
            position=1,
        )

        result = await crawler._download_image(
            context,
            "https://primary.example.test/forum.php?mod=viewthread&tid=2743445",
            image,
            asyncio.Semaphore(1),
            referer_url="https://mirror.example.test/forum.php?mod=viewthread&tid=2743445",
        )

        assert result.download_status == "downloaded"
        assert [call["url"] for call in context.request.calls] == [
            "https://primary.example.test/remote/data/attachment/forum/real-1.jpg",
            "https://mirror.example.test/remote/data/attachment/forum/real-1.jpg",
        ]
        assert all(
            call["headers"]["Referer"] == "https://mirror.example.test/forum.php?mod=viewthread&tid=2743445"
            for call in context.request.calls
        )

    asyncio.run(scenario())


def test_detail_worker_saves_content_and_enqueues_images_without_downloading(tmp_path):
    async def scenario():
        settings = Settings(**{**make_settings(tmp_path).__dict__, "request_delay_seconds": 0})
        repo = FakeCrawlerRepository()
        crawler = FastFakeCrawler(settings, repo)
        context = FakeDetailContext(
            """
            <html><body>
              <h1 id="thread_subject">separate image queue</h1>
              <div id="postlist"><div id="post_100"><div class="pcb">
                <div class="pattl">
                  <img src="/remote/data/attachment/forum/one.jpg" width="640" height="480">
                </div>
              </div></div></div>
            </body></html>
            """
        )
        detail_queue: asyncio.Queue[FavoriteItem] = asyncio.Queue()
        image_queue: asyncio.Queue[ImageDownloadJob] = asyncio.Queue()
        detail_queue.put_nowait(FavoriteItem(url="https://example.test/thread-1.html"))

        await crawler._detail_worker(
            context,
            detail_queue,
            image_queue,
            run_id=1,
            counters={"processed_posts": 0, "successful_posts": 0, "failed_posts": 0},
            lock=asyncio.Lock(),
            worker_index=1,
        )

        assert len(repo.posts) == 1
        assert repo.posts[0].images[0].download_status == "pending"
        assert image_queue.qsize() == 1
        job = await image_queue.get()
        assert job.post_url == "https://example.test/thread-1.html"
        assert job.referer_url == "https://example.test/thread-1.html"
        assert job.image.source_url == "https://example.test/remote/data/attachment/forum/one.jpg"
        assert not hasattr(context, "request")

    asyncio.run(scenario())


def test_image_worker_downloads_from_image_queue_and_updates_repository(tmp_path):
    async def scenario():
        settings = Settings(**{**make_settings(tmp_path).__dict__, "image_delay_seconds": 0})
        repo = FakeCrawlerRepository()
        crawler = ForumCrawler(settings, repo)
        context = FakeImageContext([FakeImageResponse(200, ONE_PIXEL_PNG, "image/png")])
        image_queue: asyncio.Queue[ImageDownloadJob] = asyncio.Queue()
        image_queue.put_nowait(
            ImageDownloadJob(
                post_url="https://example.test/thread-1.html",
                referer_url="https://example.test/thread-1.html",
                image=PostImage(source_url="https://example.test/one.png", position=1),
            )
        )

        worker = asyncio.create_task(crawler._image_worker(context, image_queue, asyncio.Semaphore(1), 1))
        await image_queue.join()
        worker.cancel()
        await asyncio.gather(worker, return_exceptions=True)

        assert len(repo.updated_images) == 1
        assert repo.updated_images[0][0] == "https://example.test/thread-1.html"
        assert repo.updated_images[0][1].download_status == "downloaded"

    asyncio.run(scenario())


def test_collect_favorites_fetches_discovered_pages_in_parallel(tmp_path):
    html_by_url = {
        "https://example.test/home.php?mod=space&do=favorite&page=1": """
            <div id="ct">
              <li id="favorite_li_1"><a href="forum.php?mod=viewthread&tid=101">一</a> 2026-05-13</li>
            </div>
            <div class="pg">
              <strong>1</strong>
              <a href="home.php?mod=space&do=favorite&page=2">2</a>
              <a href="home.php?mod=space&do=favorite&page=3">3</a>
            </div>
        """,
        "https://example.test/home.php?mod=space&do=favorite&page=2": """
            <div id="ct">
              <li id="favorite_li_2"><a href="forum.php?mod=viewthread&tid=102">二</a> 2026-05-12</li>
            </div>
        """,
        "https://example.test/home.php?mod=space&do=favorite&page=3": """
            <div id="ct">
              <li id="favorite_li_3"><a href="forum.php?mod=viewthread&tid=103">三</a> 2026-05-11</li>
            </div>
        """,
    }
    context = FakeFavoriteContext(html_by_url)
    settings = make_settings(tmp_path)
    crawler = ForumCrawler(
        settings,
        repository=object(),
        start_url="https://example.test/home.php?mod=space&do=favorite&page=1",
    )

    favorites = asyncio.run(crawler._collect_favorites(context))

    assert context.max_active_pages > 1
    assert {item.title for item in favorites} == {"一", "二", "三"}


class FakeFavoriteContext:
    def __init__(self, html_by_url):
        self.html_by_url = html_by_url
        self.active_pages = 0
        self.max_active_pages = 0

    async def new_page(self):
        return FakeFavoritePage(self)


class FakeFavoritePage:
    def __init__(self, context):
        self.context = context
        self.url = ""

    async def goto(self, url, **_kwargs):
        self.url = url
        self.context.active_pages += 1
        self.context.max_active_pages = max(self.context.max_active_pages, self.context.active_pages)
        await asyncio.sleep(0.01)
        self.context.active_pages -= 1

    async def content(self):
        return self.context.html_by_url[self.url]

    async def close(self):
        pass


class FastFakeCrawler(ForumCrawler):
    async def _goto_page(self, page, url, **_kwargs):
        page.url = url
        return url


class FakeCrawlerRepository:
    def __init__(self):
        self.posts = []
        self.updated_images = []
        self.errors = []
        self.run_updates = []

    def upsert_post(self, post):
        self.posts.append(post)
        return len(self.posts)

    def update_post_image(self, post_url, image):
        self.updated_images.append((post_url, image))
        return True

    def record_error(self, run_id, url, stage, message):
        self.errors.append((run_id, url, stage, message))

    def update_crawl_run(self, run_id, **fields):
        self.run_updates.append((run_id, fields))


class FakeDetailContext:
    def __init__(self, html):
        self.html = html

    async def new_page(self):
        return FakeDetailPage(self.html)


class FakeDetailPage:
    def __init__(self, html):
        self.html = html
        self.url = ""
        self.closed = False

    async def content(self):
        return self.html

    async def wait_for_timeout(self, _timeout):
        pass

    async def evaluate(self, *_args):
        return None

    def is_closed(self):
        return self.closed

    async def close(self):
        self.closed = True


class FakeVisibleContext:
    def __init__(self, pages):
        self.restored_pages = pages
        self.pages = list(pages)

    async def new_page(self):
        page = FakeVisiblePage("about:blank")
        self.pages.append(page)
        return page


class FakeVisiblePage:
    def __init__(self, url):
        self.url = url
        self.closed = False
        self.goto_calls = []

    async def goto(self, url, **_kwargs):
        self.url = url
        self.goto_calls.append(url)

    async def close(self):
        self.closed = True


class FakeManualClosePage:
    def __init__(self):
        self.wait_calls = []

    async def wait_for_event(self, event, **kwargs):
        self.wait_calls.append((event, kwargs.get("timeout")))


class FakeRenderablePage:
    def __init__(self):
        self.waits = []
        self.evaluated_scripts = []
        self.lazy_image_revealed = False

    async def wait_for_timeout(self, timeout):
        self.waits.append(timeout)

    async def evaluate(self, script, *args):
        self.evaluated_scripts.append(script)
        if "data-original" in script and "scrollIntoView" in script:
            self.lazy_image_revealed = True
        return None


class FakeProbePage:
    def __init__(self, failures):
        self.failures = failures
        self.goto_calls = []

    async def goto(self, url, **_kwargs):
        self.goto_calls.append(url)
        if self.failures:
            self.failures -= 1
            raise RuntimeError("Page.goto: net::ERR_TIMED_OUT")


ONE_PIXEL_PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="
)


class FakeImageContext:
    def __init__(self, responses):
        self.request = FakeImageRequest(responses)


class FakeImageRequest:
    def __init__(self, responses):
        self.responses = list(responses)
        self.calls = []

    async def get(self, url, **kwargs):
        self.calls.append({"url": url, "headers": kwargs.get("headers") or {}})
        return self.responses.pop(0)


class FakeImageResponse:
    def __init__(self, status, body, content_type):
        self.status = status
        self.ok = 200 <= status < 300
        self._body = body
        self.headers = {"content-type": content_type}

    async def body(self):
        return self._body


class FakePlaywrightContext:
    def __init__(self, failures):
        self.chromium = FakeChromium(failures)


class FakeChromium:
    def __init__(self, failures):
        self.failures = list(failures)
        self.calls = 0

    async def launch_persistent_context(self, **_options):
        self.calls += 1
        if self.failures:
            raise self.failures.pop(0)
        return "context"
