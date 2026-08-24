# R-fbm 发布指南

发布物只能包含程序、依赖和 Edge 扩展；不得包含收藏数据库、真实 URL、cookie、浏览器 profile、日志、截图或下载图片。

## 标准发布

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1
```

脚本会先执行 Release 构建、五套 console test runner 和扩展 JavaScript 语法检查，然后发布 framework-dependent `win-x64` 包到 `dist\win-x64`。自包含包使用：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\publish.ps1 -SelfContained
```

发布脚本只允许清理 `dist` 的子目录，并在结束时验证 `Rfbm.Native.exe` 与 `edge-extension\manifest.json` 都存在。

## 隐私与内容检查

```powershell
git status --short
git check-ignore data\bookmarks.sqlite3 config\settings.toml dist\win-x64\Rfbm.Native.exe
git grep -n -I -E "uid=[0-9]{4,}|Cookie:|Authorization: Bearer|C:\\\\Users\\\\" -- . ":(exclude)tests" ":(exclude)docs"
```

另需人工确认暂存区不包含：

- `*.sqlite*`、`*.db*`、`images/`、`edge-profile/`、`selector-profiles/`；
- `AGENT_PROJECT_SUMMARY.md`、本地规划/发现记录及真实论坛地址；
- `bin/`、`obj/`、`dist/` 和测试日志。

## 版本

- 用户可见变更写入 `CHANGELOG.md`。
- .NET/Edge 扩展版本保持一致后再打 `vX.Y.Z` tag。
- 旧 Python 包版本只代表 legacy 兼容层，不再决定桌面程序版本。
