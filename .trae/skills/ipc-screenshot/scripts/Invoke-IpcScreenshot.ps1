[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Url = "http://192.168.1.34:10022",

    [Parameter(Mandatory = $false)]
    [string]$OutDir = "d:\JustSmart\261010优易达安全带\.screenshots",

    [Parameter(Mandatory = $false)]
    [string]$FileName,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Primary", "All")]
    [string]$Screen = "Primary",

    [int]$Timeout = 30
)

$ErrorActionPreference = 'Stop'

# ---- 1. 构造远端 PowerShell 截图脚本 ----
$remoteScript = @"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

`$screenDir = Join-Path `$env:TEMP 'ipc_screenshots'
if (-not (Test-Path `$screenDir)) { New-Item -ItemType Directory -Path `$screenDir -Force | Out-Null }

`$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
`$remotePath = Join-Path `$screenDir "shot_`$stamp.png"

# 目标屏幕
$(if ($Screen -eq 'All') {
@"
`$bounds = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object { `$_.Bounds }
`$minX = (`$bounds | ForEach-Object { `$_.X } | Measure-Object -Minimum).Minimum
`$minY = (`$bounds | ForEach-Object { `$_.Y } | Measure-Object -Minimum).Minimum
`$maxRight = (`$bounds | ForEach-Object { `$_.Right } | Measure-Object -Maximum).Maximum
`$maxBottom = (`$bounds | ForEach-Object { `$_.Bottom } | Measure-Object -Maximum).Maximum
`$w = `$maxRight - `$minX
`$h = `$maxBottom - `$minY
`$src = New-Object System.Drawing.Rectangle(`$minX, `$minY, `$w, `$h)
"@
} else {
@"
`$src = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
`$w = `$src.Width; `$h = `$src.Height
"@
})

try {
    `$bmp = New-Object System.Drawing.Bitmap(`$w, `$h)
    `$g = [System.Drawing.Graphics]::FromImage(`$bmp)
    `$g.CopyFromScreen(`$src.Location, [System.Drawing.Point]::Empty, `$src.Size)
    `$bmp.Save(`$remotePath, [System.Drawing.Imaging.ImageFormat]::Png)
    `$g.Dispose(); `$bmp.Dispose()
    [Console]::WriteLine("REMOTE_PATH=`$remotePath")
} catch {
    [Console]::WriteLine("REMOTE_ERROR=`$(`$_.Exception.Message)")
    [Console]::Error.WriteLine(`$_.ScriptStackTrace)
    exit 1
}
"@

# ---- 2. 调用 /exec 让远端执行截图 ----
$base = $Url.Trim().TrimEnd('/')
if ($base -notmatch '^https?://') { $base = "http://$base" }

Write-Host "[$(Get-Date -Format HH:mm:ss)] 正在工控机上截图..." -ForegroundColor Cyan

$payload = @{ command = $remoteScript; timeout = $Timeout }
$json = $payload | ConvertTo-Json -Depth 5 -Compress
$body = [System.Text.Encoding]::UTF8.GetBytes($json)

try {
    $resp = Invoke-RestMethod -Uri "$base/exec" -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Body $body -TimeoutSec ($Timeout + 15)
} catch {
    Write-Error "HTTP Shell 调用失败：$($_.Exception.Message)"
    exit 1
}

if ($resp.stderr) {
    Write-Host "[远端 stderr]" -ForegroundColor DarkYellow
    Write-Host $resp.stderr
}

# 从 stdout 解析 REMOTE_PATH=xxx
$remotePath = $null
if ($resp.stdout) {
    foreach ($line in ($resp.stdout -split "`r?`n")) {
        if ($line -match '^REMOTE_PATH=(.+)$') { $remotePath = $Matches[1]; break }
        if ($line -match '^REMOTE_ERROR=(.+)$') {
            Write-Error "远端截图失败：$($Matches[1])"
            exit 1
        }
    }
    # 同时把远端原始输出显示出来（去掉我们关心的那两行）
    $resp.stdout -split "`r?`n" | Where-Object { $_ -notmatch '^REMOTE_(PATH|ERROR)=' } | ForEach-Object { if ($_) { Write-Host "  $_" } }
}

if (-not $remotePath) {
    Write-Error "未从远端输出解析到截图路径，stdout：$($resp.stdout)"
    exit 1
}

Write-Host "远端截图：$remotePath" -ForegroundColor Gray

# ---- 3. 下载到本地 ----
if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

if (-not $FileName) {
    $FileName = "shot_$(Get-Date -Format 'yyyyMMdd_HHmmss_fff').png"
}
$localPath = Join-Path $OutDir $FileName

Write-Host "[$(Get-Date -Format HH:mm:ss)] 下载到 $localPath ..." -ForegroundColor Cyan

try {
    # curl.exe 处理二进制文件比 Invoke-WebRequest 更稳
    & curl.exe -sS -X POST "$base/download" -F "path=$remotePath" -o "$localPath"
    if ($LASTEXITCODE -ne 0) {
        throw "curl 退出码 $LASTEXITCODE"
    }
} catch {
    # 兜底：用 Invoke-RestMethod 读字节再写文件
    Write-Host "curl 失败，改用 Invoke-WebRequest ..." -ForegroundColor DarkYellow
    $dl = Invoke-WebRequest -Uri "$base/download" -Method Post -Body @{ path = $remotePath } -TimeoutSec 60
    [System.IO.File]::WriteAllBytes($localPath, $dl.Content)
}

$localFile = Get-Item -LiteralPath $localPath -ErrorAction SilentlyContinue
if (-not $localFile -or $localFile.Length -eq 0) {
    Write-Error "下载后本地文件不存在或大小为 0：$localPath"
    exit 1
}

Write-Host "下载成功：$localPath  ($([Math]::Round($localFile.Length / 1MB, 2)) MB)" -ForegroundColor Green

# ---- 4. 清理远端临时文件 ----
try {
    $cleanCmd = "Remove-Item -LiteralPath '$remotePath' -Force -ErrorAction SilentlyContinue; [Console]::WriteLine('REMOTE_CLEANED')"
    $cleanBody = [System.Text.Encoding]::UTF8.GetBytes((@{ command = $cleanCmd; timeout = 15 } | ConvertTo-Json -Compress))
    $null = Invoke-RestMethod -Uri "$base/exec" -Method Post -ContentType 'application/json; charset=utf-8' -Body $cleanBody -TimeoutSec 20
    Write-Host "远端临时文件已清理" -ForegroundColor Gray
} catch {
    Write-Host "远端清理失败（不影响结果）：$($_.Exception.Message)" -ForegroundColor DarkYellow
}

# 输出最终路径给调用方（用于后续处理）
Write-Host ""
Write-Host "[OK] $localPath" -ForegroundColor Green
