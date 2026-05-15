from __future__ import annotations

from dataclasses import dataclass
import re
from urllib.parse import parse_qs, urlencode, urljoin, urlparse, urlunparse

from bs4 import BeautifulSoup, Tag

from .models import DownloadLink, FavoriteItem, ParsedPost, PostImage


@dataclass(frozen=True)
class FavoritePage:
    items: list[FavoriteItem]
    next_url: str | None = None
    page_urls: list[str] = None
    max_page: int = 1

    def __post_init__(self) -> None:
        if self.page_urls is None:
            object.__setattr__(self, "page_urls", [])


COUNT_RE = re.compile(r"(\d[\d,]*)")
DATE_RE = re.compile(r"20\d{2}-\d{1,2}-\d{1,2}(?:\s+\d{1,2}:\d{2})?")
RELATIVE_TIME_RE = re.compile(
    r"(刚刚|半小时前|\d+\s*(?:秒|分钟|小时|天|周|个月|年)前|昨天(?:\s+\d{1,2}:\d{2})?|前天(?:\s+\d{1,2}:\d{2})?)"
)
PASSWORD_PATTERNS = (
    re.compile(r"(?:解压密码|解压码|解壓密碼|解壓碼|压缩密码|壓縮密碼)\s*[:：=]\s*([^\s，,。；;|]+)", re.I),
    re.compile(r"(?:password|pass)\s*[:：=]\s*([^\s，,。；;|]+)", re.I),
    re.compile(r"(?:密码|密碼)\s*[:：=]\s*([^\s，,。；;|]+)", re.I),
)
PASSWORD_LABELS = (
    "解压密码",
    "解压码",
    "解壓密碼",
    "解壓碼",
    "压缩密码",
    "壓縮密碼",
    "密码",
    "密碼",
)
RESOURCE_HINTS = (
    "pan.baidu.com",
    "quark.cn",
    "aliyundrive.com",
    "alipan.com",
    "123pan.com",
    "115.com",
    "magnet:",
    "ed2k://",
    ".torrent",
    "download",
    "attach",
    "forum.php?mod=attachment",
)
IMAGE_EXCLUDE_HINTS = (
    "avatar",
    "smiley",
    "static/",
    "common/",
    "logo",
    "icon",
    "emotion",
)
IMAGE_SOURCE_ATTRIBUTES = (
    "file",
    "zoomfile",
    "data-original",
    "data-original-src",
    "data-src",
    "data-echo",
    "data-lazy-src",
    "lazy-src",
    "src",
)


def parse_favorite_page(html: str, page_url: str) -> FavoritePage:
    soup = BeautifulSoup(html, "html.parser")
    page_text = soup.get_text(" ", strip=True)
    page_urls, max_page = _favorite_page_urls(soup, page_url)
    next_url = None
    next_anchor = soup.select_one("a.nxt")
    if next_anchor and next_anchor.get("href"):
        next_url = urljoin(page_url, str(next_anchor["href"]))

    if "您还没有添加任何收藏" in page_text or "您還沒有添加任何收藏" in page_text:
        return FavoritePage(items=[], next_url=next_url, page_urls=page_urls, max_page=max_page)

    seen: set[str] = set()
    items: list[FavoriteItem] = []

    for anchor in _favorite_anchors(soup):
        href = str(anchor["href"])
        if not _looks_like_post_url(href):
            continue
        absolute = urljoin(page_url, href)
        if absolute in seen:
            continue
        seen.add(absolute)
        container = _nearest_container(anchor)
        favorite_time = _find_date(container.get_text(" ", strip=True) if container else anchor.get_text(" ", strip=True))
        items.append(FavoriteItem(url=absolute, favorite_time=favorite_time, title=_clean_text(anchor.get_text(" ", strip=True))))

    return FavoritePage(items=items, next_url=next_url, page_urls=page_urls, max_page=max_page)


