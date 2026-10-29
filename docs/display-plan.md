# 一句 · Windows 端 显示（配色）方案 + 现状与证据

## ✅ 状态：已按"放弃 WorkerW"方案落地并验证（2026-10-29）

§3 里那些病（颜色不刷新、三张卡显示三种颜色、强杀留残影）**在换成普通置底工具窗口之后全部消失**。
这也反过来坐实了 H1：**根因就是跨进程 `SetParent` 到 Explorer 的 `WorkerW`（DWM 合成断层）**。

现在的做法：

```
卡片 = 普通顶层窗口
      + WS_EX_TOOLWINDOW       （不进任务栏 / 不进 Alt+Tab）
      + HWND_BOTTOM            （压在所有应用窗口底下，"贴桌面"）
      + AllowsTransparency=True（圆角交给 XAML，不再需要 SetWindowRgn）
      + 吞掉 WM_SYSCOMMAND / SC_MINIMIZE（Win+D 不跟着最小化）
```

实测结果（脚本 `%TEMP%\yiju_livecolor.ps1`，取卡片区域最暗/最亮 0.4% 的像素）：

| 方案 | 埋点算出的值 | 屏幕实测底色 | 屏幕实测文字 | 对比度 |
|---|---|---|---|---|
| 纸白 | bg `#F2FFFFFF` / text `#FF1F1F1E` | `(250,252,253)` | `(31,31,30)` ← 正好等于算出来的值 | 0.87 |
| 深夜 | bg `#F21F2124` / text `#FFFAF8F6` | `(37,41,45)` | `(250,248,246)` ← 正好等于算出来的值 | 0.82 |

三张卡片结果完全一致。**计算色 = 屏幕色** —— 这是改架构之前做不到的事。另外验证过：

- `Shell.Application.MinimizeAll()`（Win+D 走的就是这条路）→ 三张卡**全部还在**
- `Stop-Process -Force` 强杀 → 桌面三个位置主色占比只有 4~7.5%、颜色数上百到上千（= 普通壁纸照片），**没有残影**
- 卡片中心 `WindowFromPoint` 命中自己（`hitTestableAtCenter=True`）→ 鼠标消息能到

两处按 WPF 原生机制做调整的地方（与原方案不同）：

1. **没有上全局热键钩子拦 Win+D**，而是在 `WndProc` 里吞掉 `WM_SYSCOMMAND / SC_MINIMIZE`
   （`(wParam & 0xFFF0) == SC_MINIMIZE` → `handled = true`）。简单得多，不需要钩子，实测有效。
2. **没有用 `Binding ... RelativeSource={AncestorType=Window}`** 绑 `Background`，
   而是在 `Render()` 里直接给 `Card.Background` 赋值 —— 那条 Binding 还得往 Window 上再加个依赖属性，绕一层没必要。
3. 半透明 alpha 用 `#F2`（约 95%）而不是 `#E6`（90%），透得更克制；想要完全不透光就选带"实"的那两个方案。

下方 §0~§7 保留原文，作为"问题当时是怎么查出来的"记录。

---

> 只讨论"显示"：卡片底色、文字颜色、透明度。与句子内容、选取逻辑无关。
> 当前这篇文档的目的：**把已经查到的事实、未定位的根因、建议的方案**一次性交代清楚，方便别人接着查。

---

## 0. 项目概况

- WPF / .NET 8-windows 桌面小部件：把卡片窗口**挂到 Explorer 的桌面层（`WorkerW`）**，贴在壁纸上面显示。
- 关键文件：
  - `src/Yiju/App.xaml.cs` —— 多卡片管理、单实例
  - `src/Yiju/CardWindow.xaml(.cs)` —— 卡片渲染、拖动/缩放、右键菜单
  - `src/Yiju/Services/Palette.cs` —— 配色计算
  - `src/Yiju/Data/AppConfig.cs` —— 配置（`CardConfig` 每卡一份）
  - `src/Yiju/Native/DesktopHost.cs` —— 挂载/摘除桌面层
  - `src/Yiju/Native/Win32.cs` —— P/Invoke
