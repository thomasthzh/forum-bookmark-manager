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
edge_profile_mode = "managed"
favorite_page_concurrency = 6
edge_profile_wait_seconds = 120
selector_profile_path = "data/selector-profile.json"
image_dir = "data/images"
thumbnail_dir = "data/thumbnails"
""",
        encoding="utf-8",
    )

    settings = load_settings(config)

    assert settings.start_url == "https://example.test/favorites"
    assert settings.detail_concurrency == 12
    assert settings.image_concurrency == 16
    assert settings.favorite_page_concurrency == 6
    assert settings.edge_profile_wait_seconds == 120
    assert settings.edge_profile_mode == "managed"
    assert settings.database_path == Path("data/bookmarks.sqlite3")
    assert settings.selector_profile_path == Path("data/selector-profile.json")


def test_settings_supports_real_edge_profile_paths(tmp_path):
    user_data_dir = tmp_path / "Microsoft" / "Edge" / "User Data"
    config = tmp_path / "settings.toml"
    config.write_text(
        f"""
start_url = "https://example.test/favorites"
database_path = "data/bookmarks.sqlite3"
edge_profile_mode = "system"
edge_user_data_dir = "{user_data_dir.as_posix()}"
edge_profile_directory = "Default"
""",
        encoding="utf-8",
    )

    settings = load_settings(config)

    assert settings.edge_profile_mode == "system"
    assert settings.edge_user_data_dir == user_data_dir
    assert settings.edge_profile_directory == "Default"