def parse_post_page(
    html: str,
    *,
    post_url: str,
    favorite_url: str | None = None,
    favorite_time: str | None = None,
    selector_profile: dict[str, str] | None = None,
    content_base_url: str | None = None,
) -> ParsedPost:
    soup = BeautifulSoup(html, "html.parser")
    selectors = selector_profile or {}
    selected_image_container = _selected_node(soup, selectors, "images")
    first_floor = _first_floor_container(soup)
    body = (
        _selected_node(soup, selectors, "body")
        or _nearby_body_from_image_container(selected_image_container)
        or first_floor
        or _main_post_body(soup)
    )
    body_text = _clean_text(body.get_text(" ", strip=True)) if body else _clean_text(soup.get_text(" ", strip=True))
    breadcrumb = [_clean_text(a.get_text(" ", strip=True)) for a in _breadcrumb_anchors(soup)]
    selected_project_type = _selected_text(soup, selectors, "project_type")
    selected_title = _selected_text(soup, selectors, "title")
    title = selected_title if selected_title and _looks_like_title(selected_title) else _extract_title(soup)
    password_text = _selected_text(soup, selectors, "password")
    image_container = selected_image_container or _first_floor_image_container(first_floor) or body or soup
    selected_download_container = _selected_node(soup, selectors, "download_links")
    download_container = selected_download_container or first_floor or body or soup
    page_text = soup.get_text(" ", strip=True)
    password = _extract_post_password(soup, first_floor, body, password_text)
    media_base_url = content_base_url or post_url
    images = extract_images(image_container, media_base_url)
    if not images and selected_image_container is not None:
        fallback_image_container = _first_floor_image_container(first_floor) or body or soup
        images = extract_images(fallback_image_container, media_base_url)
    download_links = extract_download_links(
        download_container,
        media_base_url,
        require_hint=not bool(selectors.get("download_links")),
    )
    if not download_links and selected_download_container is not None:
        download_links = extract_download_links(first_floor or body or soup, media_base_url, require_hint=True)

    return ParsedPost(
        post_url=post_url,
        favorite_url=favorite_url,
        title=title,
        project_type=detect_project_type(title, body_text, [selected_project_type] if selected_project_type else breadcrumb),
        favorite_time=favorite_time,
        download_count=_extract_count_from_selector_or_page(soup, selectors, "download_count", page_text, ("下载", "下載")),
        visit_count=_extract_count_from_selector_or_page(soup, selectors, "visit_count", page_text, ("查看", "访问", "訪問", "views")),
        favorite_count=_extract_count_from_selector_or_page(soup, selectors, "favorite_count", page_text, ("收藏", "favorites")),
        extract_password=password,
        body_text=body_text,
        images=images,
        download_links=download_links,
    )


def extract_password(text: str) -> str | None:
    for pattern in PASSWORD_PATTERNS:
        match = pattern.search(text)
        if match:
            return match.group(1).strip()
    return None


def detect_project_type(title: str, body_text: str, breadcrumbs: list[str]) -> str:
    haystacks = breadcrumbs + [title, body_text]
    for text in haystacks:
        normalized = text.lower()
        if "游戏" in text or "game" in normalized:
            return "游戏"
        if "国产自拍" in text or ("自拍" in text and "国产" in text):
            return "国产自拍"
    for text in reversed(breadcrumbs):
        candidate = _clean_project_type(text)
        if candidate:
            return candidate
    return "未分类"


def _clean_project_type(text: str) -> str | None:
    candidate = _clean_text(text)
    if not candidate:
        return None
    generic = {"论坛", "首页", "示例论坛", "资源", "板块", "全部", "未分类", "导读", "推广送软妹币，赚现金"}
    if candidate in generic:
        return None
    if len(candidate) > 24:
        return None
    return candidate


def extract_images(container: Tag, base_url: str, limit: int = 9) -> list[PostImage]:
    images: list[PostImage] = []
    seen: set[str] = set()
    for img in container.find_all("img"):
        for src_text in _image_source_candidates(img):
            if _is_excluded_image(img, src_text):
                continue
            absolute = urljoin(base_url, src_text)
            if absolute in seen:
                continue
            seen.add(absolute)
            images.append(PostImage(source_url=absolute, position=len(images) + 1))
            break
        if len(images) >= limit:
            break
    return images


def extract_download_links(container: Tag, base_url: str, *, require_hint: bool = True) -> list[DownloadLink]:
    links: list[DownloadLink] = []
    seen: set[str] = set()
    for anchor in container.find_all("a", href=True):
        if _is_preview_attachment_link(anchor):
            continue
        href = str(anchor["href"]).strip()
        label = _clean_text(anchor.get_text(" ", strip=True))
        if href.lower().startswith("javascript:"):
            continue
        if require_hint and not _looks_like_download_link(href, label):
            continue
        url = urljoin(base_url, href)
        if url in seen:
            continue
        seen.add(url)
        context = _link_context(anchor)
        links.append(DownloadLink(url=url, label=label, context_text=context))
    return links


def _extract_title(soup: BeautifulSoup) -> str:
    for selector in ("#thread_subject", "h1", ".ts span", ".ts"):
        node = soup.select_one(selector)
        if node:
            text = _clean_text(node.get_text(" ", strip=True))
            if text:
                return text
    if soup.title:
        return _clean_text(soup.title.get_text(" ", strip=True).split(" - ")[0])
    return ""


