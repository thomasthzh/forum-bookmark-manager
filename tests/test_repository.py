from forum_bookmark_manager.models import DownloadLink, DownloadStatus, ParsedPost, PostImage
from forum_bookmark_manager.repository import Repository


def make_post(
    title: str = "示例帖子",
    project_type: str = "游戏",
    post_url: str = "https://example.test/thread-1001-1-1.html",
) -> ParsedPost:
    return ParsedPost(
        post_url=post_url,
        favorite_url="https://example.test/home.php?page=1",
        title=title,
        project_type=project_type,
        favorite_time="2026-05-13",
        download_count=88,
        visit_count=1234,
        favorite_count=9,
        extract_password="abcd",
        body_text="正文",
        images=[PostImage(source_url="https://example.test/1.jpg", position=1)],
        download_links=[
            DownloadLink(
                url="https://pan.baidu.com/s/abc",
                label="主程序",
                context_text="百度网盘下载：主程序",
            )
        ],
    )


def test_repository_upserts_post_images_and_links(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()

    post_id = repo.upsert_post(make_post())
    posts = repo.list_posts()

    assert post_id == 1
    assert len(posts) == 1
    assert posts[0]["title"] == "示例帖子"
    assert posts[0]["status"] == DownloadStatus.PENDING.value
    assert posts[0]["images"][0]["source_url"] == "https://example.test/1.jpg"
    assert posts[0]["download_links"][0]["context_text"] == "百度网盘下载：主程序"


def test_repository_updates_single_post_image_after_async_download(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    repo.upsert_post(make_post())

    updated = repo.update_post_image(
        "https://example.test/thread-1001-1-1.html",
        PostImage(
            source_url="https://cdn.example.test/downloaded.webp",
            position=1,
            local_path="/media/images/downloaded.webp",
            thumbnail_path="/media/thumbnails/downloaded.jpg",
            download_status="downloaded",
        ),
    )

    image = repo.list_posts()[0]["images"][0]
    assert updated is True
    assert image["source_url"] == "https://cdn.example.test/downloaded.webp"
    assert image["local_path"] == "/media/images/downloaded.webp"
    assert image["thumbnail_path"] == "/media/thumbnails/downloaded.jpg"
    assert image["download_status"] == "downloaded"


def test_upsert_preserves_manual_status_when_recrawled(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    post_id = repo.upsert_post(make_post())
    repo.update_status(post_id, DownloadStatus.INVALID)

    repo.upsert_post(make_post(title="更新后的标题"))
    posts = repo.list_posts()

    assert posts[0]["title"] == "更新后的标题"
    assert posts[0]["status"] == DownloadStatus.INVALID.value


def test_list_posts_filters_by_type_status_search_and_sort(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    first_id = repo.upsert_post(make_post(title="游戏 A", project_type="游戏"))
    repo.update_status(first_id, DownloadStatus.INVALID)
    repo.upsert_post(
        ParsedPost(
            post_url="https://example.test/thread-2002-1-1.html",
            title="国产自拍 B",
            project_type="国产自拍",
            favorite_time="2026-05-12",
            extract_password="pass",
            body_text="备用说明",
        )
    )

    posts = repo.list_posts(project_type="游戏", status=DownloadStatus.INVALID, query="游戏", sort="old")

    assert len(posts) == 1
    assert posts[0]["title"] == "游戏 A"
    assert posts[0]["project_type"] == "游戏"
    assert posts[0]["status"] == DownloadStatus.INVALID.value


def test_crawl_run_progress_and_errors_are_recorded(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()

    run_id = repo.start_crawl_run()
    repo.update_crawl_run(run_id, total_favorites=3, processed_posts=1, successful_posts=1, failed_posts=0)
    repo.record_error(run_id, "https://example.test/bad", "detail", "timeout")
    progress = repo.latest_progress()

    assert progress["id"] == run_id
    assert progress["total_favorites"] == 3
    assert progress["processed_posts"] == 1
    assert progress["errors"][0]["message"] == "timeout"


def test_delete_post_removes_post_images_and_download_links(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    post_id = repo.upsert_post(make_post())

    repo.delete_post(post_id)

    assert repo.get_post(post_id) is None
    assert repo.list_posts() == []


def test_repository_updates_many_statuses(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    first_id = repo.upsert_post(make_post(post_url="https://example.test/thread-1.html"))
    second_id = repo.upsert_post(make_post(post_url="https://example.test/thread-2.html"))

    updated = repo.update_status_many([first_id, second_id], DownloadStatus.DOWNLOADED)

    assert updated == 2
    assert {post["status"] for post in repo.list_posts()} == {DownloadStatus.DOWNLOADED.value}


def test_repository_deletes_many_posts(tmp_path):
    repo = Repository(tmp_path / "bookmarks.sqlite3")
    repo.initialize()
    first_id = repo.upsert_post(make_post(post_url="https://example.test/thread-1.html"))
    second_id = repo.upsert_post(make_post(post_url="https://example.test/thread-2.html"))

    deleted = repo.delete_posts([first_id, second_id])

    assert deleted == 2
    assert repo.list_posts() == []
