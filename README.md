# ReplayTool — 即时回放（ShadowPlay 复刻）

一款**无 NVIDIA 显卡依赖**的 Windows 桌面即时回放录制工具，完整复刻 NVIDIA GeForce Experience「Instant Replay 即时回放」核心能力：**7×24 后台环形缓存录制，事后按键回溯保存精彩画面**。

- 全品牌显卡兼容：Intel QuickSync / AMD VCN / Windows Media Foundation / 软件编码兜底
- 系统托盘常驻，无主窗口打扰
- 运行时内存 < 200MB

## 核心能力（对标 N 卡 ShadowPlay）

| 功能 | 快捷键（默认） | 说明 |
|------|----------------|------|
| **保存即时回放** | `Alt+F10` | 从环形缓存提取「回溯时长」的片段导出为 MP4 |
| **手动录制** | `Alt+F9` | 长按开始正向录制，再按结束保存完整视频 |
| **截图** | `Alt+F1` | 保存当前屏幕为 PNG |
| **呼出设置** | `Alt+F2` | 打开参数设置面板 |
| **覆盖指示器** | 可关闭 | 右下角圆点：绿=录制中，红=保存中，橙=暂停 |

## 工作方式

**环形缓存录制**：画面以 1 秒粒度切成 TS 片段写入临时目录，后台线程按「回溯时长」自动删除过期片段，缓存始终固定在时间窗口内（默认 120 秒 ≈ 固定磁盘占用）。按下保存快捷键时**无需中断录制**——直接按时间戳快照当前窗口的片段、就地拼接导出成品 MP4。

```
7×24 后台录制（环形缓存）
  FFmpeg ── gdigrab 屏幕捕获（60fps）────┐
    └── 音频 stdin 管道 ← NAudio WASAPI 环路 ┘
  │                                    │
  │   ┌───── 临时目录（环形）─────┐    │
  │   │ seg_00000.ts … seg_00120  │◄── 清理线程按回溯时长删除
  │   └──────────────────────────┘    │
  │                                    │
  Alt+F10 → 时间戳快照窗口 → 拼接 → Videos\ReplayTool\*.mp4
```

## 架构

```
src/
├── ReplayTool.Core/            # 核心逻辑库（无 UI 依赖）
│   ├── Models/                 # 设置模型
│   ├── Services/
│   │   ├── RingBufferService   # 环形缓存录制（核心）
│   │   ├── EncoderService      # FFmpeg 定位 + 硬件编码器自动择优/验证
│   │   ├── AudioCaptureService # WASAPI 环路 + 麦克风 → stdin
│   │   ├── ReplayExporter      # 片段拼接导出 MP4
│   │   ├── ReplayEngine        # 中枢：手动录制/截图/开机自启
│   │   ├── DiskMonitor         # 磁盘空间监控预警
│   │   └── RecoveryService     # 崩溃恢复（遗留片段导出）
│   └── Native/                 # Win32 P/Invoke + 显示器信息
├── ReplayTool.App/             # WPF 桌面应用
│   └── Views/                  # 托盘 / 设置面板 / 图库 / 覆盖指示器 / 提示弹窗
└── tests/ReplayTool.SmokeTest  # 命令行冒烟测试
```

## 构建与运行

```bash
# 前置：.NET 8 SDK、FFmpeg（应用会从 PATH / 应用目录 / FFMPEG_PATH 自动定位）
dotnet build ReplayTool.sln

# 运行
dotnet run --project src/ReplayTool.App

# 冒烟测试（自动录制并导出回放验证全流程）
dotnet run --project tests/ReplayTool.SmokeTest
```

### 发布

```bash
dotnet publish src/ReplayTool.App -c Release -r win-x64 --self-contained false -o dist
```

> **捆绑 FFmpeg（可选）**：将 `ffmpeg.exe` / `ffprobe.exe` 放入 `libs\ffmpeg\` 并在
> `src\ReplayTool.App\ReplayTool.App.csproj` 中启用对应的 `<None Include>` 条目，
> 即可随应用一起分发，无需用户安装 FFmpeg。

## 设置面板

对标 N 卡 GE 面板的可视化参数调节：

- **回放**：回溯时长（15s~5min）、临时缓存目录、磁盘缓存上限、预警阈值
- **视频**：分辨率（原始/720P/1080P/2K/4K）、帧率、码率档位、编码格式（H.264/HEVC）、画面增强
- **音频**：系统声音开关、麦克风开关/增益/降噪、音轨策略
- **快捷键**：四个动作均可改键 / 清空
- **高级**：开机自启、启动自动录制、覆盖指示器、电池省电

## 技术要点（已实测）

- **硬件编码器自动择优**：启动时依次验证 `h264_qsv`（Intel）→ `h264_amf`（AMD）→ `h264_mf`（Win11 通用）→ `h264_d3d12va` → `libx264` 兜底，验证带 8 秒超时防挂起。完全**不依赖 NVENC**。
- **关键帧控制**：使用 `-force_key_frames "expr:gte(t,n_forced*1)"`（非 x264 专属选项）保证每秒关键帧，segment muxer 才能按秒切分——已实测软件与硬件编码器均生效。
- **音频传输走 stdin 管道**：实测发现某些 FFmpeg 构建的 UDP 输入与 muxer 组合会停滞，故音频由 NAudio（WASAPI 环路 + 麦克风）捕获后写入 FFmpeg stdin（pipe:0），系统声道与麦克风在 C# 侧混音。
- **编码器检测防死锁**：`ffmpeg -encoders` 列表实际输出在 stdout，必须异步读取双管道。

## 已知限制（第二期增强项）

- 麦克风「双音轨分离保存」暂为合并模式（单流混音）；分离需独立音频进程
- 麦克风降噪为实验性实现（未启用高阶 DSP）
- 截图/手动录制暂不含音频（视频为主）
- 指定窗口捕获 / 多显示器独立选择 / 游戏进程智能锁定 尚未实现
- 编码切换降级提示已实现（日志），UI 弹窗提示待完善

## 崩溃恢复

应用异常退出后，`%TEMP%\ReplayTool\live` 中的遗留片段可由
`ReplayTool.exe --recover` 导出到 `Videos\ReplayTool\Recovered\`，避免精彩瞬间丢失。
