from __future__ import annotations

from datetime import datetime, UTC
from pathlib import Path
import sqlite3
from typing import Any

from .models import DownloadStatus, ParsedPost


class Repository:
    def __init__(self, database_path: str | Path):
        self.database_path = Path(database_path)

    def initialize(self) -> None:
        self.database_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as conn:
            conn.executescript(SCHEMA)

    def upsert_post(self, post: ParsedPost) -> int:
        now = _now()
        with self._connect() as conn:
            existing = conn.execute(
                "select id, status, created_at from posts where post_url = ?",
                (post.post_url,),
            ).fetchone()
            if existing:
                post_id = int(existing["id"])
                conn.execute(
                    """
                    update posts
                    set favorite_url = ?,
                        title = ?,
                        project_type = ?,
                        favorite_time = ?,
                        download_count = ?,
                        visit_count = ?,
                        favorite_count = ?,
                        extract_password = ?,
                        body_text = ?,
                        updated_at = ?,
                        last_crawled_at = ?
                    where id = ?
                    """,
                    (
                        post.favorite_url,
                        post.title,
                        post.project_type,
                        post.favorite_time,
                        post.download_count,
                        post.visit_count,
                        post.favorite_count,
                        post.extract_password,
                        post.body_text,
                        now,
                        now,
                        post_id,
                    ),
                )
            else:
                cursor = conn.execute(
                    """
                    insert into posts (
                        post_url, favorite_url, title, project_type, status,
                        favorite_time, download_count, visit_count, favorite_count,
                        extract_password, body_text, created_at, updated_at, last_crawled_at
                    ) values (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        post.post_url,
                        post.favorite_url,
                        post.title,
                        post.project_type,
                        DownloadStatus.PENDING.value,
                        post.favorite_time,
                        post.download_count,
                        post.visit_count,
                        post.favorite_count,
                        post.extract_password,
                        post.body_text,
                        now,
                        now,
                        now,
                    ),
                )
                post_id = int(cursor.lastrowid)

            conn.execute("delete from post_images where post_id = ?", (post_id,))
            conn.execute("delete from download_links where post_id = ?", (post_id,))
            for image in post.images:
                conn.execute(
                    """
                    insert into post_images (
                        post_id, position, source_url, local_path, thumbnail_path, download_status
                    ) values (?, ?, ?, ?, ?, ?)
                    """,
                    (
                        post_id,
                        image.position,
                        image.source_url,
                        image.local_path,
                        image.thumbnail_path,
                        image.download_status,
                    ),
                )
            for position, link in enumerate(post.download_links, start=1):
                conn.execute(
                    """
                    insert into download_links (post_id, position, url, label, context_text)
                    values (?, ?, ?, ?, ?)
                    """,
                    (post_id, position, link.url, link.label, link.context_text),
                )
            return post_id

    def update_post_image(self, post_url: str, image) -> bool:
        with self._connect() as conn:
            post = conn.execute("select id from posts where post_url = ?", (post_url,)).fetchone()
            if not post:
                return False
            cursor = conn.execute(
                """
                update post_images
                set source_url = ?,
                    local_path = ?,
                    thumbnail_path = ?,
                    download_status = ?
                where post_id = ? and position = ?
                """,
                (
                    image.source_url,
                    image.local_path,
                    image.thumbnail_path,
                    image.download_status,
                    post["id"],
                    image.position,
                ),
            )
            return cursor.rowcount > 0

    def list_posts(
        self,
        *,
        project_type: str | None = None,
        status: DownloadStatus | str | None = None,
        query: str | None = None,
        sort: str = "new",
    ) -> list[dict[str, Any]]:
        where: list[str] = []
        params: list[Any] = []
        if project_type and project_type != "全部":
            where.append("project_type = ?")
            params.append(project_type)
        if status and status != "全部":
            where.append("status = ?")
            params.append(DownloadStatus(status).value)

        direction = "asc" if sort == "old" else "desc"
        sql = "select * from posts"
        if where:
            sql += " where " + " and ".join(where)
        sql += f" order by coalesce(favorite_time, '') {direction}, id {direction}"

        with self._connect() as conn:
            rows = [dict(row) for row in conn.execute(sql, params).fetchall()]
            for row in rows:
                row["images"] = [dict(image) for image in conn.execute(
                    "select * from post_images where post_id = ? order by position",
                    (row["id"],),
                ).fetchall()]
                row["download_links"] = [dict(link) for link in conn.execute(
                    "select * from download_links where post_id = ? order by position",
                    (row["id"],),
                ).fetchall()]

        if query:
            needle = query.casefold()
            rows = [row for row in rows if _matches_query(row, needle)]
        return rows

    def get_post(self, post_id: int) -> dict[str, Any] | None:
        with self._connect() as conn:
            row = conn.execute("select * from posts where id = ?", (post_id,)).fetchone()
            if not row:
                return None
            post = dict(row)
            post["images"] = [dict(image) for image in conn.execute(
                "select * from post_images where post_id = ? order by position",
                (post_id,),
            ).fetchall()]
            post["download_links"] = [dict(link) for link in conn.execute(
                "select * from download_links where post_id = ? order by position",
                (post_id,),
            ).fetchall()]
            return post

    def update_status(self, post_id: int, status: DownloadStatus | str) -> None:
        with self._connect() as conn:
            conn.execute(
                "update posts set status = ?, updated_at = ? where id = ?",
                (DownloadStatus(status).value, _now(), post_id),
            )

    def update_status_many(self, post_ids: list[int], status: DownloadStatus | str) -> int:
        if not post_ids:
            return 0
        status_value = DownloadStatus(status).value
        updated_at = _now()
        updated = 0
        with self._connect() as conn:
            for chunk in _chunks(post_ids):
                placeholders = ", ".join("?" for _ in chunk)
                cursor = conn.execute(
                    f"update posts set status = ?, updated_at = ? where id in ({placeholders})",
                    [status_value, updated_at, *chunk],
                )
                updated += cursor.rowcount
        return updated

    def delete_post(self, post_id: int) -> bool:
        with self._connect() as conn:
            cursor = conn.execute("delete from posts where id = ?", (post_id,))
            return cursor.rowcount > 0

    def delete_posts(self, post_ids: list[int]) -> int:
        if not post_ids:
            return 0
        deleted = 0
        with self._connect() as conn:
            for chunk in _chunks(post_ids):
                placeholders = ", ".join("?" for _ in chunk)
                cursor = conn.execute(f"delete from posts where id in ({placeholders})", chunk)
                deleted += cursor.rowcount
        return deleted

    def start_crawl_run(self) -> int:
        with self._connect() as conn:
            cursor = conn.execute(
                """
                insert into crawl_runs (
                    started_at, status, total_favorites, processed_posts,
                    successful_posts, failed_posts
                ) values (?, ?, 0, 0, 0, 0)
                """,
                (_now(), "running"),
            )
            return int(cursor.lastrowid)

    def update_crawl_run(self, run_id: int, **fields: Any) -> None:
        if not fields:
            return
        allowed = {
            "finished_at",
            "status",
            "total_favorites",
            "processed_posts",
            "successful_posts",
            "failed_posts",
            "message",
        }
        assignments: list[str] = []
        values: list[Any] = []
        for key, value in fields.items():
            if key not in allowed:
                raise ValueError(f"Unsupported crawl run field: {key}")
            assignments.append(f"{key} = ?")
            values.append(value)
        values.append(run_id)
        with self._connect() as conn:
            conn.execute(f"update crawl_runs set {', '.join(assignments)} where id = ?", values)

    def record_error(self, run_id: int, url: str, stage: str, message: str) -> None:
        with self._connect() as conn:
            conn.execute(
                """
                insert into crawl_errors (run_id, url, stage, message, created_at)
                values (?, ?, ?, ?, ?)
                """,
                (run_id, url, stage, message, _now()),
            )

    def latest_progress(self) -> dict[str, Any]:
        with self._connect() as conn:
            run = conn.execute("select * from crawl_runs order by id desc limit 1").fetchone()
            if not run:
                return {
                    "id": None,
                    "status": "idle",
                    "total_favorites": 0,
                    "processed_posts": 0,
                    "successful_posts": 0,
                    "failed_posts": 0,
                    "errors": [],
                }
            progress = dict(run)
            progress["errors"] = [
                dict(error)
                for error in conn.execute(
                    "select * from crawl_errors where run_id = ? order by id desc limit 20",
                    (run["id"],),
                ).fetchall()
            ]
            return progress

    def known_types(self) -> list[str]:
        with self._connect() as conn:
            return [
                row["project_type"]
                for row in conn.execute(
                    "select distinct project_type from posts where project_type is not null order by project_type"
                ).fetchall()
            ]

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.database_path)
        conn.row_factory = sqlite3.Row
        conn.execute("pragma foreign_keys = on")
        return conn


def _matches_query(row: dict[str, Any], needle: str) -> bool:
    text_parts = [
        row.get("title") or "",
        row.get("extract_password") or "",
        row.get("body_text") or "",
        row.get("post_url") or "",
    ]
    for link in row.get("download_links", []):
        text_parts.extend([link.get("url") or "", link.get("label") or "", link.get("context_text") or ""])
    return needle in " ".join(text_parts).casefold()


def _now() -> str:
    return datetime.now(UTC).replace(microsecond=0).isoformat()


def _chunks(values: list[int], size: int = 900):
    for index in range(0, len(values), size):
        yield values[index:index + size]


SCHEMA = """
create table if not exists posts (
    id integer primary key,
    post_url text not null unique,
    favorite_url text,
    title text not null,
    project_type text not null default '未分类',
    status text not null default '未下载',
    favorite_time text,
    download_count integer,
    visit_count integer,
    favorite_count integer,
    extract_password text,
    body_text text,
    created_at text not null,
    updated_at text not null,
    last_crawled_at text
);

create table if not exists post_images (
    id integer primary key,
    post_id integer not null references posts(id) on delete cascade,
    position integer not null,
    source_url text not null,
    local_path text,
    thumbnail_path text,
    download_status text not null,
    unique(post_id, position)
);

create table if not exists download_links (
    id integer primary key,
    post_id integer not null references posts(id) on delete cascade,
    position integer not null,
    url text not null,
    label text,
    context_text text,
    unique(post_id, position)
);

create table if not exists crawl_runs (
    id integer primary key,
    started_at text not null,
    finished_at text,
    status text not null,
    total_favorites integer not null default 0,
    processed_posts integer not null default 0,
    successful_posts integer not null default 0,
    failed_posts integer not null default 0,
    message text
);

create table if not exists crawl_errors (
    id integer primary key,
    run_id integer not null references crawl_runs(id) on delete cascade,
    url text not null,
    stage text not null,
    message text not null,
    created_at text not null
);
"""