- 埋点：`Trace`（写到 `%LOCALAPPDATA%\Yiju\trace.log`）。**开关**：该目录下放个空的 `trace.on` 文件才写日志。

## 1. 显示链路（颜色是怎么走到屏幕的）

```
AppConfig.Cards[i].TextColor / BgColor   （每卡一份配置，空=自动）
        │
        ▼
Palette.Get(bgHex, textHex)              （Services/Palette.cs）
        ├─ 自动：读壁纸主色 → 亮度推到 0.85(配深字) 或 0.15(配浅字)
        ├─ 只给了文字色：底色自动站到相反一端；对比度不足再推一档
        └─ 两个都给了：完全听用户的
        │
        ▼
CardWindow.Render()                      （CardWindow.xaml.cs:278）
        Card.Background      = SolidColorBrush(bg)
        TbSentence.Foreground= SolidColorBrush(text)
        TbSource.Foreground  = 同色，alpha 0xB3
        │
        ▼
WPF 渲染 → DWM 合成 → 屏幕
        ⚠ 窗口是 Explorer 进程里 WorkerW 的**跨进程子窗口**
```

## 2. 已经踩过并解决的坑（别重复劳动）

| 坑 | 现象 | 修法 |
|---|---|---|
| WPF 无边框窗口是 `WS_POPUP`，`SetParent` 对它**静默失败**（返回"成功"但 `GetParent` 变 0） | 卡片挂不上桌面 | 先把样式改成 `WS_CHILD` 再 `SetParent`（`DesktopHost.Attach`） |
| `FindWindow("Progman")` 不是每台机器都有 | 找不到挂载点直接 `return 0` | 改成"有则用之"，枚举所有候选 `WorkerW` |
| 桌面上有 16+ 个 `WorkerW`（别的软件也在用同一招） | 可能挂错层 | "可见 + 属于 explorer + 不含 `SHELLDLL_DefView`" 三重筛选 |
| `SHELLDLL_DefView`（图标层）铺满整屏，**吃掉所有鼠标消息** | 卡片能画出来但点不到、拖不动 | 挂载点改成 DefView 的**父窗口内部**并用 `SetWindowPos(HWND_TOP)` 置顶。注意：想把整个图标层压到我们之下时，`SetWindowPos` **返回 True 但实际不生效**（shell 强制维持层级），只在"同一父窗口内部"调整才有效 |
| 卡片窗口销毁时**桌面不重画** → 屏幕上留下点不掉的鬼影 | 退出后旧卡片还在 | 关闭前先 `DesktopHost.Detach()`（恢复 `WS_POPUP` + 父窗口置空），走普通窗口关闭路径 |
| 第二个实例弹框后进程不退出，握着互斥体不放 | 之后每次启动都判成"已在运行" | `Environment.Exit(0)` 硬退出 + 拿不到互斥体时再看有没有另一个 Yiju 进程（自愈） |
| `AppConfig` 只有带 `file` 参数的构造函数，`System.Text.Json` 当参数化构造用 → `file` 匹配不到属性 → **抛异常被 try/catch 静默吞掉** | **配置从来没被读进去过**，卡片位置每次都回到默认值 | 加 `[JsonConstructor] public AppConfig() { }` |

## 3. 当前问题（核心）：算出来的颜色 ≠ 屏幕上显示的颜色

### 3.1 实测数据

埋点（程序自己认为渲染的是什么颜色）——三张卡完全一致：

```
render card#1 cfgText='#FF000000' cfgBg='' -> bg=#FFCEDBDE text=#FF000000
render card#2 cfgText=''          cfgBg='' -> bg=#FFCEDBDE text=#FF1F1F1E
render card#3 cfgText=''          cfgBg='' -> bg=#FFCEDBDE text=#FF1F1F1E
```

抓屏统计（`@%TEMP%\yiju_livecolor.ps1`：**背景=最常见颜色，文字=最暗 0.4% 的均值**）：

