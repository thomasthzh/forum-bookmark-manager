from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
import os
from pathlib import Path
import re
from typing import Any
from urllib.parse import quote

import httpx


@dataclass(frozen=True)
class ClashControllerConfig:
    controller_url: str | None = None
    secret: str | None = None


ClientFactory = Callable[[], httpx.AsyncClient]


class ClashProxyRotator:
    def __init__(
        self,
        *,
        controller_url: str | None,
        secret: str | None = None,
        proxy_group: str = "节点选择",
        region_keywords: tuple[str, ...] = ("香港", "HK", "Hong", "新加坡", "SG", "Singapore", "狮城"),
        delay_test_url: str = "https://www.gstatic.com/generate_204",
        delay_timeout_ms: int = 5000,
        client_factory: ClientFactory | None = None,
    ):
        self.controller_url = _normalize_controller_url(controller_url)
        self.secret = secret or None
        self.proxy_group = proxy_group
        self.region_keywords = region_keywords
        self.delay_test_url = delay_test_url
        self.delay_timeout_ms = delay_timeout_ms
        self._client_factory = client_factory or (lambda: httpx.AsyncClient(timeout=8.0, trust_env=False))

    @classmethod
    def from_settings(cls, settings) -> "ClashProxyRotator":
        configured_url = getattr(settings, "clash_controller_url", None)
        configured_secret = getattr(settings, "clash_controller_secret", None)
        config = ClashControllerConfig(configured_url, configured_secret)
        if not config.controller_url:
            config = read_clash_controller_config(getattr(settings, "clash_config_path", None))
        return cls(
            controller_url=configured_url or config.controller_url,
            secret=configured_secret or config.secret,
            proxy_group=getattr(settings, "clash_proxy_group", "节点选择"),
            region_keywords=tuple(getattr(settings, "clash_region_keywords", ()) or ()),
            delay_test_url=getattr(settings, "clash_delay_test_url", "https://www.gstatic.com/generate_204"),
            delay_timeout_ms=int(getattr(settings, "clash_delay_timeout_ms", 5000)),
        )

    async def switch_to_available_proxy(self, on_message: Callable[[str], None] | None = None) -> bool:
        if not self.controller_url:
            _notify(on_message, "ClashVerge controller 未配置，跳过代理切换")
            return False
        async with self._client_factory() as client:
            proxies_payload = await self._get_json(client, "/proxies")
            if not proxies_payload:
                _notify(on_message, "ClashVerge controller 未响应，跳过代理切换")
                return False
            proxies = proxies_payload.get("proxies", {})
            group_name = self._select_group_name(proxies)
            if not group_name:
                _notify(on_message, "ClashVerge 未找到可切换的代理组")
                return False
            candidates = self._candidate_proxy_names(proxies[group_name])
            for candidate in candidates:
                if not await self._probe_delay(client, candidate):
                    continue
                if await self._select_proxy(client, group_name, candidate):
                    _notify(on_message, f"ClashVerge 已切换：{group_name} -> {candidate}")
                    return True
            _notify(on_message, "ClashVerge 未找到可用的香港/新加坡代理")
            return False

    async def status(self) -> dict[str, Any]:
        if not self.controller_url:
            return {
                "configured": False,
                "reachable": False,
                "controller_url": None,
                "proxy_group": self.proxy_group,
                "current": None,
                "candidate_count": 0,
                "message": "ClashVerge controller 未配置",
            }
        async with self._client_factory() as client:
            proxies_payload = await self._get_json(client, "/proxies")
            if not proxies_payload:
                return {
                    "configured": True,
                    "reachable": False,
                    "controller_url": self.controller_url,
                    "proxy_group": self.proxy_group,
                    "current": None,
                    "candidate_count": 0,
                    "message": "ClashVerge controller 未响应",
                }
            proxies = proxies_payload.get("proxies", {})
            group_name = self._select_group_name(proxies)
            if not group_name:
                return {
                    "configured": True,
                    "reachable": True,
                    "controller_url": self.controller_url,
                    "proxy_group": self.proxy_group,
                    "current": None,
                    "candidate_count": 0,
                    "message": "未找到可切换的代理组",
                }
            group = proxies[group_name]
            candidates = self._candidate_proxy_names(group)
            return {
                "configured": True,
                "reachable": True,
                "controller_url": self.controller_url,
                "proxy_group": group_name,
                "current": group.get("now"),
                "candidate_count": len(candidates),
                "message": "connected",
            }

    def _select_group_name(self, proxies: dict[str, Any]) -> str | None:
        if self.proxy_group in proxies and _looks_like_selector(proxies[self.proxy_group]):
            return self.proxy_group
        for name, payload in proxies.items():
            if _looks_like_selector(payload) and ("节点" in name or "Proxy" in name or "GLOBAL" in name.upper()):
                return str(name)
        for name, payload in proxies.items():
            if _looks_like_selector(payload):
                return str(name)
        return None

    def _candidate_proxy_names(self, group: dict[str, Any]) -> list[str]:
        current = str(group.get("now") or "")
        names = [str(name) for name in group.get("all", [])]
        candidates = [name for name in names if name != current and _matches_region(name, self.region_keywords)]
        if not candidates and current:
            candidates = [name for name in names if _matches_region(name, self.region_keywords)]
        return candidates

    async def _get_json(self, client: httpx.AsyncClient, path: str) -> dict[str, Any] | None:
        try:
            response = await client.get(self._url(path), headers=self._headers())
            if response.status_code >= 400:
                return None
            payload = response.json()
            return payload if isinstance(payload, dict) else None
        except (httpx.HTTPError, ValueError):
            return None

    async def _probe_delay(self, client: httpx.AsyncClient, proxy_name: str) -> bool:
        path = f"/proxies/{quote(proxy_name, safe='')}/delay"
        try:
            response = await client.get(
                self._url(path),
                params={"timeout": self.delay_timeout_ms, "url": self.delay_test_url},
                headers=self._headers(),
            )
            if response.status_code >= 400:
                return False
            payload = response.json()
            return isinstance(payload, dict) and int(payload.get("delay", 0)) > 0
        except (httpx.HTTPError, ValueError, TypeError):
            return False

    async def _select_proxy(self, client: httpx.AsyncClient, group_name: str, proxy_name: str) -> bool:
        path = f"/proxies/{quote(group_name, safe='')}"
        try:
            response = await client.put(self._url(path), headers=self._headers(), json={"name": proxy_name})
            return response.status_code < 400
        except httpx.HTTPError:
            return False

    def _headers(self) -> dict[str, str]:
        if not self.secret:
            return {}
        return {"Authorization": f"Bearer {self.secret}"}

    def _url(self, path: str) -> str:
        return f"{self.controller_url}{path}"