def _looks_like_title(text: str) -> bool:
    if not text or len(text) > 80:
        return False
    lowered = text.casefold()
    noisy_hints = ("下载链接", "百度盘", "提取码", "解压", "售价", "已购", "password", "download")
    return not any(hint.casefold() in lowered for hint in noisy_hints)


def _selected_node(soup: BeautifulSoup, selectors: dict[str, str], key: str) -> Tag | None:
    selector = selectors.get(key)
    if not selector:
        return None
    try:
        node = soup.select_one(selector)
    except Exception:
        return None
    return node if isinstance(node, Tag) else None


def _selected_text(soup: BeautifulSoup, selectors: dict[str, str], key: str) -> str | None:
    node = _selected_node(soup, selectors, key)
    if not node:
        return None
    text = _clean_text(node.get_text(" ", strip=True))
    return text or None


def _extract_selected_password(text: str) -> str | None:
    return extract_password(text) or _clean_text(text) or None


def _extract_post_password(
    soup: BeautifulSoup,
    first_floor: Tag | None,
    body: Tag | None,
    selected_text: str | None,
) -> str | None:
    if selected_text:
        selected_password = _extract_selected_password(selected_text)
        if selected_password:
            return selected_password
    for container in (first_floor, body, soup):
        if not container:
            continue
        table_value = _extract_table_value(container, PASSWORD_LABELS)
        if table_value:
            return table_value
    for container in (first_floor, body, soup):
        if not container:
            continue
        password = extract_password(container.get_text("\n", strip=True))
        if password:
            return password
    return None


def _extract_table_value(container: Tag, labels: tuple[str, ...]) -> str | None:
    for row in container.select("tr"):
        header = row.find(["th", "dt"])
        if not header:
            continue
        header_text = _clean_text(header.get_text(" ", strip=True)).rstrip(":：")
        if not any(label in header_text for label in labels):
            continue
        value = row.find(["td", "dd"])
        if not value:
            continue
        text = _clean_text(value.get_text(" ", strip=True))
        if text:
            return text
    return None


def _main_post_body(soup: BeautifulSoup) -> Tag | None:
    for selector in ("td.t_f", ".t_f", "[id^=postmessage_]", ".pcb"):
        node = soup.select_one(selector)
        if isinstance(node, Tag):
            return node
    return None


def _first_floor_container(soup: BeautifulSoup) -> Tag | None:
    for selector in (
        "#postlist > div[id^=post_] .pcb",
        "#postlist table[id^=pid] .pcb",
        "div[id^=post_] .pcb",
    ):
        node = soup.select_one(selector)
        if isinstance(node, Tag):
            return node
    return None


def _first_floor_image_container(first_floor: Tag | None) -> Tag | None:
    if not first_floor:
        return None
    node = first_floor.select_one(".pattl, .t_fsz")
    return node if isinstance(node, Tag) else first_floor


def _breadcrumb_anchors(soup: BeautifulSoup) -> list[Tag]:
    anchors = [anchor for anchor in soup.select("#pt a") if isinstance(anchor, Tag)]
    if anchors:
        return anchors
    return [anchor for anchor in soup.select(".z a") if isinstance(anchor, Tag)]


def _nearby_body_from_image_container(image_container: Tag | None) -> Tag | None:
    if not image_container:
        return None
    if _is_content_region(image_container):
        return image_container
    for parent in image_container.parents:
        if isinstance(parent, Tag) and _is_content_region(parent):
            return parent
    return None


def _is_content_region(node: Tag) -> bool:
    node_id = str(node.get("id") or "")
    classes = set(node.get("class") or [])
    return (
        node_id.startswith("postmessage_")
        or bool(classes & {"t_fsz", "t_f", "pcb"})
        or node.name == "td" and "t_f" in classes
    )


def _extract_labeled_count(text: str, labels: tuple[str, ...]) -> int | None:
    for label in labels:
        pattern = re.compile(rf"{re.escape(label)}\s*(?:次数)?\s*[:：]?\s*(\d[\d,]*)", re.I)
        match = pattern.search(text)
        if match:
            return int(match.group(1).replace(",", ""))
    return None


def _extract_count_from_selector_or_page(
    soup: BeautifulSoup,
    selectors: dict[str, str],
    key: str,
    page_text: str,
    labels: tuple[str, ...],
) -> int | None:
    selected_text = _selected_text(soup, selectors, key)
    if selected_text:
        return _extract_labeled_count(selected_text, labels) or _extract_plain_count(selected_text)
    return _extract_labeled_count(page_text, labels)


def _extract_plain_count(text: str) -> int | None:
    match = COUNT_RE.search(text)
    return int(match.group(1).replace(",", "")) if match else None


