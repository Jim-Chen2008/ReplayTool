# ShadowPlay 即时回放复刻 — 完整实现方案

## Context

在 `d:\PROJECT\replay` 从零构建一款 Windows 桌面即时回放录制工具，完整复刻 NVIDIA GeForce Experience 的 Instant Replay（即时回放）功能。目标：无 NVIDIA 显卡依赖，全品牌显卡兼容（AMD/Intel 核显），实现 7×24 后台环形缓存录制 + 快捷键截取回溯视频。

当前目录为空，全新项目。

## 技术栈决策

**选定：C# .NET 9 + WPF + FFmpeg**

| 维度 | 评估 |
|------|------|
| Windows 原生托盘 | .NET NotifyIcon (零额外依赖) |
| 全局热键 | Win32 RegisterHotKey + SetWindowsHookEx |
| 画面捕获 | Windows.Graphics.Capture API (Win10 1903+) via CsWinRT |
| 编码引擎 | FFmpeg 子进程管理，自动检测 QSV/VCN/libx264 |
| 内存占用 | .NET 原生 AOT / self-contained 发布，运行时 < 200MB |
| UI 构建 | WPF（设置面板 + 图库浏览器 + 覆盖指示器） |

**不选其他技术栈的原因：**
- Electron：内存 >300MB，不满足 <200MB 要求
- Python + PyQt：Windows 热键库不可靠，WGC 绑定不成熟
- Rust：UI 开发周期长，WinRT 绑定生态弱

## 整体架构

```
ReplayTool/
├── ReplayTool.sln                         # 解决方案
├── src/
│   ├── ReplayTool.Core/                   # 核心逻辑库（无 UI 依赖）
│   │   ├── Models/
│   │   │   ├── AppSettings.cs             # 序列化设置模型
│   │   │   ├── CaptureConfig.cs           # 捕获源/分辨率/帧率配置
│   │   │   ├── EncoderConfig.cs           # 编码器/码率/格式配置
│   │   │   ├── HotkeyConfig.cs            # 快捷键绑定
│   │   │   └── ReplaySession.cs           # 回放保存会话信息
│   │   ├── Services/
│   │   │   ├── SettingsManager.cs         # JSON配置读写 (%APPDATA%/ReplayTool/)
│   │   │   ├── CaptureService.cs          # 画面+音频捕获核心 (WGC/DXGI)
│   │   │   ├── RingBufferService.cs       # 环形缓存管理 (FFmpeg分段)
│   │   │   ├── EncoderService.cs          # FFmpeg进程生命周期管理
│   │   │   ├── HotkeyManager.cs           # 全局快捷键注册/监听
│   │   │   ├── AudioCaptureService.cs     # 系统音频+麦克风捕获
│   │   │   ├── ReplayExporter.cs          # 从环形缓存提取并导出MP4
│   │   │   ├── ScreenshotService.cs       # 截图保存
│   │   │   ├── DiskMonitor.cs             # 磁盘空间监控+预警
│   │   │   └── RecoveryService.cs         # 崩溃恢复（保留未保存片段）
│   │   └── Native/
│   │       ├── Win32.cs                   # P/Invoke (RegisterHotKey etc.)
│   │       └── WgcInterop.cs              # Windows.Graphics.Capture 互操作
│   └── ReplayTool.App/                    # WPF 桌面应用
│       ├── App.xaml / App.xaml.cs         # 应用入口，单实例控制
│       ├── Views/
│       │   ├── TrayIconService.cs         # 系统托盘图标+右键菜单
│       │   ├── SettingsWindow.xaml        # 完整参数设置面板
│       │   ├── GalleryWindow.xaml         # 图库浏览器（保存的视频/截图）
│       │   ├── OverlayWindow.xaml         # 录制状态遮盖指示器
│       │   └── ToastNotification.xaml     # 回放保存提示弹窗
│       ├── ViewModels/                    # MVVM 绑定
│       └── Resources/                     # 图标、字体等
├── libs/
│   └── ffmpeg/                            # 捆绑的 FFmpeg 可执行文件
├── .gitignore
└── README.md
```

## 核心模块设计

### 1. 环形缓存录制 (RingBufferService)

**核心策略：FFmpeg segment muxer + wrap 机制**

```
ffmpeg -f gdigrab / dshow / dxgigrab
  -video_size {W}x{H} -framerate {FPS}
  -c:v {encoder} -b:v {bitrate} -preset {preset} -pix_fmt yuv420p
  -f segment -segment_time 1 -segment_format mpegts
  -segment_wrap {max_segments}
  -reset_timestamps 1
  {temp_dir}/seg_%05d.ts
```

- `segment_time=1`：每个 TS 片段 1 秒（最大粒度保证 "往前X秒~当前时刻" 的精度）
- `segment_wrap`：环形覆盖，例如 5 分钟 × 60 段/分钟 = 300 段，`segment_wrap=300`
- 临时缓存大小：1 秒 ≈ 5-10MB (50Mbps) → 300 秒 = 1.5-3GB 固定磁盘占用
- **保存触发时**：解析 FFmpeg segment_list 文件 → 定位最近 N 个完整段 → concat demuxer 导出 MP4 → 立即重启 FFmpeg 继续录制

