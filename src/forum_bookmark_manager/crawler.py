from __future__ import annotations

import asyncio
from collections.abc import Callable, Iterable
from dataclasses import dataclass, replace
from hashlib import sha1
from io import BytesIO
from pathlib import Path
import time
from urllib.parse import urlparse, urlunparse

from PIL import Image
from playwright.async_api import BrowserContext, Error as PlaywrightError, async_playwright

from .clash import ClashProxyRotator
from .models import FavoriteItem, ParsedPost, PostImage
from .parser import parse_favorite_page, parse_post_page
from .repository import Repository
from .routing import SiteMirrorRouter
from .selector_profile import load_selector_profile
from .settings import Settings
from .tab_registry import headless_tabs


@dataclass(frozen=True)
class ImageTargets:
    image_path: Path
    thumbnail_path: Path
    image_web_path: str
    thumbnail_web_path: str


@dataclass(frozen=True)
class ImageDownloadJob:
    post_url: str
    referer_url: str | None
    image: PostImage


def image_targets(settings: Settings, post_url: str, source_url: str) -> ImageTargets:
    digest = sha1(f"{post_url}|{source_url}".encode("utf-8")).hexdigest()[:20]
    suffix = Path(urlparse(source_url).path).suffix.lower()
    if suffix not in {".jpg", ".jpeg", ".png", ".webp", ".gif"}:
        suffix = ".jpg"
    if suffix == ".jpeg":
        suffix = ".jpg"
    image_name = f"{digest}{suffix}"
    thumbnail_name = f"{digest}.jpg"
    return ImageTargets(
        image_path=settings.image_dir / image_name,
        thumbnail_path=settings.thumbnail_dir / thumbnail_name,
        image_web_path=f"/media/images/{image_name}",
        thumbnail_web_path=f"/media/thumbnails/{thumbnail_name}",
    )


def build_context_launch_options(settings: Settings, *, headless: bool) -> dict[str, object]:
    options: dict[str, object] = {
        "user_data_dir": str(edge_context_user_data_dir(settings)),
        "channel": "msedge",
        "headless": headless,
    }
    if settings.edge_profile_mode == "system" and settings.edge_profile_directory:
        options["args"] = [f"--profile-directory={settings.edge_profile_directory}"]
    if settings.proxy_server:
        options["proxy"] = {"server": settings.proxy_server}
    return options


def edge_context_user_data_dir(settings: Settings) -> Path:
    if settings.edge_profile_mode == "system":
        if settings.edge_user_data_dir is None:
            raise RuntimeError("系统 Edge 缓存目录未配置。")
        return settings.edge_user_data_dir
    return settings.edge_profile_dir


def ensure_edge_context_dir(settings: Settings) -> None:
    user_data_dir = edge_context_user_data_dir(settings)
    if settings.edge_profile_mode == "system":
        if not user_data_dir.exists():
            raise RuntimeError(f"找不到系统 Edge 缓存目录：{user_data_dir}")
        return
    user_data_dir.mkdir(parents=True, exist_ok=True)


async def launch_edge_context(
    playwright,
    settings: Settings,
    *,
    headless: bool,
    wait_for_profile: bool = False,
    on_wait: Callable[[str], None] | None = None,
    retry_delay: float = 2.0,
) -> BrowserContext:
    ensure_edge_context_dir(settings)
    deadline = time.monotonic() + max(0, settings.edge_profile_wait_seconds)
    wait_reported = False
    while True:
        try:
            return await playwright.chromium.launch_persistent_context(
                **build_context_launch_options(settings, headless=headless),
            )
        except PlaywrightError as exc:
            if settings.edge_profile_mode != "system" or not _looks_like_profile_lock_error(str(exc)):
                raise
            message = profile_in_use_message(settings)
            if not wait_for_profile:
                raise RuntimeError(message) from exc
            if not wait_reported and on_wait is not None:
                on_wait(message)
                wait_reported = True
            if time.monotonic() >= deadline:
                raise RuntimeError(f"等待 Edge 关闭超时。{message}") from exc
            await asyncio.sleep(min(retry_delay, max(0, deadline - time.monotonic())))


