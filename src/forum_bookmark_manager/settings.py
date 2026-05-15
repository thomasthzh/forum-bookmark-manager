from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import tomllib


@dataclass(frozen=True)
class Settings:
    start_url: str
    detail_concurrency: int
    image_concurrency: int
    retry_count: int
    database_path: Path
    edge_profile_dir: Path
    image_dir: Path
    thumbnail_dir: Path
    selector_profile_path: Path = Path("data/selector-profile.json")
    favorite_page_concurrency: int = 8
    edge_profile_wait_seconds: int = 600
    edge_profile_mode: str = "managed"
    edge_user_data_dir: Path | None = None
    edge_profile_directory: str = "Default"
    proxy_server: str | None = None
    request_delay_seconds: float = 1.0
    image_delay_seconds: float = 0.25
    post_render_wait_seconds: float = 1.5
    post_scroll_steps: int = 6
    post_scroll_delay_seconds: float = 0.25
    site_base_urls: tuple[str, ...] = ("https://primary.example.test", "https://mirror.example.test")
    clash_controller_url: str | None = None
    clash_controller_secret: str | None = None
    clash_config_path: Path | None = None
    clash_proxy_group: str = "节点选择"
    clash_region_keywords: tuple[str, ...] = ("香港", "HK", "Hong", "新加坡", "SG", "Singapore", "狮城")
    clash_delay_test_url: str = "https://www.gstatic.com/generate_204"
    clash_delay_timeout_ms: int = 5000
    clash_switch_wait_seconds: float = 10.0
    clash_recovery_attempts: int = 3


DEFAULT_SETTINGS = {
    "start_url": "https://primary.example.test/home.php?mod=space&do=favorite&type=all&page=1",
    "detail_concurrency": 27,
    "image_concurrency": 35,
    "favorite_page_concurrency": 11,
    "edge_profile_wait_seconds": 600,
    "retry_count": 2,
    "database_path": "data/bookmarks.sqlite3",
    "edge_profile_dir": "data/edge-profile",
    "edge_profile_mode": "managed",
    "edge_user_data_dir": "",
    "edge_profile_directory": "Default",
    "proxy_server": "",
    "request_delay_seconds": 1.0,
    "image_delay_seconds": 0.25,
    "post_render_wait_seconds": 1.5,
    "post_scroll_steps": 6,
    "post_scroll_delay_seconds": 0.25,
    "site_base_urls": ["https://primary.example.test", "https://mirror.example.test"],
    "clash_controller_url": "",
    "clash_controller_secret": "",
    "clash_config_path": "",
    "clash_proxy_group": "节点选择",
    "clash_region_keywords": ["香港", "HK", "Hong", "新加坡", "SG", "Singapore", "狮城"],
    "clash_delay_test_url": "https://www.gstatic.com/generate_204",
    "clash_delay_timeout_ms": 5000,
    "clash_switch_wait_seconds": 10.0,
    "clash_recovery_attempts": 3,
    "image_dir": "data/images",
    "thumbnail_dir": "data/thumbnails",
    "selector_profile_path": "data/selector-profile.json",
}


def load_settings(path: str | Path = "config/settings.toml") -> Settings:
    config_path = Path(path)
    data = DEFAULT_SETTINGS.copy()
    if config_path.exists():
        with config_path.open("rb") as handle:
            data.update(tomllib.load(handle))

    edge_profile_mode = str(data["edge_profile_mode"]).lower()
    edge_user_data_dir = _optional_path(data.get("edge_user_data_dir"))
    if edge_profile_mode == "system" and edge_user_data_dir is None:
        edge_user_data_dir = default_edge_user_data_dir()

    return Settings(
        start_url=str(data["start_url"]),
        detail_concurrency=int(data["detail_concurrency"]),
        image_concurrency=int(data["image_concurrency"]),
        favorite_page_concurrency=int(data["favorite_page_concurrency"]),
        retry_count=int(data["retry_count"]),
        database_path=_path(data["database_path"]),
        edge_profile_dir=_path(data["edge_profile_dir"]),
        image_dir=_path(data["image_dir"]),
        thumbnail_dir=_path(data["thumbnail_dir"]),
        selector_profile_path=_path(data["selector_profile_path"]),
        edge_profile_mode=edge_profile_mode,
        edge_user_data_dir=edge_user_data_dir,
        edge_profile_directory=str(data["edge_profile_directory"]),
        edge_profile_wait_seconds=int(data["edge_profile_wait_seconds"]),
        proxy_server=_optional_string(data.get("proxy_server")),
        request_delay_seconds=float(data["request_delay_seconds"]),
        image_delay_seconds=float(data["image_delay_seconds"]),
        post_render_wait_seconds=float(data["post_render_wait_seconds"]),
        post_scroll_steps=int(data["post_scroll_steps"]),
        post_scroll_delay_seconds=float(data["post_scroll_delay_seconds"]),
        site_base_urls=_string_tuple(data.get("site_base_urls")),
        clash_controller_url=_optional_string(data.get("clash_controller_url")),
        clash_controller_secret=_optional_string(data.get("clash_controller_secret")),
        clash_config_path=_optional_path(data.get("clash_config_path")),
        clash_proxy_group=str(data["clash_proxy_group"]),
        clash_region_keywords=_string_tuple(data.get("clash_region_keywords")),
        clash_delay_test_url=str(data["clash_delay_test_url"]),
        clash_delay_timeout_ms=int(data["clash_delay_timeout_ms"]),
        clash_switch_wait_seconds=float(data["clash_switch_wait_seconds"]),
        clash_recovery_attempts=int(data["clash_recovery_attempts"]),
    )


def default_edge_user_data_dir() -> Path:
    if os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        return Path(os.environ["LOCALAPPDATA"]) / "Microsoft" / "Edge" / "User Data"
    return Path.home() / ".config" / "microsoft-edge"


def _path(value: object) -> Path:
    return Path(os.path.expandvars(os.path.expanduser(str(value))))


def _optional_path(value: object) -> Path | None:
    if value is None or str(value).strip() == "":
        return None
    return _path(value)


def _optional_string(value: object) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _string_tuple(value: object) -> tuple[str, ...]:
    if value is None:
        return ()
    if isinstance(value, (list, tuple)):
        return tuple(str(item).strip() for item in value if str(item).strip())
    text = str(value).strip()
    if not text:
        return ()
    return tuple(part.strip() for part in text.split(",") if part.strip())