**关键点：零间隙切换**
- 收到保存信号 → 立即启动新 FFmpeg 录制进程
- 新进程就绪后 → 发送 'q' 到旧进程优雅退出
- 两进程并行约 200ms，实际丢帧窗口 < 100ms

### 2. 画面捕获 (CaptureService)

**主方案：Windows.Graphics.Capture API (Win10 1903+)**
- 通过 CsWinRT NuGet 包调用 WinRT API
- 支持：全屏/单显示器/指定窗口/进程独占
- 优势：GPU 零拷贝，支持游戏全屏独占模式

**降级方案：DXGI Desktop Duplication API**
- 通过 SharpDX / DirectX.NET 包装
- 兼容 Win8+，稳定但性能略低于 WGC

**BitBlt 兜底：** 兼容老旧设备（Win7），通过 GDI32 截图，性能最差

### 3. 编码引擎选择 (EncoderService)

自动检测流程：

```
检测硬件编码器 (nvidia-smi不检测):
1. Intel QuickSync (h264_qsv / hevc_qsv) ← 可用则优先
2. AMD VCN (h264_amf / hevc_amf)
3. 无硬件编码 → libx264 / libx265 (软件)
```

- 所有硬编码器均为非 NVIDIA 方案，完全不依赖 NVENC
- FFmpeg 自动重试：主编码器失败 → 降级 soft
- 软编码时 CPU 占用监控，动态调节 -preset (ultrafast → veryfast → medium)

### 4. 音频捕获 (AudioCaptureService)

**Windows WASAPI Loopback** (via FFmpeg dshow/virtual-audio-capturer):
- 系统主声道：`-f dshow -i audio="Stereo Mix"` 或 WASAPI Loopback
- 麦克风：`-f dshow -i audio="Microphone"`

双音轨方案：两个独立 FFmpeg 输入流，封装时可选合并或分离：
- 合并：`-filter_complex "[1:a]adelay=... [mic]; [0:a][mic]amix=inputs=2"`
- 分离：`-map 0:a -map 1:a` 输出双 track MP4

### 5. 全局快捷键 (HotkeyManager)

**核心实现：Win32 RegisterHotKey**
```csharp
[DllImport("user32.dll")]
static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
```

- 托盘图标的 `Form` (WinForms) 或 WPF `HwndSource` 作为热键消息宿主
- 默认绑定：Alt+F10 (保存回放), Alt+F9 (手动录制), Alt+F1 (截图)
- 热键冲突检测：RegisterHotKey 返回 false 时弹窗提示
- 自定义改键：WPF 按键捕获控件，序列化到 AppSettings

### 6. 覆盖指示器 (OverlayWindow)

WPF 透明顶层窗口：
- `AllowsTransparency="True"` + `WindowStyle="None"` + `Topmost="True"`
- 右下角小圆点：绿色=正常录制，红色=正在保存，灰色=暂停
- 颜色通过 ViewModel 绑定更新
- 可关闭（设置中开关）

### 7. 设置面板 (SettingsWindow)

完整 Tab 分类（对标 N 卡 GE 面板）：
1. **回放**：回溯时长、临时缓存目录、磁盘上限
2. **视频**：分辨率、帧率、码率档位、编码器选择
3. **音频**：系统音频开关、麦克风开关、降噪
4. **快捷键**：保存回放、手动录制、截图、面板呼出
5. **高级**：HDR、色彩增强、锐化、启动行为、省电模式

设置持久化：`%APPDATA%/ReplayTool/settings.json`

### 8. 图库浏览器 (GalleryWindow)

- 平铺网格展示所有保存的 .mp4 和 .png
- 缩略图：视频取首帧，图片原始缩略
- 右键菜单：打开位置、复制路径、删除、快速裁剪
- 呼出：托盘菜单 "打开图库"

## 编码流程总览