def profile_in_use_message(settings: Settings) -> str:
    return (
        "真实 Edge 登录缓存正在被占用。请关闭所有 Microsoft Edge 窗口和后台 Edge 进程后再开始爬取；"
        f"当前配置的缓存目录是：{edge_context_user_data_dir(settings)}，"
        f"profile 是：{settings.edge_profile_directory}。"
    )


def _looks_like_profile_lock_error(message: str) -> bool:
    lowered = message.lower()
    return "target page, context or browser has been closed" in lowered or "process did exit" in lowered


def pending_favorite_page_urls(parsed, *, seen_pages: set[str], queued_pages: set[str]) -> list[str]:
    candidates = parsed.page_urls or ([parsed.next_url] if parsed.next_url else [])
    pending: list[str] = []
    for url in candidates:
        if url and url not in seen_pages and url not in queued_pages:
            pending.append(url)
            queued_pages.add(url)
    return pending


class AsyncRateLimiter:
    def __init__(self, interval_seconds: float):
        self.interval_seconds = max(0.0, interval_seconds)
        self._lock = asyncio.Lock()
        self._last_at = 0.0

    async def wait(self) -> None:
        if self.interval_seconds <= 0:
            return
        async with self._lock:
            now = time.monotonic()
            wait_seconds = self._last_at + self.interval_seconds - now
            if wait_seconds > 0:
                await asyncio.sleep(wait_seconds)
            self._last_at = time.monotonic()


class NetworkStandby:
    def __init__(self, *, probe_interval: float = 15.0, probe_timeout: int = 15_000, max_probes: int = 40):
        self.probe_interval = max(0.0, probe_interval)
        self.probe_timeout = probe_timeout
        self.max_probes = max(1, max_probes)

    async def recover(self, page, url: str, on_message: Callable[[str], None] | None = None) -> bool:
        _notify(on_message, f"网络断联待机：{url}")
        for probe_index in range(1, self.max_probes + 1):
            if self.probe_interval > 0:
                await asyncio.sleep(self.probe_interval)
            try:
                await page.goto(url, wait_until="commit", timeout=self.probe_timeout)
                _notify(on_message, f"链接恢复：{url}")
                return True
            except Exception as exc:
                if probe_index >= self.max_probes:
                    _notify(on_message, f"链接仍不可用：{url}；{exc}")
                    return False
        return False


def _notify(callback: Callable[[str], None] | None, message: str) -> None:
    if callback is not None:
        callback(message)


async def open_login_browser(settings: Settings) -> None:
    async with async_playwright() as playwright:
        context = await launch_edge_context(playwright, settings, headless=False)
        page = await open_clean_visible_page(context, settings.start_url)
        print("已打开 Edge 登录窗口。登录和人机验证由你手动完成；程序不会点击、输入或处理验证。关闭该 Edge 窗口后命令会结束。")
        await context.wait_for_event("close", timeout=0)


async def open_verification_browser(settings: Settings, start_url: str) -> None:
    async with async_playwright() as playwright:
        context = await launch_edge_context(playwright, settings, headless=False)
        page = await open_clean_visible_page(context, start_url)
        print("已打开验证窗口。请你手动登录/处理人机验证；关闭这个标签页后会开始无头爬取。")
        try:
            await wait_for_manual_close(page)
        finally:
            try:
                await context.close()
            except Exception:
                pass


async def open_clean_visible_page(context: BrowserContext, target_url: str):
    page = await new_clean_visible_page(context)
    await page.goto(target_url, wait_until="domcontentloaded", timeout=60_000)
    return page


async def new_clean_visible_page(context: BrowserContext):
    page = await context.new_page()
    for restored_page in list(context.pages):
        if restored_page is page:
            continue
        try:
            await restored_page.close()
        except Exception:
            pass
    return page


async def wait_for_manual_close(page) -> None:
    await page.wait_for_event("close", timeout=0)


