from __future__ import annotations

from dataclasses import dataclass
from urllib.parse import urlparse, urlunparse


@dataclass(frozen=True)
class MirrorBase:
    scheme: str
    netloc: str


class SiteMirrorRouter:
    def __init__(self, base_urls: tuple[str, ...] | list[str]):
        self._bases = tuple(_parse_base(url) for url in base_urls if str(url).strip())
        self._active_index = 0

    @property
    def base_urls(self) -> tuple[str, ...]:
        return tuple(f"{base.scheme}://{base.netloc}" for base in self._bases)

    def candidate_urls(self, url: str) -> list[str]:
        if len(self._bases) < 2:
            return [url]
        parsed = urlparse(url)
        match_index = self._match_index(parsed.netloc)
        if match_index is None:
            return [url]

        ordered_indexes = [self._active_index]
        ordered_indexes.extend(index for index in range(len(self._bases)) if index != self._active_index)
        candidates: list[str] = []
        seen: set[str] = set()
        for index in ordered_indexes:
            base = self._bases[index]
            candidate = urlunparse(parsed._replace(scheme=base.scheme, netloc=base.netloc))
            if candidate not in seen:
                candidates.append(candidate)
                seen.add(candidate)
        return candidates

    def mark_success(self, url: str) -> None:
        index = self._match_index(urlparse(url).netloc)
        if index is not None:
            self._active_index = index

    def mark_failure(self, url: str) -> None:
        index = self._match_index(urlparse(url).netloc)
        if index is not None and index == self._active_index and self._bases:
            self._active_index = (self._active_index + 1) % len(self._bases)

    def equivalent_key(self, url: str) -> str:
        parsed = urlparse(url)
        if self._match_index(parsed.netloc) is None:
            return url
        return urlunparse(parsed._replace(scheme="", netloc="{mirror}"))

    def _match_index(self, netloc: str) -> int | None:
        lowered = netloc.lower()
        for index, base in enumerate(self._bases):
            if lowered == base.netloc.lower():
                return index
        return None


def _parse_base(url: str) -> MirrorBase:
    parsed = urlparse(str(url).strip())
    if not parsed.scheme or not parsed.netloc:
        parsed = urlparse(f"https://{str(url).strip().strip('/')}")
    return MirrorBase(parsed.scheme or "https", parsed.netloc)
