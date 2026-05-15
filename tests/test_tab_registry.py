import asyncio

from forum_bookmark_manager.tab_registry import TabRegistry


class FakePage:
    def __init__(self, url: str = "about:blank"):
        self.url = url
        self.closed = False

    def is_closed(self) -> bool:
        return self.closed

    async def close(self) -> None:
        self.closed = True


def test_tab_registry_tracks_and_opens_visible_url():
    opened_urls: list[str] = []
    registry = TabRegistry(open_url=opened_urls.append)
    page = FakePage()

    tab_id = registry.register_page(page, role="帖子详情", label="worker-1")
    registry.update_tab(tab_id, url="https://example.test/thread-1.html", status="加载完成")

    snapshot = registry.snapshot()
    assert snapshot["total"] == 1
    assert snapshot["tabs"][0]["id"] == tab_id
    assert snapshot["tabs"][0]["role"] == "帖子详情"
    assert snapshot["tabs"][0]["url"] == "https://example.test/thread-1.html"
    assert registry.open_visible(tab_id) is True
    assert opened_urls == ["https://example.test/thread-1.html"]


def test_tab_registry_closes_page_on_own_event_loop():
    async def scenario() -> None:
        registry = TabRegistry()
        page = FakePage()
        tab_id = registry.register_page(page, role="收藏页", label="favorite-worker")

        assert registry.close_tab(tab_id) is True
        await asyncio.sleep(0)

        assert page.closed is True
        assert registry.snapshot()["total"] == 0

    asyncio.run(scenario())


def test_tab_registry_closes_all_pages():
    registry = TabRegistry()
    first = FakePage("https://example.test/1")
    second = FakePage("https://example.test/2")
    registry.register_page(first, role="帖子详情", label="worker-1")
    registry.register_page(second, role="收藏页", label="worker-2")

    closed = registry.close_all()

    assert closed == 2
    assert first.closed is True
    assert second.closed is True
    assert registry.snapshot()["total"] == 0