def read_clash_controller_config(path: str | Path | None = None) -> ClashControllerConfig:
    config_path = _default_clash_config_path() if path is None else Path(os.path.expandvars(str(path))).expanduser()
    if not config_path or not config_path.exists():
        return ClashControllerConfig()
    text = config_path.read_text(encoding="utf-8", errors="ignore")
    controller = _line_value(text, "external-controller")
    secret = _line_value(text, "secret")
    return ClashControllerConfig(_normalize_controller_url(controller), secret)


def _default_clash_config_path() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "io.github.clash-verge-rev.clash-verge-rev" / "config.yaml"


def _line_value(text: str, key: str) -> str | None:
    match = re.search(rf"(?m)^\s*{re.escape(key)}\s*:\s*(.+?)\s*$", text)
    if not match:
        return None
    value = match.group(1).strip().strip("'\"")
    return value or None


def _normalize_controller_url(value: str | None) -> str | None:
    if not value:
        return None
    text = value.strip().rstrip("/")
    if not text:
        return None
    if "://" not in text:
        text = f"http://{text}"
    return text


def _looks_like_selector(payload: Any) -> bool:
    return isinstance(payload, dict) and isinstance(payload.get("all"), list)


def _matches_region(name: str, keywords: tuple[str, ...]) -> bool:
    lowered = name.lower()
    return any(keyword.lower() in lowered for keyword in keywords)


def _notify(callback: Callable[[str], None] | None, message: str) -> None:
    if callback is not None:
        callback(message)
