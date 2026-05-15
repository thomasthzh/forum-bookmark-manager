import asyncio

from forum_bookmark_manager.annotator import _annotation_script, prepare_annotation_page


def test_prepare_annotation_page_injects_before_navigation():
    context = FakeAnnotationContext([FakeAnnotationPage("https://old.example/")])

    page = asyncio.run(prepare_annotation_page(context, "https://example.test/thread-1.html", lambda payload: payload))

    assert context.restored_pages[0].closed is True
    assert page.calls[:3] == [
        "expose:forumBookmarkSaveSelectorProfile",
        "init-script",
        "goto:https://example.test/thread-1.html:commit",
    ]
    assert "script-tag" in page.calls


def test_prepare_annotation_page_keeps_panel_available_when_navigation_aborts():
    context = FakeAnnotationContext([])
    context.next_page.goto_error = RuntimeError("Page.goto: net::ERR_ABORTED; maybe frame was detached")

    page = asyncio.run(prepare_annotation_page(context, "https://example.test/thread-1.html", lambda payload: payload))

    assert page.goto_attempted is True
    assert "script-tag" in page.calls


def test_annotation_script_waits_for_document_root_before_marking_enabled():
    script = _annotation_script()

    assert "function boot()" in script
    assert "if (!document.documentElement)" in script
    assert script.index("if (!document.documentElement)") < script.index("window.__forumBookmarkAnnotator = true")


class FakeAnnotationContext:
    def __init__(self, restored_pages):
        self.restored_pages = restored_pages
        self.next_page = FakeAnnotationPage("about:blank")
        self.pages = [*restored_pages]

    async def new_page(self):
        self.pages.append(self.next_page)
        return self.next_page


class FakeAnnotationPage:
    def __init__(self, url):
        self.url = url
        self.calls = []
        self.closed = False
        self.goto_attempted = False
        self.goto_error = None

    async def expose_function(self, name, _callback):
        self.calls.append(f"expose:{name}")

    async def add_init_script(self, _script):
        self.calls.append("init-script")

    async def add_script_tag(self, **_kwargs):
        self.calls.append("script-tag")

    async def goto(self, url, **kwargs):
        self.goto_attempted = True
        wait_until = kwargs.get("wait_until")
        self.calls.append(f"goto:{url}:{wait_until}")
        if self.goto_error:
            raise self.goto_error
        self.url = url

    async def close(self):
        self.closed = True
