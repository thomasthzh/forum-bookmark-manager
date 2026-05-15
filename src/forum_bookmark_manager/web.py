from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from typing import Any

from fastapi import BackgroundTasks, FastAPI, HTTPException, Query
from pydantic import BaseModel, HttpUrl
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from .clash import ClashProxyRotator
from .models import DownloadStatus, next_status
from .repository import Repository
from .selector_profile import SelectorProfile, load_selector_profile, save_selector_profile
from .settings import load_settings
from .tab_registry import headless_tabs


CrawlerCallback = Callable[[str, str | None], None]
AnnotateCallback = Callable[[str], None]


class CrawlRequest(BaseModel):
    mode: str
    start_url: HttpUrl
    mirror_url: HttpUrl | None = None


class AnnotateRequest(BaseModel):
    target_url: HttpUrl


class BulkStatusRequest(BaseModel):
    ids: list[int]
    status: str


class BulkIdsRequest(BaseModel):
    ids: list[int]


class SelectorProfileRequest(BaseModel):
    sample_url: HttpUrl | None = None
    selectors: dict[str, str]


def create_app(
    repo: Repository | None = None,
    crawl_callback: CrawlerCallback | None = None,
    annotate_callback: AnnotateCallback | None = None,
) -> FastAPI:
    settings = load_settings()
    repository = repo or Repository(settings.database_path)
    repository.initialize()

    app = FastAPI(title="Forum Bookmark Manager")
    static_dir = Path(__file__).with_name("static")
    settings.image_dir.mkdir(parents=True, exist_ok=True)
    settings.thumbnail_dir.mkdir(parents=True, exist_ok=True)
    app.mount("/static", StaticFiles(directory=static_dir), name="static")
    app.mount("/media/images", StaticFiles(directory=settings.image_dir), name="images")
    app.mount("/media/thumbnails", StaticFiles(directory=settings.thumbnail_dir), name="thumbnails")

    @app.get("/")
    def index() -> FileResponse:
        return FileResponse(static_dir / "index.html")

    @app.get("/api/posts")
    def posts(
        type_filter: str | None = Query(None, alias="type"),
        status: str | None = None,
        q: str | None = None,
        sort: str = "new",
    ) -> dict[str, Any]:
        items = repository.list_posts(project_type=type_filter, status=status, query=q, sort=sort)
        return {"total": len(items), "items": items}

    @app.post("/api/posts/bulk-status")
    def bulk_status(request: BulkStatusRequest) -> dict[str, Any]:
        if not request.ids:
            raise HTTPException(status_code=400, detail="No posts selected")
        try:
            status_value = DownloadStatus(request.status)
        except ValueError as exc:
            raise HTTPException(status_code=400, detail="Unsupported status") from exc
        updated = repository.update_status_many(request.ids, status_value)
        return {"updated": updated, "ids": request.ids, "status": status_value.value}

    @app.post("/api/posts/bulk-delete")
    def bulk_delete(request: BulkIdsRequest) -> dict[str, Any]:
        if not request.ids:
            raise HTTPException(status_code=400, detail="No posts selected")
        for post_id in request.ids:
            post = repository.get_post(post_id)
            if post:
                _delete_media_files(post, settings.image_dir, settings.thumbnail_dir)
        deleted = repository.delete_posts(request.ids)
        return {"deleted": deleted, "ids": request.ids}

    @app.post("/api/posts/{post_id}/cycle-status")
    def cycle_status(post_id: int) -> dict[str, Any]:
        post = repository.get_post(post_id)
        if not post:
            raise HTTPException(status_code=404, detail="Post not found")
        status_value = next_status(DownloadStatus(post["status"]))
        repository.update_status(post_id, status_value)
        return {"id": post_id, "status": status_value.value}

    @app.delete("/api/posts/{post_id}")
    def delete_post(post_id: int) -> dict[str, Any]:
        post = repository.get_post(post_id)
        if not post:
            raise HTTPException(status_code=404, detail="Post not found")
        _delete_media_files(post, settings.image_dir, settings.thumbnail_dir)
        repository.delete_post(post_id)
        return {"deleted": True, "id": post_id}

    @app.get("/api/types")
    def types() -> dict[str, list[str]]:
        return {"types": repository.known_types()}

    @app.get("/api/progress")
    def progress() -> dict[str, Any]:
        return repository.latest_progress()

    @app.get("/api/headless-tabs")
    def get_headless_tabs() -> dict[str, Any]:
        return headless_tabs.snapshot()

    @app.post("/api/headless-tabs/{tab_id}/close")
    def close_headless_tab(tab_id: int) -> dict[str, Any]:
        if not headless_tabs.close_tab(tab_id):
            raise HTTPException(status_code=404, detail="Headless tab not found")
        return {"closed": True, "id": tab_id}

    @app.post("/api/headless-tabs/close-all")
    def close_all_headless_tabs() -> dict[str, Any]:
        return {"closed": headless_tabs.close_all()}

    @app.post("/api/headless-tabs/{tab_id}/open")
    def open_headless_tab(tab_id: int) -> dict[str, Any]:
        if not headless_tabs.open_visible(tab_id):
            raise HTTPException(status_code=404, detail="Headless tab not found or has no URL")
        return {"opened": True, "id": tab_id}

    @app.post("/api/crawl")
    def crawl(request: CrawlRequest, background_tasks: BackgroundTasks) -> dict[str, Any]:
        if request.mode != "forum_favorites":
            raise HTTPException(status_code=400, detail="Unsupported crawl mode")
        if crawl_callback is None:
            return {
                "started": False,
                "message": "请在终端运行 python -m forum_bookmark_manager.cli crawl，或用 CLI 启动带爬虫的服务。",
            }
        start_url = str(request.start_url)
        mirror_url = str(request.mirror_url) if request.mirror_url else None
        background_tasks.add_task(crawl_callback, start_url, mirror_url)
        return {
            "started": True,
            "message": "论坛收藏抓取任务已启动",
            "start_url": start_url,
            "mirror_url": mirror_url,
        }

    @app.get("/api/clash")
    async def clash_status() -> dict[str, Any]:
        return await ClashProxyRotator.from_settings(settings).status()

    @app.post("/api/clash/switch")
    async def clash_switch() -> dict[str, Any]:
        messages: list[str] = []
        rotator = ClashProxyRotator.from_settings(settings)
        switched = await rotator.switch_to_available_proxy(messages.append)
        return {"switched": switched, "messages": messages, "status": await rotator.status()}

    @app.get("/api/selector-profile")
    def get_selector_profile() -> dict[str, Any]:
        return load_selector_profile(settings.selector_profile_path).to_payload()

    @app.post("/api/selector-profile")
    def post_selector_profile(request: SelectorProfileRequest) -> dict[str, Any]:
        profile = SelectorProfile.from_payload(request.model_dump(mode="json"))
        save_selector_profile(settings.selector_profile_path, profile)
        return {"saved": True, **profile.to_payload()}

    @app.post("/api/annotate")
    def annotate(request: AnnotateRequest, background_tasks: BackgroundTasks) -> dict[str, Any]:
        if annotate_callback is None:
            return {
                "started": False,
                "message": "请用 CLI 启动带标注器的服务，或运行 python -m forum_bookmark_manager.cli annotate <帖子链接>。",
            }
        target_url = str(request.target_url)
        background_tasks.add_task(annotate_callback, target_url)
        return {"started": True, "message": "标注窗口已启动；请在新窗口里点选区域并保存。", "target_url": target_url}

    return app


def _delete_media_files(post: dict[str, Any], image_dir: Path, thumbnail_dir: Path) -> None:
    for image in post.get("images", []):
        _delete_web_path(image.get("local_path"), "/media/images/", image_dir)
        _delete_web_path(image.get("thumbnail_path"), "/media/thumbnails/", thumbnail_dir)


def _delete_web_path(value: str | None, prefix: str, root: Path) -> None:
    if not value or not value.startswith(prefix):
        return
    filename = value.removeprefix(prefix)
    if "/" in filename or "\\" in filename or not filename:
        return
    path = root / filename
    try:
        if path.resolve().parent == root.resolve() and path.exists():
            path.unlink()
    except OSError:
        return
