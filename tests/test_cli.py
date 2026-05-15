from forum_bookmark_manager import cli
from forum_bookmark_manager.cli import build_manager_browser_command, build_parser, server_pid_path, server_url
from forum_bookmark_manager.settings import Settings


def test_cli_parser_supports_login_crawl_and_serve():
    parser = build_parser()

    assert parser.parse_args(["login"]).command == "login"
    crawl_args = parser.parse_args(["crawl", "--start-url", "https://example.test/favorites"])
    assert crawl_args.command == "crawl"
    assert crawl_args.start_url == "https://example.test/favorites"
    assert crawl_args.no_verify is False
    assert parser.parse_args(["crawl", "--no-verify"]).no_verify is True
    args = parser.parse_args(["serve", "--host", "127.0.0.1", "--port", "53102"])

    assert args.command == "serve"
    assert args.host == "127.0.0.1"
    assert args.port == 53102

    open_args = parser.parse_args(["open", "--host", "127.0.0.1", "--port", "53103", "--no-browser"])
    assert open_args.command == "open"
    assert open_args.no_browser is True

    stop_args = parser.parse_args(["stop", "--port", "53103"])
    assert stop_args.command == "stop"
    assert stop_args.port == 53103

    annotate_args = parser.parse_args(["annotate", "https://example.test/thread-1.html"])
    assert annotate_args.command == "annotate"
    assert annotate_args.target_url == "https://example.test/thread-1.html"


def test_server_paths_are_stable_under_data_directory(tmp_path):
    settings = Settings(
        start_url="https://example.test/favorites",
        detail_concurrency=12,
        image_concurrency=16,
        retry_count=2,
        database_path=tmp_path / "bookmarks.sqlite3",
        edge_profile_dir=tmp_path / "edge-profile",
        image_dir=tmp_path / "images",
        thumbnail_dir=tmp_path / "thumbnails",
    )

    assert server_pid_path(settings, 53102) == tmp_path / "logs" / "server-53102.pid"
    assert server_url("127.0.0.1", 53102) == "http://127.0.0.1:53102"


def test_manager_browser_command_uses_isolated_app_profile(tmp_path):
    url = "http://127.0.0.1:53102"
    edge_path = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"

    command = build_manager_browser_command(url, tmp_path / "nested" / ".." / "manager-profile", edge_path)
    user_data_arg = next(item for item in command if item.startswith("--user-data-dir="))
    user_data_dir = user_data_arg.removeprefix("--user-data-dir=")

    assert command[0] == edge_path
    assert f"--app={url}" in command
    assert user_data_dir == str((tmp_path / "manager-profile").resolve())
    assert "--no-first-run" in command
    assert "--disable-session-crashed-bubble" in command


def test_manager_browser_command_never_passes_relative_profile_path():
    command = build_manager_browser_command(
        "http://127.0.0.1:53102",
        "data/manager-profile",
        "msedge.exe",
    )
    user_data_arg = next(item for item in command if item.startswith("--user-data-dir="))

    assert user_data_arg != "--user-data-dir=data/manager-profile"
    assert user_data_arg != "--user-data-dir=data\\manager-profile"


def test_cli_reports_crawl_runtime_error_without_traceback(monkeypatch, tmp_path, capsys):
    settings = Settings(
        start_url="https://example.test/favorites",
        detail_concurrency=12,
        image_concurrency=16,
        retry_count=2,
        database_path=tmp_path / "bookmarks.sqlite3",
        edge_profile_dir=tmp_path / "edge-profile",
        image_dir=tmp_path / "images",
        thumbnail_dir=tmp_path / "thumbnails",
    )

    async def raising_verify_then_crawl(*_args, **_kwargs):
        raise RuntimeError("真实 Edge 登录缓存正在被占用")

    monkeypatch.setattr(cli, "load_settings", lambda _path: settings)
    monkeypatch.setattr(cli, "verify_then_crawl", raising_verify_then_crawl)

    exit_code = cli.main(["--config", "missing.toml", "crawl"])

    captured = capsys.readouterr()
    assert exit_code == 1
    assert "真实 Edge 登录缓存正在被占用" in captured.err
    assert "Traceback" not in captured.err
