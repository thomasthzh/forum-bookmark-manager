# R-fbm AI 进度

日期：2026-09-22

## 当前状态

- 工作分支：`codex/rfbm-live-structure`
- 在独立 Git worktree 中开发，未覆盖主仓库的用户改动。
- 主仓库的用户未提交改动未被覆盖。
- 完整重构已进入最终隐私验收与提交阶段。
- 已按真实 Discuz X3.4 页面形状校准标题分类、首楼层级、带后缀计数、登录提示页和收藏分页参数；脱敏事实见 `docs/ai/live-structure-probe.md`。

## 已验证证据

- 原 Python 基线：`py -3.13 -m pytest -q` → 72 passed。
- `scripts/verify.ps1`：Release restore/build 0 warnings / 0 errors，Core、Data、BrowserBridge、Collection、Native 五套 runner 全部通过。
- `scripts/verify.ps1 -NoRestore`：再次通过；`dotnet format --verify-no-changes` 在当前 Windows 工作树命中既有全仓行尾/空格差异，未强行重排未改文件。
- Edge 扩展 `background.js`、`content.js` 语法检查：通过。
- Core、Data、BrowserBridge、Collection、Native runner 均在开发迭代中通过。
- Playwright 使用本机 Edge channel 获取 loopback fixture 成功。
- Collection 纵向切片已从 HTML 解析到 SQLite，并覆盖图片下载结果。
- 断点续采测试证明只请求失败页；并发测试证明线程详情有界并行。
- 扩展 receiver 测试覆盖 Origin、Host、nonce、payload、hash、MIME、method 和取消。
- Native 测试覆盖空首启、搜索、状态、星标/笔记/标签、回收站、随机重逢、扩展帖子导入与选择器脱敏保存。
- `scripts/publish.ps1`：framework-dependent `win-x64` 发布成功，含 Edge 扩展，共 135 个文件、111,100,837 字节。
- 以隔离 `RFBM_DATA_DIR` 启动发布版 WPF，人工检查主窗口、采集/Mihomo、扩展桥接与旧库导入对话框，进程正常退出。

## 恢复说明

- collection run、frontier 和 work item 与帖子数据共用 Data 拥有的版本化 schema。
- 新进程持有 collection lease 后才会回收遗留活跃 run；正在运行的另一实例不会被误判中断。
- 已提交帖子不会重复采集；失败/处理中断项会回到可重试状态。
- 收藏上下文、分页终止证据、计数器与 work 状态都持久化；恢复不会丢收藏时间或误报完整成功。
- 帖子、子资源、快照、图片期望清单与 work=`captured` 使用同一 SQLite 事务；每次 capture 有独立代际，图片安全落盘后再绑定该代际幂等 finalize 为 `committed`。
- 数据库图片代际与磁盘 capture 目录一一对应；新代失败、取消或 stale worker 不会覆盖旧代文件。图片字段不可观察时会连同原 manifest 复制到新代际，不会被误清空。

## 安全边界

- 不读取默认 Edge profile，不持久化 cookie/Authorization header。
- 登录、验证码与挑战页只进入可见人工流程。
- 镜像只处理可恢复导航失败；Mihomo 必须显式 opt-in 且控制器为回环 IP。
- 图片请求在实际运行中把 DNS 解析固定到公开地址，并逐跳验证重定向。
- 扩展 HTML 只在内存解析；选择器落盘时剔除 nonce 与 ticket。

真实站点验收仍需用户本人在可见 Edge 中登录或处理验证码；程序不会绕过这些安全步骤。
