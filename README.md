# 一句 · Windows 桌面卡片

与 Android 端 [`app-word-widget`](https://github.com/flyingwenku/app-word-widget) 同款的桌面句子卡片，跑在 **Windows 11** 桌面上：夹在壁纸与桌面图标之间，可拖动、可缩放、点击换句、配色跟随壁纸。

## 功能

| 功能 | 说明 |
|---|---|
| 桌面卡片 | 钉在壁纸层（图标后面那一层），不占任务栏、不出现在 Alt+Tab |
| 拖动 / 缩放 | 直接拖卡片移动；拖右下角把手改大小（最小 160×70） |
| 点击换句 | 点一下卡片换下一句（固定句模式除外） |
| 自动排版 | **按容器尺寸实测文字宽高自动选字号**（WPF 能量真实文字，比 Android 端写死分档强） |
| 配色 | 读系统实际显示的壁纸 → 算亮度 → 卡片推到 0.85 / 0.15 两端 + 深/浅字（与 Android v1.1 同一套算法） |
| 句子管理 | 多个有名字的列表 + 句子增删改查，数据格式与 Android v1.2 一致 |
| 换句方式 | 随机下一句 / 顺序下一句 / 每日一句 / 固定这一句 |
| 托盘 | 管理句子 / 换一句 / 开机自启 / 退出 |

预置《论语》14 条，装完就能看效果。

## 运行

需要 **.NET 8 Desktop Runtime**（装过 .NET 8 SDK 就自带）。

```powershell
Yiju.exe
```

- 卡片出现在桌面（默认 140,140，380×190）
- 右下角托盘图标（深色方块 + 三道白线）→ 右键菜单
- 重复启动会被单实例挡住

数据在 `%LOCALAPPDATA%\Yiju\`：`lists.json`（句子库，与 Android 同格式，多了 `"v":2` 版本字段）、`config.json`（卡片位置/大小/模式）。

## 构建

```powershell
dotnet build src/Yiju -c Release
# 发布单文件 exe
dotnet publish src/Yiju -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o publish
```

CI：`.github/workflows/windows-build.yml`（windows-latest，产物为单文件 exe）。

## 实现要点（踩过的坑，都在代码注释里）

| # | 坑 | 处理 |
|---|---|---|
| 1 | **`Progman` 不是每台机器都存在**（实测有机器 `FindWindow("Progman")` 返回 0） | 只"有则用之"，绝不作为前提条件；候选 = 顶层 `WorkerW` + Progman 的子 `WorkerW` |
| 2 | `WorkerW` 按类名找不可靠（窗口类按进程注册，会重名） | 还要满足"可见 + 属于 explorer + 不含 `SHELLDLL_DefView`" |
| 3 | **WPF 无边框窗口是 `WS_POPUP`，`SetParent` 会静默失败**（返回成功但 `GetParent` 是 0） | 挂载前先把 `WS_POPUP` 换成 `WS_CHILD` |
| 4 | Explorer 重启 / WPF 自己重写窗口风格 → 卡片掉出壁纸层 | 收 `TaskbarCreated` 消息 + 每 5 秒检查（父窗口或风格不对就整个重挂） |
| 5 | 卡片可能盖住桌面图标 | 把 `WorkerW` 用 `SetWindowPos` 压到图标层之下 |
| 6 | 跨进程 SetParent 之后分层窗口（`WS_EX_LAYERED`）不可靠 | 圆角用 `SetWindowRgn` 切，不搞分层透明 |
| 7 | 混合 DPI | `app.manifest` 显式声明 PerMonitorV2（自带 manifest 时 `<ApplicationHighDpiMode>` 属性不生效） |
| 8 | 轮播壁纸 | 读 shell 实际显示的缓存（`TranscodedWallpaper` / `CachedFiles`），60 秒查一次是否更新 |
| 9 | 读写壁纸缓存文件会撞上系统写入 | `FileShare.ReadWrite`，失败重试退回纸色 |

**已知限制**：卡片只在主显示器上有效（壁纸层的坐标空间就是主屏）；未签名 exe 首次运行 SmartScreen 会拦一次。

## 路线

- **阶段一（本仓库）**：桌面卡片 + 本地数据 ✅
- **阶段二**：坚果云 WebDAV 同步（两端共用 `lists.json`，`If-Match` 乐观并发 + `PUT .tmp` 再 `MOVE` 原子覆盖），随后 Android 端（v1.4）接入互同步

评估与方案：`D:\coding\apk_project\桌面句子小组件-Windows版-评估与方案.md`
