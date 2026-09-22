# R-fbm Windows 原生重构设计

日期：2026-08-24

## 1. 产品定义

R-fbm 是一个单机、隐私优先的论坛收藏采集与再发现工具。它从用户已经有权访问的 Discuz 收藏页开始，完整遍历全部收藏分页和帖子详情，把结构化元数据与最多九张正文图片保存到本机，供搜索、整理、状态跟踪和再次浏览。

产品不是网盘下载器、验证码破解器、通用网页归档平台或多人云服务。附件、网盘、磁力与其他资源只保存链接及上下文，绝不自动下载；登录、验证码和权限检查始终由用户在可见 Edge 页面中完成。

## 2. 必须满足的需求

1. 中文 Windows 桌面界面；数据、配置、日志和图片均默认保存在 `%LocalAppData%\R-fbm`。
2. 从 Discuz 收藏页出发，验证并遍历 50–60 页或更多分页；不能把“抓到若干页”误报成完成。
3. 每帖提取标题、原始与规范 URL、分类、收藏时间、下载/浏览/收藏计数、正文摘要、提取码、所有资源链接及附近说明、最多九张有效正文图片。
4. 正文图片以限额、临时文件加原子改名方式下载到本地；资源文件本身永不自动下载。
5. 使用 SQLite，并支持搜索、分类/状态组合筛选、收藏时间排序、批量选择、状态修改、可恢复删除。
6. 人工状态循环为 `未下载 → 已下载 → 链接失效 → 未下载`；重新采集不能覆盖人工状态、星标、笔记、标签和已有可用图片。
7. 采集有可见进度、日志、失败项、并发设置、取消、部分成功与中断恢复。
8. 首次/失效登录时打开可见 Edge 验证；用户关闭验证页后，批量阶段可无头运行。
9. Edge 扩展提供当前页快照与选择器标注；Playwright .NET 是完整批量采集主通道。
10. 支持主站/镜像统一身份和可选 Mihomo/Clash REST 恢复；不得绕过权限或验证。
11. cookies、浏览器 profile、真实论坛 URL、数据库、图片、日志和本机配置不得进入 Git 或发布包。

## 3. 额外产品能力

在不扩大隐私和部署负担的前提下，本次一并纳入：

- 星标、笔记、标签和保存搜索条件。
- 智能视图：有提取码、有资源链接、有图片、疑似失效、最近新增、回收站。
- “随机翻一帖 / 灵感漫游”，让多年收藏重新可见。
- 软删除与回收站，避免误删数据库和本地图片。
- 采集运行历史、失败原因与可恢复工作项。
- 旧 Python SQLite 数据导入入口；导入不修改原库。

后续可选而不进入首版核心：链接健康巡检、时间线/统计、HTML 快照留存、本地 AI 标签与摘要、导出静态知识库。只有出现明确规模证据时才考虑全文搜索引擎、远程 worker 或多人服务。

## 4. 参考项目与取舍

| 项目 | 借鉴 | 明确不照搬 |
| --- | --- | --- |
| linkding | URL 规范化、标签、搜索、批量操作、轻量本地产品纪律 | Django 服务形态 |
| Karakeep | SQLite worker、资产管理、全文检索体验 | AGPL 源码、服务端/多用户栈 |
| Linkwarden | 归档状态、预览、链接组织体验 | PostgreSQL 与多人部署复杂度 |
| Shiori | 单机 SQLite、低依赖部署 | Go Web UI 技术栈 |
| ArchiveBox | 采集 manifest、失败与重试记录 | 全量 WARC/容器/服务栈 |
| SingleFile | 可选单页快照的边界 | AGPL 实现代码 |
| Browsertrix Crawler | 浏览器 profile、URL/内容去重、可恢复 crawl 思路 | Docker、Redis、WARC 管线 |
| Crawlee | 持久队列、session、retry 语义 | Node 运行时和通用框架体量 |

所有参考只用于行为和架构研究；不复制非兼容许可证代码。

## 5. 技术路线

```text
.NET 8
├─ Rfbm.Core          领域模型、Discuz 纯解析、身份/时间规范化
├─ Rfbm.Data          SQLite migrations、查询、原子 upsert、回收站、运行队列
├─ Rfbm.Collection    深模块：会话、分页闭包、调度、验证、恢复、图片本地化
├─ Rfbm.BrowserBridge loopback 接收器、Edge 扩展消息与来源校验
└─ Rfbm.Native        WPF 中文桌面产品

外部适配器
├─ PlaywrightEdgeAcquisitionAdapter  批量主通道
├─ EdgeMv3AcquisitionAdapter         人工快照/标注通道
└─ MihomoNetworkRecoveryAdapter      可选网络恢复通道
```

依赖选择：WPF 不需要常驻 WebView2；HTML 解析使用 AngleSharp 1.7；浏览器自动化使用 Microsoft.Playwright 1.62；数据库使用 Microsoft.Data.Sqlite 8。所有依赖通过项目边界隔离。

## 6. 深模块接口

WPF 只面对会话式 `ICollectionEngine`：

```csharp
public interface ICollectionEngine
{
    ValueTask<ICollectionSession> OpenAsync(
        CollectionIntent intent,
        CancellationToken cancellationToken = default);

    ValueTask<RunSummary?> FindResumeCandidateAsync(
        CancellationToken cancellationToken = default);
}

public interface ICollectionSession : IAsyncDisposable
{
    CollectionRunId Id { get; }
    IAsyncEnumerable<CollectionEvent> Events(CancellationToken cancellationToken = default);
    ValueTask RequestCancelAsync(CancellationToken cancellationToken = default);
    Task<CollectionResult> Completion { get; }
}
```

