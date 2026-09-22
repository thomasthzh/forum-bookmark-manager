# R-fbm · 论坛藏宝阁

R-fbm 是一个 Windows 本地论坛收藏管理器：完整遍历 Discuz 收藏分页，把帖子正文信息、最多 9 张正文图片、密码/提取码、资源链接及上下文保存到 SQLite，再用原生桌面界面搜索、整理和重新发现。

当前主线已经从旧 Python Web 管理器重构为：

```text
.NET 8 + WPF + SQLite + AngleSharp + Playwright .NET
                              + Edge MV3 手动快照/标注
                              + 可选 Mihomo 线路恢复
```

## 已实现

- 以 `(site_family, thread_id)` 识别主站、镜像、query URL 与伪静态 URL 中的同一帖子。
- 证明式遍历全部收藏分页；缺页不会伪报成功。
- 持久化 frontier/work checkpoint，崩溃、取消或部分失败后只重试未完成项。
- 帖子、子资源、快照与 checkpoint 先原子 capture，图片按 capture 代际隔离落盘后再幂等 finalize；中途取消、超时或 stale worker 不会覆盖旧好图，也不会把半成品误标为完成。
- 首楼解析：标题、分类、收藏时间、浏览/收藏/下载计数、正文、解压密码/提取码、资源链接和附近说明。
- 只下载帖子正文图片，最多 9 张；绝不自动下载网盘、磁力、附件或资源链接。
- 图片大小、MIME、文件签名、公开网络地址和原子写入校验。
- 可见 Edge 人工登录/验证码；批量阶段使用 R-fbm 独立 profile，不读取默认 Edge profile。
- 主站瞬态网络失败后尝试已配置镜像；全部失败后才可选择调用本机 Mihomo。
- 中文三栏 WPF 管理器：全文搜索、类型/状态/星标筛选、批量状态、标签、笔记、回收站、随机重逢。
- Edge 扩展桥接：手动帖子/收藏页快照直接解析入库，选择器配置原子保存；原始 HTML 不落盘。
- 旧 Python SQLite 只读导入，不修改源数据库。

## 快速开始

开发环境需要 Windows 10/11、Microsoft Edge 和 .NET 8 SDK。双击 `start_manager.bat`，或运行：

```powershell
dotnet run --project src\Rfbm.Native\Rfbm.Native.csproj
```

本地数据默认存放在：

```text
%LocalAppData%\R-fbm\
├── rfbm.sqlite3
├── images\
├── edge-profile\
├── selector-profiles\
└── logs\
```

测试或便携场景可在启动前设置 `RFBM_DATA_DIR`，把全部运行数据隔离到指定目录。运行数据、cookie、浏览器 profile、日志和图片均被 Git 忽略。

## 构建、验证与发布

```powershell
dotnet restore R-fbm.sln
dotnet build R-fbm.sln -c Release --no-restore
powershell -ExecutionPolicy Bypass -File scripts\verify.ps1 -NoRestore
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

发布结果位于 `dist\win-x64\`，默认需要已安装 .NET 8 Desktop Runtime。需要自包含包时使用：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained
```

项目使用轻量 console 测试 runner，以避开本机 VSTest testhost loopback 不稳定问题。验证脚本会运行 Core、Data、BrowserBridge、Collection、Native 五套 runner，以及 Edge 扩展 JavaScript 语法检查。

## 批量采集

1. 在主界面点击“开始采集”。
2. 填入论坛收藏页 URL；站点标识留空时会按主机名生成。
3. 如有镜像，每行填一个 origin，例如 `https://mirror.example.com`。
4. 推荐保留“先打开可见 Edge 完成登录验证”。完成登录或验证码后关闭验证页，程序才进入批量阶段。
5. 可选展开 Mihomo 设置。控制器必须是显式回环 IP（如 `127.0.0.1`）；密钥只存在于当前内存会话。

程序会显示分页、帖子、失败计数和等待人工操作状态。取消和关闭窗口都会保留可恢复检查点。

## Edge 手动桥接

1. 点击主界面“扩展桥接”，复制界面显示的扩展目录。
2. 打开 `edge://extensions`，启用开发人员模式，选择“加载解压缩的扩展”。
3. 把 Edge 显示的 32 位扩展 ID 和稳定站点标识填回 R-fbm，启动本地桥接。
4. 在论坛页点击扩展图标发送当前页；或右键选择“为 R-fbm 标注页面区域”。

接收器仅监听 `127.0.0.1:53112`，要求精确的 `chrome-extension://<ID>` Origin、随机会话 nonce、大小和 SHA-256 校验。扩展不申请 cookie 权限，只在用户点击时读取当前标签页。

## 架构

- `src/Rfbm.Core`：稳定身份、三态观察值、Discuz 解析与页面验证。
- `src/Rfbm.Data`：版本化 SQLite schema、查询、原子 upsert、回收站和旧库导入。
- `src/Rfbm.Collection`：采集会话、持久 frontier、并发、恢复、Playwright、镜像/Mihomo 和图片安全。
- `src/Rfbm.BrowserBridge`：受来源约束的 loopback 消息协议。
- `src/Rfbm.Native`：WPF 产品界面和桌面工作流。
- `extension/edge`：Manifest V3 手动快照与选择器标注。

详细需求与不变量见 `docs/superpowers/specs/2026-08-24-rfbm-native-refactor-design.md`，实施步骤见 `docs/superpowers/plans/2026-08-24-rfbm-native-refactor.md`，同类项目与技术取舍见 `docs/ai/similar-projects-and-technical-route.md`。

## 明确不做

- 不绕过验证码、登录或站点安全策略。
- 不自动下载网盘、磁力、附件和帖子中的资源文件。
- 不读取或复用用户默认 Edge profile。
- 不把本地收藏、cookie、HTML、日志或图片上传到云端。
- 不复制 AGPL 项目的实现代码；同类项目仅用于产品与架构参考。

旧 Python 源码与 72 个测试暂时保留为迁移规格和兼容参考，不再是默认运行入口。
