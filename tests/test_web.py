from fastapi.testclient import TestClient

from forum_bookmark_manager.models import DownloadStatus, ParsedPost, PostImage
from forum_bookmark_manager.repository import Repository
from forum_bookmark_manager.tab_registry import headless_tabs
from forum_bookmark_manager.web import create_app


def seeded_client(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    game_id = repo.upsert_post(
        ParsedPost(
            post_url="https://example.test/thread-game.html",
            title="游戏项目",
            project_type="游戏",
            favorite_time="2026-05-13",
            extract_password="gamepass",
            body_text="游戏正文",
        )
    )
    repo.update_status(game_id, DownloadStatus.INVALID)
    repo.upsert_post(
        ParsedPost(
            post_url="https://example.test/thread-selfie.html",
            title="国产自拍项目",
            project_type="国产自拍",
            favorite_time="2026-05-12",
            body_text="自拍正文",
        )
    )
    return TestClient(create_app(repo)), repo, game_id


def test_api_posts_supports_combined_type_status_and_search(tmp_path):
    client, _, _ = seeded_client(tmp_path)

    response = client.get(
        "/api/posts",
        params={"type": "游戏", "status": "链接失效", "q": "gamepass", "sort": "new"},
    )

    assert response.status_code == 200
    data = response.json()
    assert data["total"] == 1
    assert data["items"][0]["title"] == "游戏项目"
    assert data["items"][0]["status"] == "链接失效"


def test_api_cycle_status_persists_next_manual_state(tmp_path):
    client, repo, game_id = seeded_client(tmp_path)

    response = client.post(f"/api/posts/{game_id}/cycle-status")

    assert response.status_code == 200
    assert response.json()["status"] == "未下载"
    assert repo.list_posts(project_type="游戏")[0]["status"] == "未下载"


def test_api_delete_post_removes_item_from_database(tmp_path):
    client, repo, game_id = seeded_client(tmp_path)

    response = client.delete(f"/api/posts/{game_id}")

    assert response.status_code == 200
    assert response.json() == {"deleted": True, "id": game_id}
    assert repo.get_post(game_id) is None


def test_api_bulk_status_updates_selected_posts(tmp_path):
    client, repo, _ = seeded_client(tmp_path)
    post_ids = [post["id"] for post in repo.list_posts()]

    response = client.post(
        "/api/posts/bulk-status",
        json={"ids": post_ids, "status": DownloadStatus.DOWNLOADED.value},
    )

    assert response.status_code == 200
    assert response.json() == {"updated": 2, "ids": post_ids, "status": DownloadStatus.DOWNLOADED.value}
    assert {post["status"] for post in repo.list_posts()} == {DownloadStatus.DOWNLOADED.value}


def test_api_bulk_delete_removes_selected_posts(tmp_path):
    client, repo, _ = seeded_client(tmp_path)
    post_ids = [post["id"] for post in repo.list_posts()]

    response = client.post("/api/posts/bulk-delete", json={"ids": post_ids})

    assert response.status_code == 200
    assert response.json() == {"deleted": 2, "ids": post_ids}
    assert repo.list_posts() == []


def test_api_delete_post_removes_local_image_files(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    image_dir = tmp_path / "data" / "images"
    thumb_dir = tmp_path / "data" / "thumbnails"
    image_dir.mkdir(parents=True)
    thumb_dir.mkdir(parents=True)
    image_file = image_dir / "one.jpg"
    thumb_file = thumb_dir / "one.jpg"
    image_file.write_bytes(b"image")
    thumb_file.write_bytes(b"thumb")
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    post_id = repo.upsert_post(
        ParsedPost(
            post_url="https://example.test/thread-image.html",
            title="带图帖子",
            images=[
                PostImage(
                    source_url="https://example.test/one.jpg",
                    position=1,
                    local_path="/media/images/one.jpg",
                    thumbnail_path="/media/thumbnails/one.jpg",
                    download_status="downloaded",
                )
            ],
        )
    )
    client = TestClient(create_app(repo))

    response = client.delete(f"/api/posts/{post_id}")

    assert response.status_code == 200
    assert not image_file.exists()
    assert not thumb_file.exists()


def test_api_close_all_headless_tabs(tmp_path):
    headless_tabs.clear()
    headless_tabs.register_page(FakeHeadlessPage("https://example.test/1"), role="帖子详情", label="worker-1")
    headless_tabs.register_page(FakeHeadlessPage("https://example.test/2"), role="收藏页", label="worker-2")
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    client = TestClient(create_app(repo))

    response = client.post("/api/headless-tabs/close-all")

    assert response.status_code == 200
    assert response.json() == {"closed": 2}
    assert headless_tabs.snapshot()["total"] == 0
    headless_tabs.clear()


def test_api_crawl_forum_favorites_passes_user_start_url(tmp_path):
    requested_urls = []

    def crawl_callback(start_url: str, mirror_url: str | None = None) -> None:
        requested_urls.append((start_url, mirror_url))

    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    client = TestClient(create_app(repo, crawl_callback=crawl_callback))
    start_url = "https://example.test/home.php?mod=space&do=favorite&page=1"

    response = client.post(
        "/api/crawl",
        json={
            "mode": "forum_favorites",
            "start_url": start_url,
            "mirror_url": "https://mirror.example.test",
        },
    )

    assert response.status_code == 200
    assert response.json()["started"] is True
    assert requested_urls == [(start_url, "https://mirror.example.test/")]


def test_api_clash_status_exposes_controller_and_current_proxy(tmp_path, monkeypatch):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()

    class FakeRotator:
        async def status(self):
            return {
                "configured": True,
                "controller_url": "http://127.0.0.1:9097",
                "proxy_group": "节点选择",
                "current": "香港HK-A",
                "reachable": True,
                "message": "connected",
            }

    monkeypatch.setattr("forum_bookmark_manager.web.ClashProxyRotator.from_settings", lambda _settings: FakeRotator())
    client = TestClient(create_app(repo))

    response = client.get("/api/clash")

    assert response.status_code == 200
    assert response.json()["current"] == "香港HK-A"


def test_api_clash_switch_uses_rotator(tmp_path, monkeypatch):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    messages = []

    class FakeRotator:
        async def switch_to_available_proxy(self, on_message=None):
            on_message("switched")
            return True

        async def status(self):
            return {"configured": True, "reachable": True, "current": "新加坡SG"}

    monkeypatch.setattr("forum_bookmark_manager.web.ClashProxyRotator.from_settings", lambda _settings: FakeRotator())
    client = TestClient(create_app(repo))

    response = client.post("/api/clash/switch")

    assert response.status_code == 200
    data = response.json()
    assert data["switched"] is True
    assert data["messages"] == ["switched"]
    assert data["status"]["current"] == "新加坡SG"


def test_api_selector_profile_can_be_saved_and_loaded(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    client = TestClient(create_app(repo))
    payload = {
        "sample_url": "https://example.test/thread-1.html",
        "selectors": {
            "title": "#thread_subject",
            "body": "td.t_f",
            "images": ".post-images",
            "password": ".secret",
            "download_links": ".downloads",
        },
    }

    save_response = client.post("/api/selector-profile", json=payload)
    load_response = client.get("/api/selector-profile")

    assert save_response.status_code == 200
    assert save_response.json()["saved"] is True
    assert load_response.status_code == 200
    assert load_response.json() == payload


def test_api_annotate_passes_target_url_to_callback(tmp_path):
    requested_urls = []

    def annotate_callback(target_url: str) -> None:
        requested_urls.append(target_url)

    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    client = TestClient(create_app(repo, annotate_callback=annotate_callback))

    response = client.post(
        "/api/annotate",
        json={"target_url": "https://example.test/thread-1.html"},
    )

    assert response.status_code == 200
    assert response.json()["started"] is True
    assert requested_urls == ["https://example.test/thread-1.html"]


def test_api_types_and_progress(tmp_path):
    client, repo, _ = seeded_client(tmp_path)
    run_id = repo.start_crawl_run()
    repo.update_crawl_run(run_id, total_favorites=2, processed_posts=1)

    assert client.get("/api/types").json()["types"] == ["国产自拍", "游戏"]
    progress = client.get("/api/progress").json()
    assert progress["total_favorites"] == 2
    assert progress["processed_posts"] == 1


class FakeHeadlessPage:
    def __init__(self, url):
        self.url = url
        self.closed = False

    def is_closed(self):
        return self.closed

    async def close(self):
        self.closed = True
