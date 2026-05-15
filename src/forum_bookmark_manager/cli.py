from __future__ import annotations

import argparse
import asyncio
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import webbrowser

import uvicorn

from .annotator import open_annotation_browser
from .crawler import ForumCrawler, open_login_browser, verify_then_crawl
from .repository import Repository
from .settings import load_settings
from .web import create_app


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="forum-bookmark-manager")
    parser.add_argument("--config", default="config/settings.toml", help="设置文件路径")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("login", help="打开独立 Edge 登录目录，手动确认论坛登录")
    crawl = subparsers.add_parser("crawl", help="并发爬取收藏帖子并写入 SQLite")
    crawl.add_argument("--start-url", help="论坛收藏页链接；不填则使用 config/settings.toml")
    crawl.add_argument("--no-verify", action="store_true", help="跳过可见验证窗口，直接无头爬取")

    annotate = subparsers.add_parser("annotate", help="打开帖子标注窗口，点选需要抓取的区域")
    annotate.add_argument("target_url", help="用于标注结构的帖子链接")

    serve = subparsers.add_parser("serve", help="启动本地 HTML 数据库浏览器")
    serve.add_argument("--host", default="127.0.0.1", help="监听地址")
    serve.add_argument("--port", type=int, default=53102, help="监听端口")

    open_cmd = subparsers.add_parser("open", help="后台启动服务并打开浏览器")
    open_cmd.add_argument("--host", default="127.0.0.1", help="监听地址")
    open_cmd.add_argument("--port", type=int, default=53102, help="监听端口")
    open_cmd.add_argument("--no-browser", action="store_true", help="只启动服务，不打开浏览器")

    stop = subparsers.add_parser("stop", help="关闭后台服务")
    stop.add_argument("--port", type=int, default=53102, help="要关闭的服务端口")
    return parser


def server_url(host: str, port: int) -> str:
    return f"http://{host}:{port}"


def server_pid_path(settings, port: int) -> Path:
    return settings.database_path.parent / "logs" / f"server-{port}.pid"


def manager_profile_path(settings) -> Path:
    return settings.database_path.parent / "manager-profile"


def build_manager_browser_command(url: str, profile_dir: str | Path, edge_executable: str | Path) -> list[str]:
    resolved_profile_dir = Path(profile_dir).expanduser().resolve()
    return [
        str(edge_executable),
        f"--app={url}",
        f"--user-data-dir={resolved_profile_dir}",
        "--no-first-run",
        "--disable-session-crashed-bubble",
        "--disable-features=msEdgeStartupBoost",
    ]


def open_manager(settings, *, host: str, port: int, config_path: str, open_browser: bool = True) -> str:
    url = server_url(host, port)
    if not _is_port_open(host, port):
        pid_path = server_pid_path(settings, port)
        pid_path.parent.mkdir(parents=True, exist_ok=True)
        stdout_path = pid_path.with_suffix(".out.log")
        stderr_path = pid_path.with_suffix(".err.log")
        stdout = stdout_path.open("ab")
        stderr = stderr_path.open("ab")
        creationflags = 0
        if os.name == "nt":
            creationflags = subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NO_WINDOW
        process = subprocess.Popen(
            [
                sys.executable,
                "-m",
                "forum_bookmark_manager.cli",
                "--config",
                config_path,
                "serve",
                "--host",
                host,
                "--port",
                str(port),
            ],
            cwd=Path.cwd(),
            stdout=stdout,
            stderr=stderr,
            creationflags=creationflags,
        )
        stdout.close()
        stderr.close()
        pid_path.write_text(str(process.pid), encoding="utf-8")
        _wait_for_port(host, port)
    if open_browser:
        open_manager_browser(settings, url)
    return url


def open_manager_browser(settings, url: str) -> None:
    edge_executable = _find_edge_executable()
    if edge_executable is None:
        webbrowser.open(url)
        return
    profile_dir = manager_profile_path(settings).expanduser().resolve()
    profile_dir.mkdir(parents=True, exist_ok=True)
    creationflags = 0
    if os.name == "nt":
        creationflags = subprocess.CREATE_NO_WINDOW
    subprocess.Popen(
        build_manager_browser_command(url, profile_dir, edge_executable),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        creationflags=creationflags,
    )


def stop_manager(settings, *, port: int) -> bool:
    pid_path = server_pid_path(settings, port)
    if not pid_path.exists():
        return False
    try:
        pid = int(pid_path.read_text(encoding="utf-8").strip())
    except ValueError:
        pid_path.unlink(missing_ok=True)
        return False

    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, text=True)
    else:
        try:
            os.kill(pid, 15)
        except OSError:
            pass
    pid_path.unlink(missing_ok=True)
    return True


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    settings = load_settings(args.config)
    repository = Repository(settings.database_path)
    repository.initialize()

    if args.command == "login":
        try:
            asyncio.run(open_login_browser(settings))
        except RuntimeError as exc:
            print(str(exc), file=sys.stderr)
            return 1
        return 0

    if args.command == "crawl":
        try:
            if args.no_verify:
                asyncio.run(ForumCrawler(settings, repository, start_url=args.start_url).crawl())
            else:
                asyncio.run(verify_then_crawl(settings, repository, start_url=args.start_url))
        except RuntimeError as exc:
            print(str(exc), file=sys.stderr)
            return 1
        return 0

    if args.command == "annotate":
        try:
            asyncio.run(open_annotation_browser(settings, args.target_url))
        except RuntimeError as exc:
            print(str(exc), file=sys.stderr)
            return 1
        return 0

    if args.command == "serve":
        def crawl_callback(start_url: str, mirror_url: str | None = None) -> None:
            asyncio.run(verify_then_crawl(settings, repository, start_url=start_url, mirror_url=mirror_url))

        def annotate_callback(target_url: str) -> None:
            asyncio.run(open_annotation_browser(settings, target_url))

        app = create_app(repository, crawl_callback=crawl_callback, annotate_callback=annotate_callback)
        uvicorn.run(app, host=args.host, port=args.port)
        return 0

    if args.command == "open":
        url = open_manager(
            settings,
            host=args.host,
            port=args.port,
            config_path=args.config,
            open_browser=not args.no_browser,
        )
        print(f"收藏管理器已启动：{url}")
        return 0

    if args.command == "stop":
        stopped = stop_manager(settings, port=args.port)
        print("收藏管理器已关闭。" if stopped else "没有找到正在运行的后台服务。")
        return 0

    parser.error(f"Unknown command: {args.command}")
    return 2


def _is_port_open(host: str, port: int) -> bool:
    try:
        with socket.create_connection((host, port), timeout=0.3):
            return True
    except OSError:
        return False


def _wait_for_port(host: str, port: int, timeout: float = 15.0) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if _is_port_open(host, port):
            return
        time.sleep(0.2)
    raise RuntimeError(f"服务启动超时：{server_url(host, port)}")


def _find_edge_executable() -> str | None:
    candidates: list[Path] = []
    for env_name in ("ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"):
        root = os.environ.get(env_name)
        if root:
            candidates.append(Path(root) / "Microsoft" / "Edge" / "Application" / "msedge.exe")
    for candidate in candidates:
        if candidate.exists():
            return str(candidate)
    return shutil.which("msedge") or shutil.which("microsoft-edge")


if __name__ == "__main__":
    raise SystemExit(main())
