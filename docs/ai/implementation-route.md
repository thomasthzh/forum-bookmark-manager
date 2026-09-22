# R-fbm 路线索引

日期：2026-08-24

## 权威文档

1. `docs/superpowers/specs/2026-08-24-rfbm-native-refactor-design.md`：需求、架构、不变量与验收。
2. `docs/ai/similar-projects-and-technical-route.md`：同类项目调研与技术路线。
3. `docs/superpowers/plans/2026-08-24-rfbm-native-refactor.md`：可执行 TDD 计划。
4. `docs/ai/tasks.md`：当前任务状态。
5. `docs/ai/progress.md`：验证与恢复记录。
6. `docs/ai/live-structure-probe.md`：真实站点结构校准的脱敏事实。

## 当前结论

主线为 `.NET 8 + WPF + SQLite + AngleSharp + Playwright .NET`。Playwright 是完整批量采集通道；Edge MV3 扩展负责人工快照与选择器标注；Mihomo 仅作为可选网络恢复适配器。旧 Python 实现和 72 个测试是迁移规格，Node 实验目录不进入主线。