def _looks_like_post_url(href: str) -> bool:
    lowered = href.lower()
    return (
        "mod=viewthread" in lowered
        or "thread-" in lowered
        or "tid=" in lowered and "forum.php" in lowered
    )


def _favorite_anchors(soup: BeautifulSoup) -> list[Tag]:
    scoped: list[Tag] = []
    for selector in (
        "[id^=favorite_li_]",
        "[id^=favorite_]",
        ".el li",
        ".xld li",
        "#favorite_ul li",
    ):
        for container in soup.select(selector):
            scoped.extend(container.find_all("a", href=True))
    if scoped:
        return scoped

    content = soup.select_one("#ct, .ct2_a, .mn")
    if isinstance(content, Tag):
        excluded_ids = {"ft", "hd", "nv", "um", "toptb"}
        anchors = []
        for anchor in content.find_all("a", href=True):
            if any(parent.get("id") in excluded_ids for parent in anchor.parents if isinstance(parent, Tag)):
                continue
            anchors.append(anchor)
        return anchors
    return soup.find_all("a", href=True)


def _favorite_page_urls(soup: BeautifulSoup, page_url: str) -> tuple[list[str], int]:
    if not soup.select_one(".pg"):
        return [], 1
    page_numbers = {int(match.group(1)) for match in re.finditer(r"[?&]page=(\d+)", page_url)}
    for anchor in soup.select(".pg a[href], .pg strong"):
        if anchor.name == "strong":
            text = _clean_text(anchor.get_text(" ", strip=True))
            if text.isdigit():
                page_numbers.add(int(text))
            continue
        href = str(anchor.get("href", ""))
        parsed_page = _page_number_from_url(href)
        if parsed_page:
            page_numbers.add(parsed_page)
    if not page_numbers:
        return [], 1
    max_page = max(page_numbers)
    return [_replace_page(page_url, page) for page in range(1, max_page + 1)], max_page


def _page_number_from_url(href: str) -> int | None:
    match = re.search(r"[?&]page=(\d+)", href)
    return int(match.group(1)) if match else None


def _replace_page(url: str, page: int) -> str:
    parsed = urlparse(url)
    query = parse_qs(parsed.query, keep_blank_values=True)
    query["page"] = [str(page)]
    return urlunparse(parsed._replace(query=urlencode(query, doseq=True)))


def _looks_like_download_link(href: str, label: str) -> bool:
    combined = f"{href} {label}".lower()
    return any(hint in combined for hint in RESOURCE_HINTS)


def _is_preview_attachment_link(anchor: Tag) -> bool:
    href = str(anchor.get("href") or "").lower()
    return "mod=attachment" in href


def _is_excluded_image(img: Tag, src: str) -> bool:
    lowered = src.lower()
    if any(hint in lowered for hint in IMAGE_EXCLUDE_HINTS):
        return True
    width = _int_attr(img, "width")
    height = _int_attr(img, "height")
    if width is not None and height is not None and (width < 80 or height < 80):
        return True
    return False


def _image_source_candidates(img: Tag) -> list[str]:
    sources: list[str] = []
    for attr in IMAGE_SOURCE_ATTRIBUTES:
        value = img.get(attr)
        if value:
            sources.append(str(value).strip())
    srcset = img.get("srcset") or img.get("data-srcset")
    if srcset:
        sources.extend(_srcset_urls(str(srcset)))
    clean_sources: list[str] = []
    seen: set[str] = set()
    for source in sources:
        if not source or source in seen:
            continue
        seen.add(source)
        clean_sources.append(source)
    return clean_sources


def _srcset_urls(value: str) -> list[str]:
    urls: list[str] = []
    for part in value.split(","):
        url = part.strip().split(" ", 1)[0]
        if url:
            urls.append(url)
    return urls


def _int_attr(tag: Tag, name: str) -> int | None:
    value = tag.get(name)
    if value is None:
        return None
    match = COUNT_RE.search(str(value))
    return int(match.group(1).replace(",", "")) if match else None


def _nearest_container(node: Tag) -> Tag | None:
    for parent in node.parents:
        if isinstance(parent, Tag) and parent.name in {"li", "tr", "div", "p"}:
            return parent
    return None


def _link_context(anchor: Tag) -> str:
    for parent in anchor.parents:
        if isinstance(parent, Tag) and parent.name in {"p", "li", "tr", "div"}:
            text = _clean_text(parent.get_text(" ", strip=True))
            if text:
                return text
    return _clean_text(anchor.get_text(" ", strip=True))


def _find_date(text: str) -> str | None:
    match = DATE_RE.search(text)
    if match:
        return match.group(0)
    match = RELATIVE_TIME_RE.search(text)
    return match.group(0) if match else None


def _clean_text(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip()