async def prepare_post_page_for_parsing(page, settings: Settings) -> None:
    settle_ms = int(max(0.0, settings.post_render_wait_seconds) * 1000)
    if settle_ms:
        await page.wait_for_timeout(settle_ms)
    await page.evaluate(
        """
        async ({ steps, delayMs }) => {
          const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
          const attrs = [
            "file",
            "zoomfile",
            "data-original",
            "data-original-src",
            "data-src",
            "data-echo",
            "data-lazy-src",
            "lazy-src"
          ];
          const images = Array.from(document.images || []);
          for (const image of images) {
            image.scrollIntoView({ block: "center", inline: "nearest" });
            await sleep(delayMs);
            const source = attrs.map((attr) => image.getAttribute(attr)).find(Boolean);
            const current = image.getAttribute("src") || "";
            if (source && (!current || current.includes("none.gif") || current.includes("loading"))) {
              image.setAttribute("src", source);
            }
          }
          const height = Math.max(document.body?.scrollHeight || 0, document.documentElement?.scrollHeight || 0);
          for (let index = 0; index < steps; index += 1) {
            window.scrollTo(0, Math.round((height * index) / Math.max(1, steps - 1)));
            await sleep(delayMs);
          }
          window.scrollTo(0, 0);
        }
        """,
        {
            "steps": max(1, settings.post_scroll_steps),
            "delayMs": int(max(0.0, settings.post_scroll_delay_seconds) * 1000),
        },
    )


async def verify_then_crawl(
    settings: Settings,
    repository: Repository,
    start_url: str | None = None,
    mirror_url: str | None = None,
) -> None:
    target_url = start_url or settings.start_url
    runtime_settings = settings_with_runtime_mirror(settings, target_url, mirror_url)
    repository.initialize()
    run_id = repository.start_crawl_run()
    repository.update_crawl_run(
        run_id,
        status="waiting",
        message="验证窗口已打开。请你完成登录/人机验证后关闭该标签页，随后会开始无头爬取。",
    )
    await open_verification_browser(runtime_settings, target_url)
    repository.update_crawl_run(run_id, status="running", message="验证标签页已关闭，开始无头爬取。")
    await ForumCrawler(runtime_settings, repository, start_url=target_url).crawl()


def settings_with_runtime_mirror(settings: Settings, start_url: str, mirror_url: str | None = None) -> Settings:
    if not mirror_url:
        return settings
    bases = [_origin(start_url), _origin(mirror_url)]
    bases.extend(settings.site_base_urls)
    seen: set[str] = set()
    unique_bases: list[str] = []
    for base_url in bases:
        if base_url and base_url not in seen:
            unique_bases.append(base_url)
            seen.add(base_url)
    return replace(settings, site_base_urls=tuple(unique_bases))


def _origin(url: str) -> str:
    parsed = urlparse(url)
    if not parsed.scheme or not parsed.netloc:
        return ""
    return urlunparse((parsed.scheme, parsed.netloc, "", "", "", ""))


