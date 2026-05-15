from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
from typing import Any


SELECTOR_FIELDS = {
    "title": "标题",
    "body": "正文主内容",
    "images": "图片区域",
    "password": "解压码区域",
    "download_links": "下载链接区域",
    "project_type": "分类/面包屑",
    "download_count": "下载次数",
    "visit_count": "访问次数",
    "favorite_count": "收藏次数",
}


@dataclass(frozen=True)
class SelectorProfile:
    selectors: dict[str, str]
    sample_url: str | None = None

    @classmethod
    def from_payload(cls, payload: dict[str, Any]) -> "SelectorProfile":
        selectors = {
            key: str(value).strip()
            for key, value in dict(payload.get("selectors", {})).items()
            if key in SELECTOR_FIELDS and str(value).strip()
        }
        sample_url = payload.get("sample_url")
        return cls(selectors=selectors, sample_url=str(sample_url) if sample_url else None)

    def to_payload(self) -> dict[str, Any]:
        return {"sample_url": self.sample_url, "selectors": self.selectors}


def load_selector_profile(path: str | Path) -> SelectorProfile:
    profile_path = Path(path)
    if not profile_path.exists():
        return SelectorProfile(selectors={})
    with profile_path.open("r", encoding="utf-8") as handle:
        return SelectorProfile.from_payload(json.load(handle))


def save_selector_profile(path: str | Path, profile: SelectorProfile) -> None:
    profile_path = Path(path)
    profile_path.parent.mkdir(parents=True, exist_ok=True)
    with profile_path.open("w", encoding="utf-8") as handle:
        json.dump(profile.to_payload(), handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")
