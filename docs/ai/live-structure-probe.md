# 真实结构校准记录

日期：2026-09-22

本记录只保存脱敏后的 DOM/行为事实，不保存真实论坛地址、用户标识、Cookie、Authorization header、页面正文或截图。

## 已确认事实

- 目标站点使用 Discuz X3.4 风格页面。
- 收藏入口是 `home.php` 的 `space/favorite` 路由；实际分页会携带用户视图、类型和用户标识等查询参数，页码观测到 60 页以上。
- 需要登录的收藏路由可能返回“提示信息”页，并通过脚本跳转到 `member.php` 登录路由，而不是直接返回登录表单。
- 帖子标题的主题文本位于 `h1.ts #thread_subject`；分类前缀位于同一个 `h1.ts` 内的相邻链接，不能只读取 `h1` 的纯文本。
- 首楼正文的常见层级是 `#postlist → div#post_* → table.plhin → td.plc → .pct → .pcb → td.t_f`，正文节点通常带 `postmessage_*` id。
- 访问量可能使用小数和中文/英文数量后缀，例如 `433.3W`；解析器已统一转换为整数观察值。
- 分页链接带有站点原始查询参数；解析器生成后续页 URL 时保留这些参数，只替换 `page`。

## 代码落点

- `src/Rfbm.Core/Parsing/DiscuzPageParser.cs`：标题、分类、首楼和带后缀计数解析。
- `src/Rfbm.Collection/Playwright/PlaywrightSnapshotClassifier.cs`：Discuz 提示信息登录页识别。
- `tests/Rfbm.Core.Tests/Fixtures/discuz-x34-shape.html`：不含真实内容的结构回归样例。
- `tests/Rfbm.Core.Tests/Fixtures/favorites-page-1.html`：带合成查询参数的分页回归样例。

## 未自动化的边界

- 未读取或导出 Edge 默认 profile 的 Cookie/密码；登录、验证码和反自动化挑战仍需用户在可见 Edge 中完成。
- 未把真实页面 HTML、真实用户标识、真实资源链接或站点私密内容写入仓库。
- 真实环境首轮采集仍应使用独立应用 profile/扩展快照，并在本地抽样核对解析结果。
