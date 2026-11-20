---
name: http-shell-ipc-powershell
description: Run PowerShell on an industrial PC through the bundled HTTP Shell CLI/VNC exe, with file transfer and a noVNC desktop. Use when asked to connect to 工控机 or run remote PowerShell. Not for SSH.
---

# 工控机远程 PowerShell（HTTP Shell）

用项目自带的单文件 `http_shell_cli_vnc_*.exe` 连接工控机。同一个程序在工控机上跑 `server`、在本机跑 `client`，不需要 SSH。服务端按 `pwsh → powershell → cmd` 自动选择 Shell。

## 0. 先确认

- 工控机地址和端口（默认 `10022`）。用户没给地址就先问，不要猜、不要扫端口。
- 客户端程序在 `assets\` 下，文件名带时间戳，始终取最新的：

```powershell
$Root = "d:\JustStupid\httpshell远程操作工控机_2605201359\InternetProxyPy"
$exe = (Get-ChildItem "$Root\assets\http_shell_cli_vnc_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
```

每次 Shell 调用都是新的 PowerShell 会话，`$exe` 不会跨命令保留；需要时在同一段命令里重新解析。

### 备选：InternetProxyPy 自带的内嵌 Shell

工控机上如果已经跑着本项目打包的 InternetProxyPy 客户端，它内置了同款接口
（`/health`、`/exec`、`/upload`、`/download`，默认 `10022`，配置项
`remote_shell` / `remote_shell_port`），不必再单独起 `http_shell_cli_vnc_*.exe`。
请求/响应格式完全一致，本文所有命令和第 3 节的 `Invoke-RemotePowerShell.ps1`
都可直接复用；内嵌版没有 VNC 页面。它跟随软件以管理员身份运行，改时间、
改网卡不需要再次 UAC。

## 1. 健康检查（每次会话先做一次）

```powershell
Invoke-RestMethod -Uri "http://192.168.1.100:10022/health" -TimeoutSec 5
```

返回 `status = running` 才能继续；`shell` 字段是服务端实际 Shell 的路径。

注意：`/health` 秒回但 `/exec` 一律超时，通常是服务端控制台被鼠标框选（QuickEdit）冻住了 stdout，不是网络问题，处理见第 8 节。

连不通时按顺序核对，不要反复重试：
1. 工控机上服务端是否已启动：`& $exe -role server -host 0.0.0.0 -port 10022`
2. IP、端口是否正确，工控机防火墙是否放行该端口。
3. 仍不通就停止，告诉用户需要现场确认。

## 2. 执行命令（默认单次模式）

```powershell
$exe = (Get-ChildItem "d:\JustStupid\httpshell远程操作工控机_2605201359\InternetProxyPy\assets\http_shell_cli_vnc_*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
& $exe -role client -url "http://192.168.1.100:10022" -timeout 60 -c "Get-Process | Select-Object -First 5"
```

- 远端 stdout 直接输出到 stdout、stderr 输出到 stderr，进程退出码等于远端命令退出码，可用 `$LASTEXITCODE` 判断。
- `-timeout` 同时控制客户端请求超时和远端命令超时，默认 30 秒。安装、编译、启停服务等慢操作必须显式放大。
- `-w "C:\app"` 指定远端工作目录。

### 引号规则（最容易出错）

远端命令含 `$env:`、`$true` 等变量时，外层用单引号，防止本地 PowerShell 先展开：

```powershell
& $exe -role client -url "http://192.168.1.100:10022" -c 'Get-ChildItem $env:SystemRoot | Select-Object -First 3'
```

远端命令自身需要单引号时，外层改用双引号并转义 `$`（例如 `` `$env:Path ``）。命令跨多行、引号嵌套或脚本较长时，不要硬拼字符串，改用第 3 节。

### 中文输出乱码

Windows PowerShell 5.1 被重定向时按系统 ANSI 码页（GBK）输出，而服务端按 UTF-8 解码，中文（日期、系统名称等）会乱码。需要中文结果时，在远端命令最前面加一行：

```powershell
& $exe -role client -url "http://192.168.1.100:10022" -c '[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-Date; (Get-CimInstance Win32_OperatingSystem).Caption'
```

用第 3 节辅助脚本时，把同样一行写在 `-Command` 开头或脚本文件第一行。

## 3. 复杂 / 多行脚本（推荐）

配套脚本直接调用 `/exec` 接口，用 UTF-8 JSON 传输命令，避开所有嵌套引号问题：

```powershell
$ps1 = "d:\JustStupid\httpshell远程操作工控机_2605201359\InternetProxyPy\.trae\skills\http-shell-ipc-powershell\scripts\Invoke-RemotePowerShell.ps1"

# 单条命令
& $ps1 -Url "http://192.168.1.100:10022" -Command "Get-Service | Where-Object Status -eq 'Running'" -Timeout 60

# 多行脚本：先写成 UTF-8 的 .ps1，再整体发送
& $ps1 -Url "http://192.168.1.100:10022" -ScriptFile .\remote_task.ps1 -WorkDir "C:\app" -Timeout 600

# 只做健康检查
& $ps1 -Url "http://192.168.1.100:10022" -Health
```

脚本输出远端 stdout/stderr，并以远端退出码退出，适合需要判断成功失败的自动化任务。

## 4. 交互式模式（连续操作或文件传输）

```powershell
& $exe -role client -url "http://192.168.1.100:10022"
```

内置命令：`cd`、`pwd`、`upload <本地> [远程]`、`download <远程> [本地]`、`rsync` / `rsync-pull`（增量同步）、`rfb-upload` / `rfb-download` / `rfb-list`（断点续传）、`!<命令>`（仅本地执行）、`exit`。

这是给人用的交互终端；Agent 自动化时优先用单次模式或第 3 节脚本模式。

## 5. 文件传输（自动化）

HTTP 接口，适合脚本调用：

```powershell
curl.exe -X POST "http://192.168.1.100:10022/upload" -F "file=@.\local.txt" -F "path=C:/remote/dir/"
curl.exe -X POST "http://192.168.1.100:10022/download" -F "path=C:/remote/dir/file.txt" -o .\file.txt
```

大文件或弱网优先用交互模式里的 `rsync` / `rfb-*` 命令。

`/upload`、`/download` 成功路径会先打日志再返回，stdout 被冻结时会挂住；此时代用不打印的 `/rfb/file/*`（见第 9 节），请求体 JSON：

```powershell
# 列目录
@{ path = "C:\Windows" } | ConvertTo-Json -Compress | Out-File .\j.json -Encoding ascii
curl.exe -sS -X POST "http://192.168.1.100:10022/rfb/file/list" -H "Content-Type: application/json" --data-binary "@.\j.json"

# 下载（offset 支持断点续传）
@{ path = "C:\Windows\Logs\CBS\CBS.log"; offset = 0 } | ConvertTo-Json -Compress | Out-File .\j.json -Encoding ascii
curl.exe -sS -X POST "http://192.168.1.100:10022/rfb/file/download" -H "Content-Type: application/json" --data-binary "@.\j.json" -o .\CBS.log

# 上传（multipart）
curl.exe -X POST "http://192.168.1.100:10022/rfb/file/upload" -F "file=@.\a.bat" -F "path=X:\Temp\a.bat"
```

### 5.1 用 git patch 把本地改动同步到工控机（增量部署）

场景：本地改了十几个 .cs 文件，不想 robocopy 整目录、也不想等 Git push 之后工控机 pull。**直接生成 diff patch、上传、远端 `git apply`**——只传增量、速度快、可回滚。

#### 第一步：本机生成 patch

```powershell
cd "D:\JustSmart\261010优易达安全带\CodeBase\SeatBeltInspection_2611161017"
$patchPath = "$env:TEMP\feat_my_changes.patch"
$env:GIT_PAGER = "cat"

# 1) untracked 新文件先登记为 intent-to-add，git diff 才会输出其内容
$newFiles = @(
  "JustSmart.App/I18nHelper.cs",
  "JustSmart.App/WPFForm/ProcessingHintWindow.cs"
) | Where-Object { Test-Path $_ }
if ($newFiles.Count -gt 0) { git add -N -- $newFiles }

# 2) 让 git 直接把 patch 写到文件（--output），不经过 PowerShell 字符串中转
git diff HEAD -- JustSmart.App/ README.md --output=$patchPath

# 3) 还原索引（git add -N 只是临时标记）
if ($newFiles.Count -gt 0) { git reset -q -- $newFiles }

Write-Host "Patch: $patchPath ($((Get-Item $patchPath).Length) bytes)"
```

要点：
- **必须用 `git diff --output=<file>`**：Windows PowerShell 5.1 接收外部进程 stdout 时按 ANSI(GBK) 解码，UTF-8 中文注释会变乱码，落盘后的 patch 在远端 `git apply` 报 `patch failed at ...:1`；`--output` 由 git 直接写字节，天然保留编码。
- **以下两种写法都会产出不可用的 patch，禁止使用**：
  - `$sb.Append($modified)`：`$modified = git diff ...` 是字符串数组，`Append` 会把字面量 `System.Object[]` 写进 patch；
  - `git diff ... 2>&1 | Out-String`：会把 PowerShell 的 warning/错误记录（含“所在位置 行:xx”）混进 patch，生成非法文件。
- **只列你改过的目录/文件**（上面的 `-- JustSmart.App/ README.md`），不要 `git diff HEAD` 全仓库——工控机上可能有本机没有的历史改动，全量 patch 会乱。
- 新文件除 `git add -N` 外，也可用 `git diff --no-index --output=<part> <空文件> <file>` 单独生成后按字节拼接。
- `AssemblyInfo.cs` 通常由 build.py 写入版本号，**不要放进 patch**。
- `trailing whitespace` / `squelched N whitespace errors` 是 Windows CRLF 与远端 LF 差异的警告，不影响 apply。

#### 第二步：上传到工控机

```powershell
$ipcExe = "D:\JustSmart\261010优易达安全带\工具软件\http_shell_cli_vnc_20260519_134037.exe"
$ipUrl  = "http://192.168.1.34:10022"
$patchDir = "D:/JustSmartAuto/_patches"
$remoteRepo = "D:/JustSmartAuto/SeatBeltInspection_2611161012"

# 先确保远端目录存在（/rfb/file/upload 目标 dir 必须已存在，否则报 "The system cannot find the path"）
& $ipcExe -role client -url $ipUrl -timeout 10 -c "New-Item -ItemType Directory -Force -Path $patchDir | Out-Null; Write-Host OK"

# curl /rfb/file/upload（multipart）
$patchPath = "$env:TEMP\feat_my_changes.patch"
curl.exe -sS -X POST "$ipUrl/rfb/file/upload" -F "file=@$patchPath" -F "path=$patchDir/feat_my_changes.patch"
# 返回 {"status":"success","saved_as":"...","size":...}
```

#### 第三步：远端 apply patch + 编译

```powershell
# 远端先预览状态（可选）
& $ipcExe -role client -url $ipUrl -timeout 15 -w $remoteRepo -c "git status --short | Select-Object -First 15; Write-Host '---'; git log --oneline -3"

# dry-run 预检（零耗时，发现冲突立即停）
& $ipcExe -role client -url $ipUrl -timeout 15 -w $remoteRepo -c "git apply --check $patchDir/feat_my_changes.patch 2>&1; Write-Host 'EXIT='$LASTEXITCODE"

# 真 apply（优先不加 --reject，保证整体原子性；失败时按 5.1.1 生成增量 patch）
& $ipcExe -role client -url $ipUrl -timeout 20 -w $remoteRepo -c "git apply $patchDir/feat_my_changes.patch 2>&1; Write-Host 'EXIT='$LASTEXITCODE"

# 仅在明确需要逐文件落地时才用 --reject：会留下 .rej/.orig，下一次增量前必须清理
# & $ipcExe -role client -url $ipUrl -timeout 20 -w $remoteRepo -c "git apply --reject $patchDir/feat_my_changes.patch 2>&1; Write-Host 'EXIT='$LASTEXITCODE"

# 冲突后看 .rej：git apply --reject 会对冲突文件生成 .orig/.rej，
# 再手动合并或先手动编辑解决后，跳过 reject 重跑 apply。
& $ipcExe -role client -url $ipUrl -timeout 10 -w $remoteRepo -c "Get-ChildItem -Recurse -Filter *.rej | Select-Object FullName"
```

#### 第四步：远端编译（后台 + 日志重定向）

build.py 编译 10-30 秒，`/exec` 有 30 秒上限会挂住。**必须用 `Start-Process -RedirectStandardOutput/Error` 后台跑 + 下载日志文件**：

```powershell
& $ipcExe -role client -url $ipUrl -timeout 10 -w $remoteRepo -c @"
Start-Process -FilePath 'python' -ArgumentList 'build.py' -WorkingDirectory '$remoteRepo' `
  -RedirectStandardOutput '$patchDir/build_out.txt' `
  -RedirectStandardError  '$patchDir/build_err.txt' `
  -NoNewWindow
Write-Host 'BUILD_STARTED'
"@

# 等待（长任务放本地 sleep + 轮询）
Start-Sleep -Seconds 40

# 轮询：python 进程还在吗 + 日志是否已写完
& $ipcExe -role client -url $ipUrl -timeout 10 -c @"
`$p = Get-Process python -ErrorAction SilentlyContinue
if (`$p) { Write-Host ('RUNNING PID=' + `$p.Id) } else { Write-Host 'DONE' }
if (Test-Path '$patchDir/build_out.txt') { Get-Content '$patchDir/build_out.txt' -Tail 30 }
if (Test-Path '$patchDir/build_err.txt') { Write-Host '---ERR---'; Get-Content '$patchDir/build_err.txt' -Tail 30 }
"@

# 成功判定：日志末尾有"编译成功" + 无 "error CS" / "Build FAILED"
```

#### 完整 checklist

| 步骤 | 验证 |
|------|------|
| 本机 `git diff --output` 生成 patch | 文件大小合理（通常几十 KB）；首行是 `diff --git`，不是 `System.Object[]` 或 PowerShell 报错文本 |
| 上传 | curl 返回 `"status":"success"` |
| 是否已应用 | `git apply --reverse --check` 返回 0 时说明远端已是目标状态，跳过 apply |
| dry-run | `git apply --check` 的 `EXIT=0`，无报错 |
| apply | 优先 plain `git apply`（失败即整体不落地）；无 `.rej` 文件残留 |
| 同步校验 | 两端源码 hash 清单逐路径一致（忽略 `AssemblyInfo.cs`） |
| build | 日志末尾 `编译成功，耗时 ...，退出码 0` |

#### 回滚

apply 之后、编译之前想回滚：
```powershell
git apply --reverse $patchDir/feat_my_changes.patch
```
编译完了才发现有问题，用 `git stash` 或 `git reset --hard HEAD`（仅当远端所有改动都不要时）。

#### 5.1.1 增量补丁：远端已应用过部分改动

上一轮 patch 已 apply（新文件已存在、部分 hunk 已落地）后，再 apply 同一 patch 会报 `already exists` / `patch does not apply`。

先判断是否已应用（`REV=0` 表示目标状态已达成，无需再 apply）：

```powershell
git apply --reverse --check $patchDir/xxx.patch; Write-Host "REV=$LASTEXITCODE"
```

需要继续同步剩余差异时，**以远端当前文件内容为基准**生成增量 patch，而不是继续用旧 patch：

```powershell
# 1) 用 /rfb/file/download 把远端当前文件取回本地临时目录
$tmp = "$env:TEMP\remote_cur"; New-Item -ItemType Directory -Force $tmp | Out-Null

# 2) 远端版本 vs 本地目标版本做 no-index diff（有差异时退出码为 1，属正常）
git diff --no-index --output="$env:TEMP\np_1.patch" "$tmp\MainWindowViewModel.cs" `
  "本地\JustSmart.App\MainWindowViewModel.cs"
```

把每个 part 的前 4 行改写为仓库相对路径，再按字节拼接：

```powershell
# Latin-1（28591）是字节到字符串的一一映射，中转不会破坏 UTF-8 中文
$enc = [Text.Encoding]::GetEncoding(28591)
$bytes = [IO.File]::ReadAllBytes($part)
$s = $enc.GetString($bytes)
# 头部 4 行改写为：
#   diff --git a/<相对路径> b/<相对路径>
#   index <old>..<new> <mode>
#   --- a/<相对路径>
#   +++ b/<相对路径>
[IO.File]::WriteAllBytes($out, $enc.GetBytes($s))
```

关键：`<相对路径>` 必须是仓库相对路径（如 `JustSmart.App/MainWindowViewModel.cs`），远端 `git apply` 默认 `-p1` 才能落对位置。

#### 5.1.2 同步完整性校验（两端 hash 清单）

远端和本机各生成一份源码清单，按路径对比 `git hash-object` 结果：

```powershell
$exts = @(".cs",".xaml",".csproj",".slnx",".props",".config",".json",".xml",".targets")
Get-ChildItem $root -Recurse -File -Force |
  Where-Object { $_.FullName -notmatch '\\\.git\\|\\bin\\|\\obj\\|\\packages\\|\\\.vs\\|\\build_logs\\' -and ($exts -contains $_.Extension) } |
  ForEach-Object {
    $rel = $_.FullName.Substring($root.Length + 1).Replace("\","/")
    "$rel`t" + (git hash-object -- $_.FullName)
  } | Sort-Object | Out-File $inventory -Encoding ascii
```

注意：
- `git hash-object` 不带 `--path` 时计算**原始字节**，两端必须用同样方式；
- `AssemblyInfo.cs` 因 build.py 写入版本号必然不同，对比时忽略；
- 本机未跟踪的新文件出现在清单里属正常差异，说明远端还没同步。

#### 5.1.3 常见坑（实测）

| 现象 | 原因 | 处理 |
|------|------|------|
| patch 首行是 `System.Object[]` | `StringBuilder.Append(字符串数组)` | 改用 `git diff --output` |
| `git apply` 报 `patch failed at ...:1` | PowerShell 转码破坏 UTF-8 中文 | 用 `--output` 生成，不让 PowerShell 中转 |
| `already exists` / `does not apply` | 远端已应用过该 patch | 先 `git apply --reverse --check` 判断；按远端当前内容生成增量 patch |
| patch 成功但仍有 `error CS...` | 本地源码本身编译不过（WIP 状态） | 修本地源码 → 生成增量 patch → 重新 build，循环到 `编译成功`；不要直接在远端改源码，否则两端再次分叉 |
| 远端 `AssemblyInfo.cs` 总是不同 | build.py 自动写版本号 | 校验 hash 时忽略该文件 |
| `git apply --reject` 后目录里大量 `.rej/.orig` | 非原子应用留下半成品 | 增量同步场景优先 plain `git apply`；确需 `--reject` 时先清理 `.rej/.orig` 再算下一次增量 |

## 6. 远程桌面

浏览器打开 `http://192.168.1.100:10022/vnc/vnc.html`，默认无密码。仅 Windows 工控机支持；服务端会自动复用已有的 RealVNC/TightVNC/UltraVNC，没有则启用内嵌 TightVNC（首次需要管理员权限）。

### 6.1 VNC 突然崩溃 / 弹出 `R6025 pure virtual function call`

现象：工控机屏幕上出现 `Microsoft Visual C++ Runtime Library` 弹窗，Program 为 `C:\Program Files\TightVNC\tvnserver.exe`，`R6025 - pure virtual function call`，VNC 页面随之黑屏或断开。

- **定位**：`Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'}` 里看 `tvnserver.exe` 的 `0xc0000005` / `0x40000015`，再把崩溃时刻与 `JustSmart.App`（或任何需要提权的程序）的启动时间对齐——**精确到同一秒**即说明是 UAC 安全桌面切换打崩了 hook 抓屏。
- **根因**：`PromptOnSecureDesktop=1` 时 UAC 提示会切到独立安全桌面，TightVNC 未装镜像驱动、靠 hook 抓屏，切桌面必崩；`R6025` 弹窗会把 `-desktopserver` 卡死，服务不会自动恢复。
- **修复**：`Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name PromptOnSecureDesktop -Value 0 -Type DWord`（保留 UAC 确认，只是不再切安全桌面，改在交互桌面弹，远程也能点）。立即生效，无需重启。
- **当场恢复**：杀掉卡在弹窗里的 `-desktopserver`（`Get-CimInstance Win32_Process -Filter "Name='tvnserver.exe'"` 里 CommandLine 含 `-desktopserver` 的那个），再 `Restart-Service tvnserver -Force`。验证：`5900` 监听 + `Invoke-WebRequest http://127.0.0.1:10022/vnc/vnc.html` 返回 200。
- 正常实例形态只有三个：`-service`（services.exe 启动）、`-controlservice -slave`（explorer 登录时拉起）、`-desktopserver`（service 的子进程）。数量对不上或 `-desktopserver` 有多个，就是崩溃重启残留。

## 7. 常见问题

- `连接失败` 或健康检查无响应：服务端未启动、IP 错、防火墙拦截。确认一次后上报。
- 退出码 `-1` 且 stderr 含“命令执行超时”：加大 `-timeout`，或把长任务放到远端后台执行后轮询结果。
- 中文乱码：Windows PowerShell 5.1 默认按 GBK 输出，按第 2 节“中文输出乱码”加一行设置即可；cmd 输出由服务端自动按 GBK 转码。
- 输出过大：在远端先筛选或 `Out-File`，再下载结果文件，不要直接回传海量文本。

## 8. `/exec` 挂起：控制台 QuickEdit 阻塞（高频坑）

现象：`/health` 秒回，`/rfb/file/*` 正常，但所有 `/exec`、`/upload`、`/download` 的成功路径永久挂起；客户端报 `context deadline exceeded`（可能是“连接失败: Post .../exec”），远端即使 `timeout=1` 也不返回，而非法 JSON 仍能立刻报 400。

原因：服务端在返回前先往 stdout 打一行日志。Windows 控制台被鼠标框选（QuickEdit 选中态）后，stdout 写入全部阻塞，处理请求的协程卡死在打印处，命令根本没被创建。

判定：用 `/rfb/file/upload` 传一个文件，文件确实落盘但请求无响应 → 阻塞在打印日志，而不是命令执行。

修复：用 noVNC 打开桌面，点一下服务端那个控制台窗口、按两次 Esc 清除选中态，再重试 `/exec` 立即恢复，不需要重启服务。期间不要用 `taskkill` 杀 exe——宿主的 noVNC 可能就挂在同一个进程上。

## 9. 降级通道与 WinPE 目标机

目标是精简 PE 时（`X:` 是 RAM 盘、`C:` 是真实系统，`shell` 显示 `X:\windows\system32\cmd.exe`），常见约束：

- 没有 PowerShell，`/exec` 实际跑 cmd；命令写绝对路径，别依赖 PATH。
- 常见可用：`chkdsk`、`dism`、`bcdboot`、`bootsect`、`diskpart`、`reg`、`attrib`、`xcopy`/`robocopy`、`curl`，以及 BusyBox（`ps`、`kill`、`dd`、`mkdir`）；常见缺失：`hostname`、`sfc`、`tasklist`、`fsutil`、`wmic`、`tar`、`where`。
- 长任务（chkdsk、dism）：上传一个 .bat，内容把输出重定向到 `X:\Temp\*.txt` 并追加 `EXIT=%errorlevel%`，再用 `start /b X:\Temp\task.bat` 启动。这条 `/exec` 自身会超时/挂住，属正常；随后轮询下载该 txt 判断进度和退出码，别把长任务放前台等 `-timeout`。
- DISM 离线：`/scratchdir` 目录必须事先存在，否则错误 267；`/cleanup-image /restorehealth` 没有 `/source` 时损坏项会回退 Windows Update，在 PE 里会长时间卡在某个百分比，需要自备同版 `install.wim` 作源。`/scanhealth` 报“可以修复组件存储”只代表存在可修复项，未必与启动故障相关。`/source:WIM:x.wim:1 /limitaccess` 若 WIM 系统版本与目标不一致，会直接报 0x800f081f（找不到源文件），此时只能换同版本 WIM 或改用离线 SFC。
- 一个 exe 可以同时跑多个实例（不同端口、各自控制台窗口），VNC 端口由服务端自选；控制台里显示的端口可能不是你连的那个，不要据此判断。
- noVNC 页 `/vnc/vnc.html` 返回 200 不代表上游可用；直接用浏览器打开该页最直观，websockify 对缺少 Origin 的手工握手会返回 403。
- `taskkill.exe` 虽然存在，但可能报“找不到指定的模块”而失效；用 BusyBox `ps` 找 PID 再 `kill <PID>`。
- 含中文的 .bat 上传后若用 LF 换行，cmd 会解析错乱（表现为每行前几个字符被吞、报“'936' 不是内部或外部命令”）；要么写成 CRLF+GBK，要么干脆不用 .bat，长命令直接走 `/exec`（中文路径在 `/exec` 里是通的）。
- 满速拷贝（robocopy `/MT:8`）时服务端可能短暂失联：`ping` 通但 10022 TCP 连接超时，几十秒到几分钟后自行恢复，不要重启服务。

### 9.1 robocopy 大目录备份（PE 下的标准打法）

```powershell
# 前台启动会挂住本次 /exec，属正常；命令本身继续在后台跑
start /b X:\windows\system32\robocopy.exe C:\Users\Administrator "D:\旧系统备份\Administrator" /E /COPY:DAT /DCOPY:DAT /B /XJ /R:1 /W:1 /MT:8 /NFL /NDL /NP /LOG+:X:\Temp\robocopy.log
```

- 必须先估容量：`dir /s` 会重复统计 junction 和云占位，本次 Administrator 报 588 GB，实际只有 92.6 GB。以 robocopy 汇总字节为准，别拿 `dir /s` 决定要不要扩容。
- 一定加 `/XJ`，否则 `Application Data`、`My Documents` 等 junction 会循环或重复。
- 中断后重跑同一条命令即可续传（换 `/LOG+`），已存在文件自动跳过；`ps | findstr robocopy` 为空 + 日志末尾出现“已结束”即结束。
- 磁盘写满会产生上千条「错误 112」；扩容后续传会自动补齐，最终以最后一次运行的汇总为准，不用清理重来。
- 完工校验：加 `/L` 再跑一遍（秒级），要求「复制=0、不匹配=0」；剩下少量「失败」若都是错误 1920，即是云端占位文件（`.wpsonline`、WPS/OneDrive 未下载到本地）和 `WindowsApps\*_Alias.exe`，属不可复制项，不算备份失败。
- 日志是 GBK，本地按 936 解码；判断进度只需用 `/rfb/file/download` 带 offset 拉尾部，不必整包下载。

## 10. 非 PE 新系统（PowerShell）下的脚本化约定

- **最稳的执行方式**：本地写 ASCII 的 `.ps1` → `/rfb/file/upload` 传到 `D:\...\_diag\` → `/exec` 里跑 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\...\x.ps1`。本节及以下的经验都基于这条路径。
- **`\$` 不是转义**：外层 PowerShell 会先展开 `$sid`、`$_`、`$env:...`，写成 `\$sid` 只会变成 `\=` 后报错。要么外层用单引号，要么直接走上面的 `.ps1` 文件。同理 `Where-Object { $_.X }` 经 `/exec` 会丢掉 `$_`，改用简化语法 `Where-Object ProcessName -like '*x*'`。
- **改用户注册表不能用 `HKCU`**：`/exec` 跑在 SYSTEM 身份，`HKCU` 是 SYSTEM 的配置单元。用 `Registry::HKEY_USERS\<SID>\...`；直接写 `HKU\<SID>\...` 会被当成相对路径解析到当前目录（报 `D:\HKU\... 不存在`）。
- **`/exec` 有约 30 秒上限**：超过就 `context deadline exceeded`（即使远端还在跑）。需要长时间观测时，把采样循环压到 10 秒内，或由脚本自己 `Start-Process` 后台跑并写日志，再下载日志。
- **会话归属要先确认**：`(Get-Process -Id $PID).SessionId`。若 HTTP Shell 服务本身是用户在桌面会话里以管理员启动的，则 `/exec` 子进程也在同一会话（本次实测 session 1），此时 `GetForegroundWindow`、UIA、`Get-Process ... MainWindowTitle` 都可用；若服务是 SYSTEM 常驻服务，子进程在 session 0，看不到用户窗口。
- **不要 `Start-Process explorer.exe` 重启外壳**：从 `/exec`（SYSTEM）拉起会得到一个 SYSTEM 身份的 explorer。正确做法是 `Stop-Process -Name explorer -Force`，靠 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\AutoRestartShell=1`（默认）自动在用户会话重建，几秒后核对新 PID 即可。

## 11. 部署为开机自启常驻服务（非 PE）

```powershell
# 1) 上传 exe 与启动 bat 到固定目录，例如 D:\JustSmart\LocalFileDropShell\
# 2) bat 必须 CRLF + ASCII，结束时立即返回，便于任务显示 Ready
#    @echo off
#    cd /d "D:\JustSmart\LocalFileDropShell"
#    start "" "D:\JustSmart\LocalFileDropShell\App_<时间戳>.exe" --role server --port 10022
schtasks.exe /Create /TN LocalFileDropShell /SC ONSTART /RU SYSTEM /RL HIGHEST /TR "D:\JustSmart\LocalFileDropShell\start_lds.bat" /F
```

- **必须清掉 72 小时执行上限**：`schtasks /Create` 默认 `ExecutionTimeLimit=PT72H`，常驻服务 3 天后会被杀。补一句
  `Set-ScheduledTask -TaskName X -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew)`，再核对 `Settings.ExecutionTimeLimit` 为 `PT0S`。
- 核对三件事：`Triggers.CimClass.CimClassName = MSFT_TaskBootTrigger`、`Principal` 为 `SYSTEM / Highest / ServiceAccount`、`Get-ScheduledTaskInfo.LastTaskResult = 0`。任务跑完 bat 后状态回到 `Ready` 属正常（常驻进程已由 `start` 脱离）。
- 验证时要区分端口归属：Go 版 HTTP Shell 的 `/health` 返回 `status=running`，本软件返回 `status=ok`。当 10022 被其他程序占用时，新实例应自动落到 10023 并返回 `ok`；用 `Get-NetTCPConnection -State Listen -LocalPort 10022,10023` 看 `OwningProcess`。
- 更新版本时：先 `schtasks /End` + `Stop-Process` 旧 exe，改 bat 指向新 exe，再 `schtasks /Run`。旧构建留在目录里无副作用，但不要在 bat 里再引用。

### 11.1 需要 VNC 且要求「无黑窗」时：VBS + 登录触发（实测可用）

要保留 noVNC 页面就必须挂在交互式桌面上，`/SC ONSTART /RU SYSTEM` 会落进 session 0 导致 VNC 黑屏。此时改用：

- **触发/身份**：`New-ScheduledTaskTrigger -AtLogOn -User <域\用户>` + `New-ScheduledTaskPrincipal -LogonType Interactive -RunLevel Highest`，并确认目标机 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon` 的 `AutoAdminLogon=1`（自动登录时等价于开机自启）。
- **隐藏控制台**：任务动作写 `C:\Windows\System32\wscript.exe //B "D:\...\StartHttpShellServer.vbs"`，VBS 内 `sh.Run """exe"" -role server -host 0.0.0.0 -port 10022", 0, False`（窗口样式 0，不等待）。`schtasks` 的隐藏选项只隐藏任务本身，挡不住黑窗。
- **VBS 编码**：必须 UTF-16LE 带 BOM（或纯 ASCII）。UTF-8 中文注释会被按 ANSI 解析，报 `缺少语句` 且无任何日志。
- **端口递推**：从 10022 起逐个探测，占用就 +1，并把实际端口写进日志（`D:\JustSmartAuto\HttpShellServer.log`），否则重启后不知道落在哪个端口。
- **探测监听状态**：VBS 里只能 `GetObject("winmgmts:\\.\root\StandardCimv2")` + `SELECT ... FROM MSFT_NetTCPConnection WHERE LocalPort=<p> AND State=2`（`State=2` 即 Listen）。写成 `CreateObject` 会报「ActiveX 部件不能创建对象」——`CreateObject` 不认 moniker。
- **验证**：`Start-ScheduledTask` 后看 `LastTaskResult=0`、`Get-Process <exe> | Select MainWindowHandle` 为 0（无可见窗口）、`Get-NetTCPConnection -State Listen -LocalPort 10022,10023` 里新实例落在 10023、`/health` 返回 `running`。

## 12. 资源管理器「滚轮乱跳 / 列表弹回顶部」排查

先分三类，再动手（本次实测：控制台等其它程序正常、只有资源管理器乱跳，且重启 explorer 后只正常几秒）：

1. **只有资源管理器异常 → 视图刷新类**，按下面顺序逐项试，每改一项就重启 explorer 让用户复验。
2. **所有程序都乱跳 → 输入源**：拔掉外接鼠标/接收器，禁用触摸板与小红点（ThinkPad 的中键滚动卡住会持续发向上滚）。
3. **只有经 VNC 才乱 → 客户端侧**：换 noVNC（`/vnc/vnc.html`）或物理鼠标对比。

视图刷新类的处理顺序（都能用 `/exec` 完成）：

```powershell
# a. 重启外壳（AutoRestartShell=1 时会自动重建）
Stop-Process -Name explorer -Force
# b. 清缩略图/图标缓存：先杀 explorer，再尽快删，删不干净（多个 db 被新进程抢锁）属正常
Remove-Item "C:\Users\<用户>\AppData\Local\Microsoft\Windows\Explorer\*.db" -Force -ErrorAction SilentlyContinue
# c. 关闭缩略图（判定是否缩略图重建引起）：Advanced\IconsOnly = 1
# d. 清快速访问/最近使用：%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations、CustomDestinations
# e. 重置文件夹视图缓存：Shell\Bags、Shell\BagMRU（先 reg export 备份）
```

- 已知的「每隔几秒跳回顶部」根因之一：壁纸设为幻灯片 + 主题色自动取色，每次换壁纸资源管理器整体刷新。检查 `Control Panel\Personalization\Desktop Slideshow`、`DWM\AutoColorization`，壁纸改成单张图片、主题色改为手动指定即可。
- 另一类根因是第三方自启程序（更新器/云盘/加速器）周期性触发外壳通知，用启动项 + 计划任务按「上次运行时间」排查。
- **不要指望用 UIA 复现**：资源管理器的项目视图是 `UIItemsView`，虽然报 `VerticallyScrollable=true`，但 `ScrollPattern.SetScrollPercent()` 不生效（读数恒为 0）；图标/详细信息视图下也找不到 `SysListView32`（不是经典 ListView）。要判定是否周期性刷新，改用「重启 explorer 后人工观察 + 对比改动前后」的方式。
- `Bags` 30 余项、`BagMRU` 30 余项属于正常规模，不要一上来就判定为缓存膨胀。

## 13. 用 WinXShell 替换桌面外壳（把 PE 桌面搬到正常 Windows）

场景：explorer 有顽固问题（如 §12 的滚动乱跳），改用 PE 的桌面环境。全程可用 `/exec` 完成，不需要进 PE。

### 13.1 先搞清 PE 里的 shell 是谁

```powershell
C:\PROGRA~1\7-Zip\7z.exe x D:\XRPE_amd64.iso -oD:\_xrpe -y          # 全量解包最稳
C:\PROGRA~1\7-Zip\7z.exe x D:\_xrpe\sources\pe64.wim -oD:\PE -y      # 解开 PE 的 WIM
```

- PE 的桌面外壳是 `Program Files\WinXShell\WinXShell.exe`，**不是** `Windows\explorer.exe`。
- 别拿 PE 的 `explorer.exe` 去替换系统 explorer：它是 PE 版（实测 10.0.22621），与目标系统（如 19044）版本不匹配，必崩。只能当文件保存备用。
- `7z l` 报 `Unexpected end of archive`、只列出根目录就中断 → ISO 被截断。先比对 `Get-Item X.iso).Length` 与卷声明大小，补齐再解。
- `-i!` 包含过滤器实测会 `Files: 0`（在 PowerShell 里不可靠），直接全量解 ISO 再解 WIM 更省事。

### 13.2 部署到 C 盘并切换 Shell

```powershell
# WinXShell 自带全部依赖，只需它自己的目录（约 9 MB / 95 文件）
Copy-Item 'D:\PE\Program Files\WinXShell\*' 'C:\WinXShell\' -Recurse -Force
$w = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
Set-ItemProperty -Path $w -Name Shell -Value 'C:\WinXShell\WinXShell.exe'
```

- 系统组件一律放 C 盘，路径尽量不带空格（Winlogon 的 Shell 值对含空格路径实测能解析，但没必要冒险）。
- 切换前要先把旧 shell 进程杀掉（explorer / 之前的 7zFM 等），否则会叠出两套桌面和两条任务栏。
- 部署完把解包目录删掉，只留 C 盘的运行目录。

### 13.3 验证（不用重启）

```powershell
Stop-Process -Name WinXShell -Force -ErrorAction SilentlyContinue   # AutoRestartShell=1 会按新 Shell 值拉起
Start-Sleep -Seconds 10
Get-Process WinXShell | Select-Object Id, Path                      # Path 应指向 C:\WinXShell\WinXShell.exe
```

- 看到进程 `Path` 是目标路径，就证明 Winlogon 能正确解析该 Shell 值，**重启后也会同样启动**，无需真重启。
- 再用 `ipc-screenshot` 截图确认：桌面图标、任务栏、开始按钮、托盘是否齐全。
- 注意 LAUNCH 身份：`/exec` 是 SYSTEM（session 1），直接 `Start-Process` 起的窗口属于 SYSTEM，用户看不到；要让程序跑在用户桌面，用计划任务：
  `New-ScheduledTaskPrincipal -UserId qsbye -LogonType Interactive -RunLevel Limited` + `Start-ScheduledTask`。
- 杀 explorer 后**不会**自动重启（当前 shell 已换成别的进程），需要手动或计划任务拉起；而 `Start-Process explorer.exe` 在 SYSTEM 下会把界面丢到 session 0。

### 13.4 效果与已知限制

- WinXShell 提供：桌面图标（来自 Public/用户 Desktop）、任务栏、开始菜单、自带的文件管理器（界面仿 Win 10）。
- 壁纸只渲染纯色：把 `HKU\<SID>\Control Panel\Desktop\Wallpaper` 指向 PE 的 `img0.jpg` 后仍是纯浅绿底，壁纸需在 `WinXShell.jcfg` 里单独配。
- 桌面图标里的快捷方式（例如还原脚本）要放到 `C:\Users\Public\Desktop` 或用户 Desktop 才会显示。

### 13.5 回退

准备 `restore_explorer_shell.bat`：自提权后把 `Shell` 写回 `explorer.exe`，注销/重启生效。放桌面 + 数据盘根目录 + 开始菜单各一份；我方可随时通过 `/exec` 一条命令切回。

## 14. 全局屏蔽某一方向的滚轮事件（输入层兜底）

场景：explorer / WinXShell 的文件列表持续自动向上滚，§12 的刷新源抑制和 §13 的换外壳都没根治，用户只要求「全局禁用上滚动事件」。这时不再找刷新源，直接在输入层吞事件。

### 14.1 原理

- `WH_MOUSE_LL`(14) 低级鼠标钩子，全局拦截 `WM_MOUSEWHEEL`(0x020A)；`MSLLHOOKSTRUCT.mouseData` 高 16 位按 `(short)` 取即 delta，`delta > 0` 是上滚。
- 回调返回 `(IntPtr)1` 吞掉该事件，其余事件一律 `CallNextHookEx` 放行。
- **`SendInput` / `mouse_event` 注入的事件同样会经过 `WH_MOUSE_LL`**，所以对「软件注入的滚动」也有效；用户反馈「鼠标全拔了还滚」基本就排除了硬件，钩子是正确方向。
- 钩子必须在**用户会话（session 1）**安装。`/exec` 子进程若是 SYSTEM 且跑在 session 0，钩子装上了也收不到桌面输入。

### 14.2 部署（脚本文件 + 登录触发计划任务）

用 §10 的「本地写 .ps1 → `/rfb/file/upload` → `/exec` 调用」路径，别用 `-c` 硬拼 C# 源码（引号必炸）。

```powershell
schtasks.exe /Create /TN WheelBlockUp /SC ONLOGON /RU qsbye /IT /RL HIGHEST `
  /TR 'powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File D:\JustSmart\wheelblock.ps1' /F
schtasks.exe /Run /TN WheelBlockUp
```

- 必须 `/SC ONLOGON` + `/RU <登录用户>` + `/IT`；`/SC ONSTART /RU SYSTEM` 会落到 session 0，钩子永远收不到事件。
- 必须 `-STA`：`Application.Run(new ApplicationContext())` 需要 STA 的消息泵。
- PowerShell 里 `Add-Type -TypeDefinition $code -ReferencedAssemblies 'System.Windows.Forms'`，C# 侧 `System.Windows.Forms.Timer` 要用全名，避免和 `System.Threading.Timer` 撞名。
- 日志用 `File.AppendAllText` + `lock`；文件路径与脚本都放 `D:\JustSmart\`。

### 14.3 验证（只看 `hook != 0` 不算通过）

启动日志形如：

```
2026-11-18 00:33:08 start hook=22217547 err=0 session=1
2026-11-18 00:33:08 passed_down=1 blocked_up=0 delta=-120
2026-11-18 00:33:36 blocked_up=1 passed_down=71 delta=120
2026-11-18 00:33:38 alive pid=2068 blocked=6 passed=90
```

判定顺序：

1. `start` 行里 `session=1`、`err=0`。
2. 让用户滚一次**下滚**，日志出现 `passed_down ... delta=-120` → 证明事件真的进了钩子（只有 `start` 一行说明还没事件，或钩子不在用户会话）。
3. 自发上滚出现 `blocked_up ... delta=120` → 已在拦截，任务完成。
4. `alive` 心跳每 30 秒一行，重启后据此确认常驻进程还活着。

### 14.4 坑

- **日志必须节流**：失控滚动会瞬间产生上千条事件，上滚按 1 秒、下滚按 5 秒各记一条；再配 30 秒 `alive` 心跳证明线程没死。
- 心跳用 `System.Windows.Forms.Timer`（`Tick` 里写日志），不能用主线程 `sleep`——`Application.Run` 正占着该线程做消息泵。
- **改脚本后要 `schtasks /End /TN WheelBlockUp` 再 `/Run`**，否则旧进程仍在跑旧代码；顺手把旧日志改名留档。
- 钩子进程用 `Get-Process powershell | Select Id,SessionId` 复核，session 必须是 1。
- 关闭/回退：`schtasks /End /TN WheelBlockUp` + `schtasks /Change /TN WheelBlockUp /DISABLE`。
- 这只是全局屏蔽，用户正常上滚也一起失效；要双向全禁就把 `delta > 0` 改成任意 `delta`。
- 若 `start hook=0`，看 `err`（常见 1428：需要带模块句柄或线程问题），并确认进程是交互式会话。
- 启用该钩子后，之前为排查而禁用的触摸板/小红点设备应尽量恢复，避免把两个变量叠在一起。

## 15. 安全边界

该服务没有身份认证，只用于内网 / 离线环境。不要暴露到公网；不要把密码等凭据写进命令或日志；传输敏感文件前先确认网络环境。