class ForumCrawler:
    def __init__(self, settings: Settings, repository: Repository, start_url: str | None = None):
        self.settings = settings
        self.repository = repository
        self.start_url = start_url or settings.start_url
        self.selector_profile = load_selector_profile(settings.selector_profile_path)
        self.page_limiter = AsyncRateLimiter(settings.request_delay_seconds)
        self.image_limiter = AsyncRateLimiter(settings.image_delay_seconds)
        self.network_standby = NetworkStandby()
        self.site_router = SiteMirrorRouter(settings.site_base_urls)
        self.proxy_rotator = ClashProxyRotator.from_settings(settings)

    async def crawl(self) -> None:
        self.repository.initialize()
        self.settings.image_dir.mkdir(parents=True, exist_ok=True)
        self.settings.thumbnail_dir.mkdir(parents=True, exist_ok=True)
        ensure_edge_context_dir(self.settings)

        run_id = self.repository.start_crawl_run()
        counters = {"processed_posts": 0, "successful_posts": 0, "failed_posts": 0}
        lock = asyncio.Lock()

        try:
            async with async_playwright() as playwright:
                def report_profile_wait(message: str) -> None:
                    self.repository.update_crawl_run(run_id, status="waiting", message=message)

                context = await launch_edge_context(
                    playwright,
                    self.settings,
                    headless=True,
                    wait_for_profile=True,
                    on_wait=report_profile_wait,
                )
                self.repository.update_crawl_run(run_id, status="running", message="Edge 已关闭，开始抓取收藏页。")
                favorites = await self._collect_favorites(context)
                self.repository.update_crawl_run(run_id, total_favorites=len(favorites))
                image_semaphore = asyncio.Semaphore(self.settings.image_concurrency)
                queue: asyncio.Queue[FavoriteItem] = asyncio.Queue()
                image_queue: asyncio.Queue[ImageDownloadJob] = asyncio.Queue()
                for item in favorites:
                    queue.put_nowait(item)

                image_workers = [
                    asyncio.create_task(self._image_worker(context, image_queue, image_semaphore, worker_index))
                    for worker_index in range(1, max(1, self.settings.image_concurrency) + 1)
                ]
                workers = [
                    asyncio.create_task(
                        self._detail_worker(
                            context,
                            queue,
                            image_queue,
                            run_id,
                            counters,
                            lock,
                            worker_index,
                        )
                    )
                    for worker_index in range(1, max(1, self.settings.detail_concurrency) + 1)
                ]
                await asyncio.gather(*workers)
                await image_queue.join()
                for task in image_workers:
                    task.cancel()
                await asyncio.gather(*image_workers, return_exceptions=True)
                await context.close()

            self.repository.update_crawl_run(run_id, status="finished", finished_at=_now_for_repo())
        except Exception as exc:
            self.repository.update_crawl_run(run_id, status="failed", finished_at=_now_for_repo(), message=str(exc))
            raise

    async def _collect_favorites(self, context: BrowserContext) -> list[FavoriteItem]:
        seen_pages: set[str] = set()
        queued_pages: set[str] = {self.start_url}
        seen_items: set[str] = set()
        favorites: list[FavoriteItem] = []
        queue: asyncio.Queue[str] = asyncio.Queue()
        queue.put_nowait(self.start_url)
        lock = asyncio.Lock()
        errors: list[Exception] = []

        async def worker(worker_index: int) -> None:
            page = await context.new_page()
            tab_id = headless_tabs.register_page(page, role="收藏页", label=f"收藏页 worker {worker_index}")
            try:
                while True:
                    if _page_is_closed(page):
                        return
                    current_url = await queue.get()
                    try:
                        async with lock:
                            if current_url in seen_pages:
                                continue
                            seen_pages.add(current_url)

                        headless_tabs.update_tab(tab_id, url=current_url, status="正在加载收藏页")
                        loaded_url = await self._goto_page(
                            page,
                            current_url,
                            rate_limit=False,
                            on_message=lambda message: headless_tabs.update_tab(tab_id, url=current_url, status=message),
                        )
                        parsed = parse_favorite_page(await page.content(), loaded_url)
                        headless_tabs.update_tab(
                            tab_id,
                            url=page.url,
                            status=f"已解析收藏页：{len(parsed.items)} 项",
                        )

                        async with lock:
                            for item in parsed.items:
                                if item.url not in seen_items:
                                    seen_items.add(item.url)
                                    favorites.append(item)
                            for next_url in pending_favorite_page_urls(
                                parsed,
                                seen_pages=seen_pages,
                                queued_pages=queued_pages,
                            ):
                                queue.put_nowait(next_url)
                    except Exception as exc:
                        errors.append(exc)
                        if _page_is_closed(page):
                            return
                    finally:
                        queue.task_done()
            except asyncio.CancelledError:
                return
            finally:
                await _close_tracked_page(page, tab_id)

        workers = [
            asyncio.create_task(worker(worker_index))
            for worker_index in range(1, max(1, self.settings.favorite_page_concurrency) + 1)
        ]
        await queue.join()
        for task in workers:
            task.cancel()
        await asyncio.gather(*workers, return_exceptions=True)

        if errors and not favorites:
            raise errors[0]

        return favorites

    async def _detail_worker(
        self,
        context: BrowserContext,
        queue: asyncio.Queue[FavoriteItem],
        image_queue: asyncio.Queue[ImageDownloadJob],
        run_id: int,
        counters: dict[str, int],
        lock: asyncio.Lock,
        worker_index: int,
    ) -> None:
        page = await context.new_page()
        tab_id = headless_tabs.register_page(page, role="帖子详情", label=f"详情 worker {worker_index}")
        try:
            while True:
                if _page_is_closed(page):
                    return
                try:
                    item = queue.get_nowait()
                except asyncio.QueueEmpty:
                    return

                success = False
                try:
                    for attempt in range(self.settings.retry_count + 1):
                        try:
                            headless_tabs.update_tab(tab_id, url=item.url, status=f"正在加载帖子（第 {attempt + 1} 次）")
                            loaded_url = await self._goto_page(
                                page,
                                item.url,
                                on_message=lambda message: headless_tabs.update_tab(tab_id, url=item.url, status=message),
                            )
                            await prepare_post_page_for_parsing(page, self.settings)
                            headless_tabs.update_tab(tab_id, url=page.url, status="正在解析帖子")
                            post = parse_post_page(
                                await page.content(),
                                post_url=item.url,
                                favorite_url=self.start_url,
                                favorite_time=item.favorite_time,
                                selector_profile=self.selector_profile.selectors,
                                content_base_url=loaded_url,
                            )
                            headless_tabs.update_tab(tab_id, url=page.url, status="正在处理图片和保存数据")
                            self.repository.upsert_post(post)
                            for image in post.images:
                                image_queue.put_nowait(
                                    ImageDownloadJob(post_url=post.post_url, referer_url=loaded_url, image=image)
                                )
                            headless_tabs.update_tab(tab_id, url=page.url, status="帖子保存完成")
                            success = True
                            break
                        except Exception as exc:
                            if attempt >= self.settings.retry_count:
                                self.repository.record_error(run_id, item.url, "detail", str(exc))
                            else:
                                await asyncio.sleep(0.6 * (attempt + 1))
                    async with lock:
                        counters["processed_posts"] += 1
                        if success:
                            counters["successful_posts"] += 1
                        else:
                            counters["failed_posts"] += 1
                        self.repository.update_crawl_run(run_id, **counters)
                finally:
                    queue.task_done()
        finally:
            await _close_tracked_page(page, tab_id)

    async def _goto_page(
        self,
        page,
        url: str,
        *,
        rate_limit: bool = True,
        on_message: Callable[[str], None] | None = None,
    ) -> str:
        last_error: Exception | None = None
        recovery_attempts = max(0, self.settings.clash_recovery_attempts)
        for recovery_index in range(recovery_attempts + 1):
            for candidate_url in self.site_router.candidate_urls(url):
                if rate_limit:
                    await self.page_limiter.wait()
                try:
                    response = await page.goto(candidate_url, wait_until="domcontentloaded", timeout=60_000)
                    status = _response_status(response)
                    if status is not None and status >= 500:
                        last_error = RuntimeError(f"HTTP {status}: {candidate_url}")
                        self.site_router.mark_failure(candidate_url)
                        _notify(on_message, f"站点无响应，切换镜像：{candidate_url}")
                        continue
                    self.site_router.mark_success(candidate_url)
                    return getattr(page, "url", "") or candidate_url
                except Exception as exc:
                    if not _looks_like_network_error(str(exc)):
                        raise
                    last_error = exc
                    self.site_router.mark_failure(candidate_url)
                    _notify(on_message, f"站点无响应，切换镜像：{candidate_url}")
            if recovery_index >= recovery_attempts:
                break
            switched = await self.proxy_rotator.switch_to_available_proxy(on_message)
            wait_seconds = max(0.0, self.settings.clash_switch_wait_seconds)
            if wait_seconds > 0:
                if switched:
                    _notify(on_message, f"代理已切换，等待 {wait_seconds:g} 秒后继续")
                else:
                    _notify(on_message, f"两个镜像暂不可用，等待 {wait_seconds:g} 秒后重试")
                await asyncio.sleep(wait_seconds)
        if last_error is not None:
            raise last_error
        raise RuntimeError(f"无法打开页面：{url}")

    async def _image_worker(
        self,
        context: BrowserContext,
        image_queue: asyncio.Queue[ImageDownloadJob],
        image_semaphore: asyncio.Semaphore,
        worker_index: int,
    ) -> None:
        while True:
            try:
                job = await image_queue.get()
            except asyncio.CancelledError:
                return
            try:
                image = await self._download_image(
                    context,
                    job.post_url,
                    job.image,
                    image_semaphore,
                    referer_url=job.referer_url,
                )
                self.repository.update_post_image(job.post_url, image)
            finally:
                image_queue.task_done()

    async def _download_images(
        self,
        context: BrowserContext,
        post_url: str,
        images: Iterable[PostImage],
        semaphore: asyncio.Semaphore,
        *,
        referer_url: str | None = None,
    ) -> list[PostImage]:
        tasks = [
            asyncio.create_task(self._download_image(context, post_url, image, semaphore, referer_url=referer_url))
            for image in images
        ]
        if not tasks:
            return []
        return list(await asyncio.gather(*tasks))

    async def _download_image(
        self,
        context: BrowserContext,
        post_url: str,
        image: PostImage,
        semaphore: asyncio.Semaphore,
        *,
        referer_url: str | None = None,
    ) -> PostImage:
        async with semaphore:
            await self.image_limiter.wait()
            last_status = "failed"
            try:
                for candidate_url in self.site_router.candidate_urls(image.source_url):
                    response = await context.request.get(
                        candidate_url,
                        timeout=30_000,
                        headers=_image_request_headers(referer_url),
                    )
                    if not response.ok:
                        last_status = f"failed:{response.status}"
                        self.site_router.mark_failure(candidate_url)
                        continue
                    content = await response.body()
                    if not _looks_like_image_response(response, content):
                        last_status = "failed:not-image"
                        self.site_router.mark_failure(candidate_url)
                        continue
                    targets = image_targets(self.settings, post_url, candidate_url)
                    targets.image_path.parent.mkdir(parents=True, exist_ok=True)
                    targets.thumbnail_path.parent.mkdir(parents=True, exist_ok=True)
                    targets.image_path.write_bytes(content)
                    await asyncio.to_thread(_write_thumbnail, content, targets.thumbnail_path)
                    self.site_router.mark_success(candidate_url)
                    return replace(
                        image,
                        source_url=candidate_url,
                        local_path=targets.image_web_path,
                        thumbnail_path=targets.thumbnail_web_path,
                        download_status="downloaded",
                    )
                return replace(image, download_status=last_status)
            except Exception as exc:
                return replace(image, download_status=f"failed:{type(exc).__name__}")