```
┌─────────────────────────────────────────────────────────┐
│                    ReplayTool.App (WPF)                  │
│  ┌──────────┐  ┌───────────┐  ┌──────────────────────┐ │
│  │TrayIcon  │  │Overlay    │  │Settings/Gallery      │ │
│  │Service   │  │Window     │  │Windows               │ │
│  └────┬─────┘  └─────┬─────┘  └──────────┬───────────┘ │
│       │               │                    │             │
│  ┌────┴───────────────┴────────────────────┴──────────┐ │
│  │              Core Services (Singleton)              │ │
│  │  ┌──────────────┐  ┌─────────────┐  ┌───────────┐  │ │
│  │  │CaptureService│  │RingBuffer   │  │Encoder    │  │ │
│  │  │(WGC/DXGI)    │  │Service      │  │Service    │  │ │
│  │  └──────┬───────┘  └──────┬──────┘  └─────┬─────┘  │ │
│  │         │                  │               │        │ │
│  │         └──────────────────┼───────────────┘        │ │
│  │                            │                        │ │
│  │                    ┌───────┴───────┐                │ │
│  │                    │ FFmpeg        │                │ │
│  │                    │ Subprocess    │                │ │
│  │                    │ (segment      │                │ │
│  │                    │  muxer pipe)  │                │ │
│  │                    └───────┬───────┘                │ │
│  │                            │                        │ │
│  │              ┌─────────────┴───────────┐            │ │
│  │              │ Temp Dir (ring buffer)  │            │ │
│  │              │ seg_00001.ts ... 00300  │            │ │
│  │              └─────────────────────────┘            │ │
│  └────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────┘

保存触发流程:
1. Hotkey → Core → Start new FFmpeg (new ring) ────────┐
2. Stop old FFmpeg gracefully ("q" on stdin)             │
3. Parse segment_list → find last N seconds of segments   │
4. FFmpeg concat: segments → output.mp4 (copy codec)     │
5. Toast: "回放已保存" + path                            │
6. Update Gallery index                                   │
7. Old segments cleanup (async)                          │
   Gap between step 1 and 2: ~200ms (acceptable)         │
```

## MVP 实施阶段

### 第一阶段：核心录制 + 保存回放（约 80% 功能价值）

**文件列表：**
1. `ReplayTool.sln` — 解决方案文件
2. `src/ReplayTool.Core/ReplayTool.Core.csproj` — 核心库项目
3. `src/ReplayTool.Core/Models/AppSettings.cs` — 设置模型
4. `src/ReplayTool.Core/Services/SettingsManager.cs` — 设置管理
5. `src/ReplayTool.Core/Services/CaptureService.cs` — 捕获服务
6. `src/ReplayTool.Core/Services/RingBufferService.cs` — 环形缓存
7. `src/ReplayTool.Core/Services/EncoderService.cs` — 编码器管理
8. `src/ReplayTool.Core/Services/AudioCaptureService.cs` — 音频捕获
9. `src/ReplayTool.Core/Services/HotkeyManager.cs` — 快捷键管理
10. `src/ReplayTool.Core/Services/ReplayExporter.cs` — 回放导出
11. `src/ReplayTool.Core/Services/DiskMonitor.cs` — 磁盘监控
12. `src/ReplayTool.Core/Services/RecoveryService.cs` — 崩溃恢复
13. `src/ReplayTool.Core/Native/Win32.cs` — P/Invoke 声明
14. `src/ReplayTool.Core/Native/WgcInterop.cs` — WGC 互操作
15. `src/ReplayTool.App/ReplayTool.App.csproj` — WPF 应用项目
16. `src/ReplayTool.App/App.xaml` + `App.xaml.cs` — 应用入口
17. `src/ReplayTool.App/Views/TrayIconService.cs` — 托盘服务
18. `src/ReplayTool.App/Views/SettingsWindow.xaml` + `.cs` — 设置面板
19. `src/ReplayTool.App/Views/OverlayWindow.xaml` + `.cs` — 指示器
20. `src/ReplayTool.App/Views/ToastNotification.xaml` + `.cs` — 提示弹窗
21. `src/ReplayTool.App/Views/GalleryWindow.xaml` + `.cs` — 图库

### 第二阶段：增强功能
- 自定义快捷键改键 UI
- 麦克风双音轨 + 降噪
- 手动录制 (Alt+F9) + 截图 (Alt+F1)
- 简易视频裁剪工具
- 游戏进程智能识别锁定
- 多显示器独立选择

## 关键技术风险与对策

| 风险 | 对策 |
|------|------|
| WGC 对老旧 Windows 不兼容 | CsWinRT 条件加载 + DXGI fallback + BitBlt 兜底 |
| FFmpeg 硬编码初始化失败 | 自动重试软编码 + 编码切换提示 |
| 热键被其他应用占用 | RegisterHotKey 返回 false 时弹窗引导用户改键 |
| 高帧率录制性能不足 | 动态降帧率 / 降码率 / 降分辨率 |
| 断电后缓存丢失 | TS 分段文件 + 启动时扫描恢复 + recovery 标记 |

## 验证方案

1. **构建验证**：`dotnet build` 编译通过
2. **功能性验证**：
   - 启动托盘 → 托盘图标可见
   - 右键菜单 → 设置面板可打开
   - 设置面板 → 修改参数 → 重启后持久化
   - Alt+F10 → 回放文件正确生成到导出目录
   - 环形缓存 → temp 目录分段文件数不超上限
3. **编码降级测试**：不同 GPU 环境下 FFmpeg 自动选择正确编码器
4. **稳定性验证**：长时间运行（>1 小时）无内存泄漏，无磁盘空间溢出
5. **崩溃恢复**：强制杀进程 → 重新启动 → 未保存缓存片段可恢复
