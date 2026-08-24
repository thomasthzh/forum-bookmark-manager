# Changelog

本项目的重要用户可见变更记录在这里。

## Unreleased

### Added

- 原生 Windows WPF 收藏管理器，支持搜索、筛选、批量状态、星标、标签、笔记、回收站与随机重逢。
- 基于 Playwright + Edge 的 Discuz 收藏全量采集，支持持久断点、部分成功、取消恢复与有界并发。
- 正文、密码/提取码、资源链接上下文、收藏时间和最多 9 张正文图片的本地归档。
- Edge Manifest V3 手动快照与选择器标注桥接。
- 可选镜像与 Mihomo 本机线路恢复、旧 Python SQLite 只读导入。
- 五套离线 runner、扩展语法检查、Release 验证与 `win-x64` 发布脚本。

### Changed

- 默认产品主线从旧 Python Web 管理器重构为 `.NET 8 + WPF + SQLite + AngleSharp + Playwright .NET`。
- 帖子身份改为 `(site_family, thread_id)`，使主站、镜像和不同 URL 形态能够稳定去重。
- 采集提交改为带代际与期望图片清单的两阶段持久模型：原子 capture 后写入独立代际目录，再绑定同一代际幂等 finalize；新图未成功、超时或取消时保留旧好图及其物理文件。

### Security

- 不读取默认 Edge profile，不自动下载网盘、磁力、附件或资源文件。
- 登录与验证码仅允许用户在可见 Edge 中人工完成。
- 图片下载实施公开地址、重定向、MIME、文件签名、大小与原子写入校验。
- 扩展桥接仅监听 loopback，并校验 Origin、nonce、哈希和消息大小；原始 HTML 不落盘。

## 0.2.1 - 2026-05-16

- Removed real forum domain examples from public documentation, defaults, tests, and release assets.
- Reworked README to describe the project as a generic forum favorite crawler.
- Changed public example config to use placeholder domains and blank local proxy/controller values.
- Made Windows start/stop scripts work from a fresh checkout by setting `PYTHONPATH` and installing missing dependencies when needed.

## 0.2.0 - 2026-05-15

- Added mirror-site failover between a primary site and a mirror site.
- Added optional proxy-controller integration for region-based switching.
- Split post-content crawling from image downloading with separate worker queues.
- Increased default worker counts by three for favorite pages, post details, and images.
- Added a crawl dialog mirror URL field.
- Added a proxy status and switching panel to the web UI.
- Reworked post image previews to show the first two images larger, with a lightbox for zooming and browsing all images.
- Bumped package version to `0.2.0` for release preparation.