def _write_thumbnail(content: bytes, path: Path) -> None:
    with Image.open(BytesIO(content)) as image:
        image.thumbnail((360, 360))
        if image.mode not in {"RGB", "L"}:
            image = image.convert("RGB")
        path.parent.mkdir(parents=True, exist_ok=True)
        image.save(path, format="JPEG", quality=85, optimize=True)


def _response_status(response) -> int | None:
    status = getattr(response, "status", None)
    return int(status) if status is not None else None


def _looks_like_network_error(message: str) -> bool:
    lowered = message.lower()
    return any(
        hint in lowered
        for hint in (
            "net::err",
            "timeout",
            "timed out",
            "econn",
            "connection",
            "proxy",
            "name_not_resolved",
            "internet_disconnected",
        )
    )


def _image_request_headers(referer_url: str | None) -> dict[str, str]:
    headers = {
        "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
    }
    if referer_url:
        headers["Referer"] = referer_url
    return headers


def _looks_like_image_response(response, content: bytes) -> bool:
    content_type = str(getattr(response, "headers", {}).get("content-type", "")).lower()
    if content_type.startswith("image/"):
        return True
    return content.startswith((b"\xff\xd8\xff", b"\x89PNG\r\n\x1a\n", b"GIF87a", b"GIF89a", b"RIFF"))


async def _close_tracked_page(page, tab_id: int) -> None:
    try:
        if not _page_is_closed(page):
            await page.close()
    except Exception:
        pass
    finally:
        headless_tabs.unregister_tab(tab_id)


def _page_is_closed(page) -> bool:
    try:
        return bool(page.is_closed())
    except Exception:
        return False


def _now_for_repo() -> str:
    from datetime import UTC, datetime

    return datetime.now(UTC).replace(microsecond=0).isoformat()
