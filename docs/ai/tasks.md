# R-fbm 重构任务状态

日期：2026-08-24

## 已完成

- [x] 恢复旧 Python 需求、行为测试与隐私边界。
- [x] 调研同类收藏/归档/浏览器采集项目并确定技术路线。
- [x] 建立 `.NET 8 + WPF + SQLite + AngleSharp + Playwright .NET` 解决方案。
- [x] 稳定帖子身份与三态观察模型。
- [x] Discuz 收藏页、帖子页、密码、计数、图片、资源链接解析与拒绝页校验。
- [x] 版本化 SQLite schema、原子 capture、查询/筛选/排序、人工字段保护和旧库只读导入。
- [x] 持久 pagination frontier/work item、取消、崩溃恢复、部分成功和有界帖子并发。
- [x] 可见 Edge 人工验证、独立 profile、镜像 fallback 和可选 Mihomo 恢复。
- [x] 最多 9 张正文图片的大小/MIME/签名/公开网络/原子落盘防护。
- [x] Edge MV3 手动快照、选择器标注、loopback Origin/nonce/hash/size 校验。
- [x] Native 扩展桥接实际解析入库；选择器配置脱敏原子保存。
- [x] WPF 三栏管理器、搜索、筛选、批量状态、星标、笔记、标签、回收站和随机重逢。
- [x] 双击启动、Release 验证与 win-x64 发布脚本。

## 交付验收

- [x] 五套 Release test runner 全部通过。
- [x] Edge 扩展 JavaScript 语法检查通过。
- [x] `dotnet publish` 产物和打包扩展检查通过。
- [x] 在隔离数据目录启动 WPF 并人工检查主要窗口/对话框。
- [x] 隐私 grep 与 Git 暂存内容检查通过。
- [x] 独立代码复审无 Blocker/P1。

## 真实环境人工项

- [ ] 用户在目标论坛可见 Edge 中完成真实登录/验证码。
- [ ] 用户在 `edge://extensions` 加载发布包内扩展并回填扩展 ID。
- [ ] 用真实 50–60+ 页收藏执行首轮采集并抽样核对站点特有 DOM。

这些步骤需要用户的真实登录态或 Edge 交互，不由自动测试伪造，也不通过绕过安全验证完成。
