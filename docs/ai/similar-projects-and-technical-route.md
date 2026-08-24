# R-fbm 同类项目调研与技术路线报告

日期：2026-08-24

## 结论

保留已经确定的 Windows 原生方向，并把验证路线收敛为正式产品架构：

```text
.NET 8 + WPF + SQLite + AngleSharp + Playwright .NET
                    + Edge MV3 人工快照/标注适配器
                    + 可选 Mihomo REST 恢复适配器
```

Playwright 是完整批量采集主通道；Edge 扩展是利用现有登录态进行人工补录和选择器标注的辅助通道。WPF 不常驻 WebView2，从而降低内存、更新面和 UI/采集耦合。

## 调研项目

- [linkding](https://github.com/sissbruecker/linkding) — 采用轻量、URL 规范化、标签/搜索/批量操作；不采用 Web 服务产品形态。
- [Karakeep](https://github.com/karakeep-app/karakeep) — 采用资产生命周期、SQLite worker、全文搜索体验；不复制 AGPL 代码，不引入多人服务栈。
- [Linkwarden](https://github.com/linkwarden/linkwarden) — 采用链接归档状态和详情体验；不引入 PostgreSQL 与组织/权限系统。
- [Shiori](https://github.com/go-shiori/shiori) — 采用本地 SQLite、低依赖、单机优先的产品纪律。
- [ArchiveBox](https://github.com/ArchiveBox/ArchiveBox) — 采用每次采集可审计 manifest、retry 与失败记录；不采用 WARC/Redis/Docker 栈。
- [SingleFile](https://github.com/gildas-lormeau/SingleFile) — 作为未来可选的单页快照能力参考；不复制 AGPL 实现。
- [Browsertrix Crawler](https://github.com/webrecorder/browsertrix-crawler) — 采用浏览器 profile、去重和恢复语义；不采用容器化分布式归档管线。
- [Crawlee](https://github.com/apify/crawlee) — 采用持久队列、session、retry 的行为模型；不采用 Node 主运行时。
- [Playwright](https://github.com/microsoft/playwright) — 直接采用官方 .NET API、可见 Edge 登录与后续受控无头上下文。

## 为什么不继续 Python/Node 主线

旧 Python 版有 72 个通过的测试、可复用的解析行为和兼容 schema，是迁移规格，不是要丢弃的成果。但现有 Web 管理器的任务生命周期、分页完成语义、原子更新、进程控制与本地安全边界都需要大改。Node 目录是未经测试的逐行实验移植，并存在队列、解析和数据库并发问题，不适合作为主线。

.NET 原生方案同时满足已选择的 Windows 软件形态、SQLite 原子性、Playwright .NET、单进程任务会话和中文桌面管理体验；这是本次重构主线。

## 已落地的产品能力

本次已经实现：完整 schema、Discuz 解析、稳定身份、采集会话/恢复、loopback 接收器、图片本地化、搜索筛选、状态/星标/笔记/标签、回收站、随机再发现、运行进度、Edge 快照与标注、旧库只读导入、镜像/Mihomo 路由。

## 在不破坏原需求的前提下，还能长成什么

### 第一层：最值得继续做

1. **资源链接健康巡检**：只做 HEAD/轻量探测和历史状态，不下载资源；把失效、需登录、限流分开显示。
2. **保存搜索与智能书架**：把“星标 + 游戏 + 有密码 + 最近一年”等查询保存成动态合集。
3. **重复与镜像合并**：基于稳定 thread key、资源 URL 指纹和图片感知 hash 提示重复收藏，不自动删除。
4. **加密备份/导出**：导出 SQLite、标签、笔记和本地图片清单；默认剔除 cookie/profile，可选用户口令加密。
5. **站点 adapter 包**：把 Discuz 解析接口稳定下来，再按 fixture 增加 XenForo、phpBB 或特定论坛适配器。

### 第二层：让它更有趣

- **数字考古模式**：按“收藏于 X 年前”“从未打开”“曾经失效又复活”每日抽取旧帖。
- **藏宝图**：以标签、资源站域名、帖子类型形成可点击关系图，发现收藏中的主题簇。
- **时间胶囊**：按月/年展示收藏轨迹、最常见类型和资源域名；所有统计在本机计算。
- **整理成就**：只记录本地整理动作，例如“补完 100 条标签”“清理 20 条失效链接”，不做在线排行榜。
- **主题陈列柜**：把一组收藏生成本地静态展页或离线目录，发布前显式检查密码和私密 URL。
- **图片画廊/联系表**：只使用已经允许下载的正文图片，生成封面墙和相似图片分组。

### 第三层：可选智能能力

- 本地模型生成摘要、建议标签和类型，但只作为候选，人工确认后写入。
- 对资源链接上下文做实体识别，自动归纳“网盘/磁力/附件/教程/补丁”，仍不下载资源。
- 根据失败历史建议更稳的并发、等待时间和镜像顺序，不自动改变安全边界。

## 继续扩展时不可破坏的红线

- 不绕过登录、验证码或反自动化挑战。
- 不自动下载网盘、磁力、附件和资源文件。
- 不把本地收藏、HTML、cookie、profile 或密码发送到云端。
- 不让 AI 覆盖人工状态、笔记、标签和星标。
- 不因单一镜像或一次错误响应覆盖已经保存的可靠字段。

默认不做云同步、多人协作、移动端、验证码求解、WARC 全站归档和分布式爬虫；这些会显著改变产品隐私与复杂度，而不是“顺便增加功能”。
