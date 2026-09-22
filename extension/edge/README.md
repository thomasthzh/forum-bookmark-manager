# R-fbm Edge Bridge

这个 MV3 扩展只在用户点击工具栏或右键命令时读取当前标签页，不会常驻扫描浏览历史，也不申请 cookies 权限。

## 功能

- 工具栏按钮或“发送当前页到 R-fbm”：发送最多 2 MB 的完整 DOM 快照，并附带 UTF-8 大小、SHA-256、请求/最终 URL、页面类型、时间与一次性 ticket。
- “为 R-fbm 标注页面区域”：点击收藏列表、帖子正文、图片、资源链接等区域，生成 CSS selector profile 并发送给本地程序。
- 每次发送前从 `127.0.0.1:53112` 获取当前随机 session nonce；所有 fetch 都使用 `credentials: "omit"`。

## 本地加载

在 R-fbm 主界面点击“扩展桥接”并复制打包后的扩展目录；再到 Edge 的 `edge://extensions` 开启开发人员模式，选择“加载解压缩的扩展”。把页面显示的扩展 ID 和稳定站点标识填回 R-fbm。

桌面端会启动只允许该精确 `chrome-extension://<扩展 ID>` origin 的接收器并监听 `127.0.0.1:53112`。帖子/收藏页快照在内存解析后写入 SQLite，原始 HTML 不落盘；选择器配置落盘时不会包含 nonce 或 ticket。扩展没有远程服务、cookie API 或资源文件下载能力。
