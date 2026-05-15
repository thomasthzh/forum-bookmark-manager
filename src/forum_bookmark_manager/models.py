from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum


class DownloadStatus(StrEnum):
    PENDING = "未下载"
    DOWNLOADED = "已下载"
    INVALID = "链接失效"


STATUS_CYCLE = (
    DownloadStatus.PENDING,
    DownloadStatus.DOWNLOADED,
    DownloadStatus.INVALID,
)


def next_status(status: DownloadStatus | str) -> DownloadStatus:
    current = DownloadStatus(status)
    index = STATUS_CYCLE.index(current)
    return STATUS_CYCLE[(index + 1) % len(STATUS_CYCLE)]


@dataclass(frozen=True)
class DownloadLink:
    url: str
    label: str = ""
    context_text: str = ""


@dataclass(frozen=True)
class PostImage:
    source_url: str
    position: int
    local_path: str | None = None
    thumbnail_path: str | None = None
    download_status: str = "pending"


@dataclass
class ParsedPost:
    post_url: str
    title: str
    project_type: str = "未分类"
    favorite_url: str | None = None
    favorite_time: str | None = None
    download_count: int | None = None
    visit_count: int | None = None
    favorite_count: int | None = None
    extract_password: str | None = None
    body_text: str | None = None
    images: list[PostImage] = field(default_factory=list)
    download_links: list[DownloadLink] = field(default_factory=list)


@dataclass(frozen=True)
class FavoriteItem:
    url: str
    favorite_time: str | None = None
    title: str | None = None