| 卡片 | 屏幕上的底色 | 屏幕上的文字笔画 | 结论 |
|---|---|---|---|
| card#1 | `(255,255,255)` 纯白 | `(94,106,103)` 深灰 | 底色不符（应为 `206,219,222`），文字勉强可读 |
| card#2 | `(255,255,255)` 纯白 | `(163,199,212)` **浅蓝** | 底色不符，**文字几乎看不见** |
| card#3 | `(242,242,242)` 浅灰 | `(47,47,47)` 深灰 | 底色不符，文字可读 |

三张卡**配置完全相同、算出来的颜色完全相同**，屏幕上却三种不同结果 —— 这是本报告要请人重点看的地方。

用户截图（`assets/image.4bf6ed935f.png`，同样按比例统计）也是同一现象：卡片纯白 `(255,255,255)`，整个区域里**最暗的 0.2% 像素都只有 `(135~150,181~187,197~203)`**（浅蓝），也就是说**一个深色像素都没有**。

### 3.2 另一个相关现象：颜色会自己跳

同一天 09:07~09:08 的埋点里，11 秒之内自动配色翻了三次：

```
09:08:19  bg=#FF1F2925  text=#FFFAF8F6   （深绿）
09:08:24  bg=#FF4A184E  text=#FFFAF8F6   （深紫）
09:08:30  bg=#FFCEDBDE  text=#FF1F1F1E   （浅蓝灰）
```

这台机器的壁纸是 **Windows Spotlight**（`DesktopSpotlight\Assets\Images\image_0.jpg`），会自动轮换。
`getWallpaper` 的取值顺序（`Palette.CurrentWallpaperPath`）是：
1. `SPI_GETDESKWALLPAPER` 返回的路径
2. `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`（缓存副本）
3. `Themes\CachedFiles` 里最新那个文件

**这三个文件内容不一定同一张图**，路径解析在不同时刻落到不同文件 → 取到的主色不同 → 颜色跳。这是"颜色完全乱了"的直接来源之一。

## 4. 待验证的根因假设（请优先看这段）

### H1：跨进程子窗口的 **DWM 表面没有跟着 WPF 更新**（陈旧帧）
- 支持证据：三张卡同一时刻代码路径一致、配置一致，屏幕上却显示**三种不同且都不能用新配色解释**的结果；其中 card#2 显示的正是"**旧**一版配色"（浅地深/浅字）的记忆。
- 与"退出时留下鬼影"很可能是**同一个病**的不同表现：`<WorkerW>` 这一层的重画本来就不可靠。
- 可验证：改完颜色后看屏幕是否滞后；或在 `Render()` 后强制重画（`HwndSource.RootVisual.InvalidateVisual()` / `InvalidateRect` / 或者 `SetWindowPos` 微调触发重画）。

### H2：配色源不稳定 + 重算太频繁
- `WM_SETTINGCHANGE`（壁纸变更广播）→ `Palette.Refresh()` + `Render()`。如果有别的东西频繁广播这条消息，配合 §3.2 的路径不稳定，就会来回跳。
- 可验证：在 `Render()` 里埋点记"当前读到的是哪个文件路径"，连打几十次看路径是否交替。

### H3：XAML / 渲染层（**基本可排除**，供确认）
- `Window.Background="#FFB8B6B5"`、`Border Background="#FFB8B6B5"`，`AllowsTransparency="False"`，没设 `Opacity`。
- `FitText()` 里的 `FormattedText` 用 `Brushes.Black`，但**只用于测量**尺寸，不影响实际显示色。

## 5. 建议的显示方案（要实现的）

目标：**颜色稳定**（不随壁纸轮换跳）、**始终可读**、可选"半透明"质感；且**所有卡片统一**（多卡片时不能一张黑字一张白字）。

### 5.1 固定配色方案，默认不依赖壁纸

