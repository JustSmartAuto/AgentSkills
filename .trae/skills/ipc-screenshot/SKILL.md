---
name: ipc-screenshot
description: 在工控机上用 WinForms CopyFromScreen 抓屏并通过 HTTP Shell 下载到本地目录。用于远程调试图标态、界面、告警弹窗等，配合 http-shell-ipc-powershell 使用。
---

# 工控机截图

一层薄薄的 PowerShell 封装：远程执行 `System.Windows.Forms.Screen` + `System.Drawing.Bitmap.CopyFromScreen`，保存 PNG 到 `$env:TEMP\ipc_screenshots\`，再通过 HTTP Shell 的 `/download` 接口拉回本地。

## 0. 先确认

- 工控机的 HTTP Shell 服务端在跑（默认 `http://192.168.1.34:10022`）。
- 本机 PowerShell 5.1（Windows 自带即可，无需 PowerShell 7）。
- 工控机处于**有桌面会话**的状态（用户已登录）——Session 0 上跑会抓到黑屏。

## 1. 用法

### 默认：截主屏到 `.screenshots/`

```powershell
$ps1 = "d:\JustSmart\261010优易达安全带\CodeBase\SeatBeltInspection_2611161017\.trae\skills\ipc-screenshot\scripts\Invoke-IpcScreenshot.ps1"
& $ps1 -Url "http://192.168.1.34:10022"
```

### 截全部显示器 + 自定义文件名

```powershell
& $ps1 -Url "http://192.168.1.34:10022" `
       -Screen All `
       -FileName "justsmart_running.png" `
       -OutDir "D:\temp\shots"
```

### 参数

| 参数 | 默认 | 说明 |
|------|------|------|
| `-Url` | `http://192.168.1.34:10022` | HTTP Shell 地址 |
| `-OutDir` | `d:\JustSmart\261010优易达安全带\.screenshots` | 本地输出目录（自动创建） |
| `-FileName` | `shot_yyyyMMdd_HHmmss_fff.png` | 本地文件名（留空自动带毫秒时间戳） |
| `-Screen` | `Primary` | `Primary` 仅主屏 / `All` 所有显示器拼接 |
| `-Timeout` | `30` | 远端执行超时秒数 |

## 2. 实现原理

```
[本地脚本 Invoke-IpcScreenshot.ps1]
  │
  ├─ 1. 构造远端 PowerShell（Add-Type WinForms + Drawing → CopyFromScreen → Save PNG）
  ├─ 2. POST /exec 执行 → 远端写 REMOTE_PATH=C:\Users\admin\AppData\Local\Temp\ipc_screenshots\xxx.png
  ├─ 3. POST /download 下载 → 本地 .screenshots\xxx.png （curl.exe 或 Invoke-WebRequest 兜底）
  └─ 4. POST /exec 清理远端临时文件
```

## 3. 已知限制

- **Session 0 黑屏**：如果 HTTP Shell 服务端是作为 Windows 服务（Session 0）启动的，
  `CopyFromScreen` 会抓到全黑图。解决方案：在交互式桌面会话（用户登录后）启动服务端，
  或用 `/vnc/vnc.html` noVNC 页面手动截图。
- **多显示器**：`-Screen Primary` 只截主屏（默认），`-Screen All` 会把所有屏拼接成一张大图。
- **DPI 缩放**：工控机如果开了非 100% 缩放，实际像素数会和 `Screen.Bounds` 不一致，
  截下来的图可能略小。一般视觉问题不大。
- **.ps1 必须存 UTF-8 BOM**：PowerShell 5 读 UTF-8 无 BOM 会把中文注释按 GBK 解析炸掉。
  仓库里这个脚本已经是 UTF-8 BOM，不要用 `Write` 工具重写（默认 UTF-8 无 BOM）。

## 4. 与 VNC 的区别

| | ipc-screenshot | noVNC (`http://host:10022/vnc/vnc.html`) |
|--|--|--|
| 触发 | Agent 自动 | 浏览器手动 |
| 画质 | 原始 PNG | 有损 JPEG 实时流 |
| 适合场景 | 批量 / 自动化调试 | 实时观察 / 交互 |
| 需要桌面 | 是 | 是 |