`OpenAsync` 的 token 只取消“建立会话”；事件 token 只停止观察；`DisposeAsync` 只释放观察句柄；只有 `RequestCancelAsync` 会取消采集。任何时刻最多一个 active run。

真正需要替换的 seam 只有页面采集、论坛格式和可选网络恢复。SQLite 不额外包装无意义的 repository interface；集成测试直接使用临时 SQLite。

## 7. 核心不变量

### 身份与分页

- 稳定帖子身份是 `(site_family, thread_id)`。主站、镜像、跳转和 URL 形态不能制造副本。
- Discuz `tid` 可从 query、伪静态 URL 或已验证页面元数据得到；无法确定时拒绝写入。
- 收藏页使用 durable frontier。声明总页数为 N 时必须验证 `1..N`；没有总页数时，持续跟进 next/已发现链接，直至 frontier 为空且终页明确没有 next。
- 任一分页永久失败只能是 `PartiallySucceeded` 或 `Interrupted`，并保留恢复点；绝不标成 `Succeeded`。

### 快照与更新

- 快照写入前验证大小、UTF-8、hash、origin、页面类型、帖子身份和结构。
- 登录页、403/429、验证码、空壳页、身份不匹配不能进入帖子 upsert。
- 每个字段使用三态观察语义：`ObservedValue`、`ObservedEmpty`、`Unobservable`。只有前两种允许修改旧值，避免抓取异常清空好数据。
- 一个短 SQLite transaction 原子完成帖子、当前 capture 代际、图片期望清单、资源链接、快照元数据与 work item checkpoint；图片安全落盘后，绑定同一代际的第二个短 transaction 才允许 finalize 并增加运行计数。
- 重新采集保留人工状态、星标、笔记、标签；新图片按数据库代际与独立磁盘目录暂存，新图成功落盘前每个位置仍读取旧代际的可用图片，旧 worker 不能写入或覆盖新代际。图片字段不可观察时原代际与 manifest 会被完整继承。

### 下载、安全与隐私

- 图片最多九张，按正文顺序去重；限制响应体大小与类型，写入临时文件后原子改名。
- 附件、网盘、磁力和其他资源永不下载，只记录 URL、标签与附近上下文。
- loopback 接收器验证 method、path、Host、Origin、Content-Type、nonce 和 payload 上限；日志不记录 cookie/token。
- 人工登录/验证码返回 `WaitingForUser`，不自动点击、输入、求解或规避。

### 取消与恢复

- 取消先持久化 `CancelRequested`，再停止派发并取消外部导航。
- transaction 已开始时完成短临界区，未开始则不写半条数据。
- 异常退出后的 active run 在下次启动时转为 `Interrupted`；未完成工作项可恢复。
- 单帖子失败不阻断其他帖子；状态变化和错误事件不丢失，高频进度可合并。

## 8. SQLite 设计

数据库启用 WAL、foreign keys、busy timeout 和版本迁移。v3 schema 为每次 capture 分配独立代际，并保存不可混淆的期望图片 manifest。关键表：

- `posts`：规范身份、抓取字段、人工字段、软删除时间。
- `post_images`、`resource_links`、`post_tags`。
- `selector_profiles`。
- `collection_runs`、`collection_frontier`、`collection_work_items`、`collection_events`。
- `schema_migrations`。

关键索引包括 `(site_family, thread_id)` 唯一索引、`(deleted_at, status, favorite_time_utc)` 查询索引、运行 work item 唯一索引和单 active run partial unique index。列表查询在 SQL 内分页、搜索和排序，不把全库读入内存。

## 9. UI 设计

桌面产品采用深色墨蓝工作区、暖金强调色和高对比卡片，不再保留灰白 probe 外观：

- 顶部：搜索、开始/恢复采集、连接状态、随机翻一帖。
- 左侧：状态、分类、标签、智能视图、回收站。
- 中部：可虚拟化、多选的帖子列表；标题、类型、状态、收藏时间、星标可快速操作。
- 右侧：正文摘要、提取码、资源链接上下文、九图缩略图、笔记与标签。
- 底部：当前运行阶段、可靠计数、错误/等待用户状态、取消入口。

空态不自动写假数据。首次运行展示三步引导：配置站点 → 可见 Edge 登录验证 → 开始完整采集。

## 10. 验收标准

1. Core/Data/Collection/BrowserBridge/Native 测试 runner 全部通过。
2. fixture 证明镜像 URL 去重、分页闭包、登录页拒绝覆盖、字段三态更新、状态保留、软删除恢复、取消/部分成功可恢复。
3. loopback 集成测试证明不可信 Host/Origin/超限 payload 被拒绝。
4. extension JavaScript 语法检查通过。
5. `dotnet restore`、Debug/Release build、Windows publish 通过且零警告。
6. WPF 真实启动、数据库为空时无假数据，关键界面可见且可操作。
7. 隐私扫描不包含 cookie、token、真实论坛域名/UID、数据库或媒体文件。

真实论坛的最终采集验收必须由用户在可见 Edge 中完成登录/验证码；自动化测试使用本地 HTML fixture，不尝试绕过该边界。