```csharp
// Services/Palette.cs —— 新增
public static readonly (string Name, string Bg, string Text)[] Schemes =
{
    ("纸白",     "#EEFFFFFF", "#FF1F1F1E"),  // 默认：白底 + 深色字，略透
    ("纸白·实",  "#FFFFFFFF", "#FF1F1F1E"),  // 完全不透
    ("深夜",     "#E61F2124", "#FFFAF8F6"),
    ("深夜·实",  "#FF1F2124", "#FFFAF8F6"),
    ("跟随壁纸", "",          ""),            // 保留原行为
};
```

要点：
- **半透明用背景色的 alpha 通道（`#EEFFFFFF`）实现，不要设 `UIElement.Opacity`** —— `Opacity` 会把文字一起弄淡，这正是"看不清"的来源之一；背景用 alpha、文字保持完全不透明，才能保证可读。
- 默认"纸白"（白底深字）：在任何壁纸上都可读，符合用户想要的"通透 + 字清楚"。

### 5.2 配置结构

```csharp
// AppConfig —— 全局，所有卡片共用，保证一致
public int ColorScheme { get; set; } = 0;     // 索引到 Palette.Schemes

// CardConfig —— 保留单卡覆盖能力，默认空（跟随全局）
public string TextColor { get; set; } = "";   // 已有
public string BgColor   { get; set; } = "";   // 已有
```

迁移：旧配置里没有 `ColorScheme` → 默认 0（纸白）。**建议顺手清掉**之前为测试写进 `card#1` 的 `TextColor: "#FF000000"`，否则它就是那个"只有一张卡是黑字"的乱源。

### 5.3 渲染改动（`CardWindow.Render()`）

```csharp
var scheme = Palette.Schemes[_app.ColorScheme];
var (bg, text) = Palette.Get(
    string.IsNullOrEmpty(_card.BgColor)   ? scheme.Bg   : _card.BgColor,
    string.IsNullOrEmpty(_card.TextColor) ? scheme.Text : _card.TextColor);

Card.Background       = new SolidColorBrush(bg);
TbSentence.Foreground = new SolidColorBrush(text);
TbSource.Foreground   = new SolidColorBrush(Color.FromArgb(0xB3, text.R, text.G, text.B));
```

### 5.4 降低颜色抖动

- `WM_SETTINGCHANGE` 到达时，**先 `Palette.WallpaperChanged()`（比对文件 mtime）确实变了再 `Refresh()`**，而不是无条件重算。
- 只有选中"跟随壁纸"方案时才需要取色，其余方案完全不读壁纸文件（顺带省掉磁盘 IO）。

### 5.5 菜单

- 卡片右键菜单保留「文字颜色 / 卡片底色」，另加「配色方案 →」子菜单（作用于**所有卡片**，保证统一）。
- 托盘菜单同样加「配色方案 →」。

## 6. 验收方法（脚本已在，可复现）

- `%LOCALAPPDATA%\Yiju\trace.log` —— `render card#N ... -> bg=#XXXXXXXX text=#XXXXXXXX` 是"程序认为的颜色"。
- `%TEMP%\yiju_livecolor.ps1` —— 抓每张卡的屏幕像素，输出"底色（最常见）"和"文字笔画（最暗 0.4% 均值）"。
- **判据**：对所有卡片，`底色亮度 > 0.6 且 文字亮度 < 0.35` 才算可读；并且 "屏幕上的底色" 必须等于 "埋点里的 bg"。

## 7. 需要对方确认的问题

1. **H1（陈旧帧）还是 H2（取色源不稳定）是主因**？—— 两者都会让"颜色看起来不对"，修法完全不同：前者要在渲染完成后强制让跨进程子窗口重画，后者只要固定配色就消失。
2. 如果 H1 成立，是否考虑**放弃 WorkerW 挂载**，改成普通 `ToolWindow` 置 `HWND_BOTTOM`？
   - 优点：正常窗口，渲染/重画/输入法都可靠，鼠标消息也没问题。
   - 代价：`Win+D` 会把它一起最小化（现在这种挂法能跟着桌面留下来）。
3. 半透明是否只用背景 alpha、文字永远不透明？（我的建议：是）
