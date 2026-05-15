from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from itertools import count
from threading import Lock
from typing import Any
import webbrowser


@dataclass
class TrackedTab:
    id: int
    role: str
    label: str
    url: str
    status: str
    created_at: str
    updated_at: str
    page: Any
    loop: asyncio.AbstractEventLoop | None


class TabRegistry:
    def __init__(self, open_url: Callable[[str], object] | None = None):
        self._open_url = open_url or webbrowser.open
        self._ids = count(1)
        self._tabs: dict[int, TrackedTab] = {}
        self._lock = Lock()

    def register_page(self, page: Any, *, role: str, label: str) -> int:
        tab_id = next(self._ids)
        now = _now()
        try:
            loop = asyncio.get_running_loop()
        except RuntimeError:
            loop = None
        with self._lock:
            self._tabs[tab_id] = TrackedTab(
                id=tab_id,
                role=role,
                label=label,
                url=getattr(page, "url", "") or "about:blank",
                status="已打开",
                created_at=now,
                updated_at=now,
                page=page,
                loop=loop,
            )
        return tab_id

    def update_tab(
        self,
        tab_id: int,
        *,
        url: str | None = None,
        status: str | None = None,
    ) -> None:
        now = _now()
        with self._lock:
            tab = self._tabs.get(tab_id)
            if tab is None:
                return
            if url is not None:
                tab.url = url
            if status is not None:
                tab.status = status
            tab.updated_at = now

    def unregister_tab(self, tab_id: int) -> None:
        with self._lock:
            self._tabs.pop(tab_id, None)

    def snapshot(self) -> dict[str, Any]:
        tabs: list[dict[str, Any]] = []
        closed_ids: list[int] = []
        with self._lock:
            for tab_id, tab in self._tabs.items():
                if _is_page_closed(tab.page):
                    closed_ids.append(tab_id)
                    continue
                tabs.append(
                    {
                        "id": tab.id,
                        "role": tab.role,
                        "label": tab.label,
                        "url": tab.url,
                        "status": tab.status,
                        "created_at": tab.created_at,
                        "updated_at": tab.updated_at,
                    }
                )
            for tab_id in closed_ids:
                self._tabs.pop(tab_id, None)
        tabs.sort(key=lambda item: item["id"])
        return {"total": len(tabs), "tabs": tabs}

    def open_visible(self, tab_id: int) -> bool:
        with self._lock:
            tab = self._tabs.get(tab_id)
            url = tab.url if tab else ""
        if not tab or not url or url == "about:blank":
            return False
        self._open_url(url)
        return True

    def close_tab(self, tab_id: int) -> bool:
        with self._lock:
            tab = self._tabs.get(tab_id)
            if tab is None:
                return False
            tab.status = "正在关闭"
            tab.updated_at = _now()
            page = tab.page
            loop = tab.loop

        async def close_page() -> None:
            try:
                if not _is_page_closed(page):
                    await page.close()
            finally:
                self.unregister_tab(tab_id)

        try:
            current_loop = asyncio.get_running_loop()
        except RuntimeError:
            current_loop = None
        if loop is not None and loop.is_running():
            if current_loop is loop:
                asyncio.create_task(close_page())
            else:
                loop.call_soon_threadsafe(lambda: asyncio.create_task(close_page()))
        elif current_loop is not None:
            current_loop.create_task(close_page())
        else:
            asyncio.run(close_page())
        return True

    def close_all(self) -> int:
        with self._lock:
            tab_ids = list(self._tabs)
        closed = 0
        for tab_id in tab_ids:
            if self.close_tab(tab_id):
                closed += 1
        return closed

    def clear(self) -> None:
        with self._lock:
            self._tabs.clear()


def _is_page_closed(page: Any) -> bool:
    try:
        return bool(page.is_closed())
    except Exception:
        return False


def _now() -> str:
    return datetime.now(UTC).replace(microsecond=0).isoformat()


headless_tabs = TabRegistry()
