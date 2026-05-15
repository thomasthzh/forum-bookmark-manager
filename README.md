# Forum Bookmark Manager

本工具用于把 Discuz 风格论坛的收藏页抓取到本地 SQLite 数据库，并用本地中文网页浏览、筛选、标记和查看图片。

## 功能

- 从你提供的收藏页链接开始自动翻页，抓取收藏帖。
- 保存标题、正文摘要、图片、解压密码、下载链接说明、分类、收藏时间和访问/下载/收藏次数。
- 图片下载到本地，网盘、磁力、附件等资源链接只保存文本，不自动下载。
- 支持主站与镜像站交替访问；镜像地址由本地配置或爬取弹窗填写。
- 支持可选的 Clash/Mihomo 兼容代理控制器联动，在站点无响应时尝试切换匹配线路。
- Web UI 支持状态筛选、类型筛选、搜索、批量标记、批量删除、大图预览和图片浏览。

## 安装

需要 Python 3.13+ 和 Microsoft Edge。

```powershell
python -m pip install -e .
```

运行测试：

```powershell
python -m pip install -e ".[dev]"
python -m pytest -q
```

## 配置

公开仓库只提供示例配置：

```text
config/settings.example.toml
```

首次使用时复制为本地私有配置：

```powershell
Copy-Item config/settings.example.toml config/settings.toml
```

然后把 `start_url`、`site_base_urls`、代理和控制器设置改成你自己的环境。`config/settings.toml` 已被 `.gitignore` 排除，不会提交到仓库。

## 打开软件

Windows 下可以直接双击：

```text
start_manager.bat
```

脚本会先设置本地 `src` 路径；如果依赖缺失，会自动执行 `python -m pip install -e .`。启动失败时窗口会停住并显示错误。

命令行方式：

```powershell
python -m forum_bookmark_manager.cli open
```

默认地址：

```text
http://127.0.0.1:53102
```

关闭后台服务：

```text
stop_manager.bat
```

或：

```powershell
python -m forum_bookmark_manager.cli stop
```

## 爬取流程

1. 打开本地 Web UI。
2. 点击 `开始爬取`。
3. 输入收藏页链接和可选镜像网址。
4. 在弹出的可见 Edge 窗口中手动完成登录或验证。
5. 关闭验证标签页后，程序会复用独立的 `data/edge-profile` 并开始无头爬取。

也可以直接命令行爬取：

```powershell
python -m forum_bookmark_manager.cli crawl --start-url "https://forum.example.test/home.php?mod=space&do=favorite&type=all&page=1"
```

## 本地数据

以下内容都是本地私有数据，默认不会进入 Git：

- `data/bookmarks.sqlite3`：SQLite 数据库
- `data/images/`：下载原图
- `data/thumbnails/`：缩略图
- `data/edge-profile/`：独立 Edge 登录态
- `data/selector-profile.json`：标注模式保存的选择器
- `config/settings.toml`：你的真实站点、镜像、代理和控制器配置

## 发布隐私

公开仓库只应包含源码、测试、文档和示例配置。发布前执行：

```powershell
git check-ignore data/bookmarks.sqlite3 data/edge-profile config/settings.toml
git grep -n -I -E "uid=[0-9]{4,}|C:\\\\Users\\\\" -- . ":(exclude)tests"
```

如需发布包：

```powershell
python -m build
```

## 注意

- 本工具不会绕过验证码、人机验证、年龄验证或站点反爬限制。
- 登录和验证必须由用户自己在可见浏览器窗口完成。
- 请只抓取你有权限访问和保存的内容。
