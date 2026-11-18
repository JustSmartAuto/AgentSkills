#Requires -Version 5.1
<#
.SYNOPSIS
    在工控机上读取 / 修改 Cognex VisionPro 配方(.Vproj)里 ToolBlock 的脚本。

.DESCRIPTION
    本脚本负责"传输 + 远端编译 + 执行 + 回传"，真正的读写逻辑在同目录的 VppTool.cs。
    远端用 csc 编译成 VppTool.exe，在工作目录里跑 tree 或 apply，最后把工作目录打包回本地。

    tree  : 列出工具树、导出每个 ToolBlock 的脚本原文
    apply : 按 patch_map.tsv 打补丁（自动备份 + 编译校验 + 回读校验 + 替换原文件）
            默认 apply 成功后通过 MQTT 自动重载配方（-AutoReload:$false 可关闭）

.EXAMPLE
    # 1. 读取结构、导出脚本
    .\Invoke-VppOnIpc.ps1 -Cmd tree -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj"

.EXAMPLE
    # 2. 打补丁 + 自动 MQTT 重载（默认行为）
    .\Invoke-VppOnIpc.ps1 -Cmd apply -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj"

.EXAMPLE
    # 3. 打补丁但不自动重载（手动重载）
    .\Invoke-VppOnIpc.ps1 -Cmd apply -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj" -AutoReload:$false
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('tree', 'dump', 'apply')]
    [string]$Cmd,

    [string]$Url = 'http://192.168.1.34:10022',

    [string]$Vpp = 'C:\JustSmart\Configs\Recipes\型号A.Vproj',

    # 工控机上的工作目录（ASCII 路径，避免中文路径在各处被乱码）
    [string]$RemoteWork = 'D:\Temp\vpptool',

    # 本地工作目录：tree 时下载到这里，apply 时从这里上传补丁
    [string]$LocalWork = "$env:TEMP\vpptool",

    [string]$Backup = 'D:\JustSmart\AppBackup\RecipeBackup',

    [string]$AppBin = 'D:\JustSmartAuto\SeatBeltInspection_2611161012\JustSmart.App\bin\x64\Release',

    [int]$TimeoutSec = 1200,

    # ===== 新增：自动重载配方 =====
    # apply 成功后是否通过 MQTT 自动重载配方（默认 true）
    [bool]$AutoReload = $true,

    # MQTT broker（默认 127.0.0.1:8907，应用内嵌的 broker）
    [string]$MqttHost = '127.0.0.1',
    [int]$MqttPort = 8907
)

$ErrorActionPreference = 'Stop'
$csFile = Join-Path $PSScriptRoot 'VppTool.cs'
if (-not (Test-Path $csFile)) { throw "缺少 $csFile" }
if (-not (Test-Path $LocalWork)) { New-Item -ItemType Directory -Path $LocalWork | Out-Null }

# 从 Vproj 路径提取 productType（文件名去扩展名）
$productType = [IO.Path]::GetFileNameWithoutExtension($Vpp)
Write-Host "配方 productType: $productType"

function Find-Client {
    $root = 'd:\JustStupid\httpshell远程操作工控机_2605201359\InternetProxyPy'
    $f = Get-ChildItem "$root\assets\http_shell_cli_vnc_*.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { throw '找不到 http_shell_cli_vnc_*.exe 客户端，检查 skill http-shell-ipc-powershell' }
    $f.FullName
}

function Upload([string]$local, [string]$remoteDir) {
    $r = curl.exe -s -X POST "$Url/upload" -F "file=@$local" -F "path=$remoteDir"
    if ($LASTEXITCODE -ne 0) { throw "上传失败: $local" }
    $r
}

# 0) 健康检查
$health = Invoke-RestMethod -Uri "$Url/health" -TimeoutSec 8
if ($health.status -ne 'running') { throw "工控机 HTTP Shell 未运行: $($health | ConvertTo-Json -Compress)" }

# 1) 上传 VppTool.cs 与（apply 时的）补丁文件
Upload $csFile "$RemoteWork/" | Out-Null
if ($Cmd -eq 'apply') {
    $map = Join-Path $LocalWork 'patch_map.tsv'
    if (-not (Test-Path $map)) { throw "apply 需要 $map （格式：工具路径<TAB>补丁文件[<TAB>模板块路径]）" }
    Get-ChildItem $LocalWork -File | ForEach-Object { Upload $_.FullName "$RemoteWork/" | Out-Null }
}

# ===== 内嵌 MQTT 客户端代码（远端执行，单引号 here-string 避免外层 $ 展开） =====
# 注意：PUBLISH QoS 0 报文里 payload 没有长度前缀，只有 topic 前有 2 字节长度前缀
# 用单引号 here-string 保证所有 $ 在远端才被展开
$mqttReloadBlock = ''
if ($Cmd -eq 'apply' -and $AutoReload) {
$mqttReloadBlock = @'

# ===== 自动 MQTT 重载配方 =====
Write-Host ''
Write-Host ''--- Auto Reload via MQTT ---''
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
public class MQReload {
    public static string PubAndWait(string host, int port, string subTopic, string topic, string payload, int waitMs) {
        using(var c=new TcpClient(host, port)) {
            using(var s=c.GetStream()) {
                byte[] cn = {0x10,0x10,0x00,0x04,0x4D,0x51,0x54,0x54,0x04,0x02,0x00,0x3C,0x00,0x04,0x70,0x73,0x6D,0x71};
                s.Write(cn,0,18); byte[] ca=new byte[4]; s.Read(ca,0,4);
                byte[] st=Encoding.UTF8.GetBytes(subTopic);
                int srl = 2 + 2 + st.Length + 1;
                byte[] sb = new byte[2+srl]; sb[0]=0x82; sb[1]=(byte)srl; sb[2]=0x00; sb[3]=0x01;
                sb[4]=(byte)(st.Length>>8); sb[5]=(byte)(st.Length&0xFF); Array.Copy(st,0,sb,6,st.Length); sb[6+st.Length]=0x00;
                s.Write(sb,0,sb.Length); byte[] sa=new byte[5]; s.Read(sa,0,5);
                byte[] tb=Encoding.UTF8.GetBytes(topic); byte[] pb=Encoding.UTF8.GetBytes(payload);
                int rl=2+tb.Length+pb.Length; byte[] pu=new byte[2+rl]; pu[0]=0x30; pu[1]=(byte)rl;
                pu[2]=(byte)(tb.Length>>8); pu[3]=(byte)(tb.Length&0xFF); Array.Copy(tb,0,pu,4,tb.Length);
                Array.Copy(pb,0,pu,4+tb.Length,pb.Length);
                s.Write(pu,0,pu.Length);
                System.Threading.Thread.Sleep(waitMs); s.ReadTimeout = 10000;
                try {
                    byte[] hdr = new byte[1]; int hr = s.Read(hdr, 0, 1);
                    if (hr < 1) return "NO_RESPONSE";
                    int remLen = 0, mul = 1, cnt = 0;
                    while (cnt < 4) { byte[] b = new byte[1]; s.Read(b,0,1); cnt++; remLen += (b[0]&0x7F)*mul; if((b[0]&0x80)==0) break; mul*=128; }
                    byte[] body = new byte[remLen]; int total = 0;
                    while (total < remLen) { int n = s.Read(body,total,remLen-total); if(n<=0) break; total+=n; }
                    int tpL = (body[0]<<8)|body[1];
                    return Encoding.UTF8.GetString(body, 2+tpL, remLen-2-tpL);
                } catch { return "TIMEOUT"; }
            }
        }
    }
}
"@
$mqttHost = '__MQTTHOST__'
$mqttPort = __MQTTPORT__
$cmdJson = '{"id":"vr1","cmd":"reload_recipe","productType":"__PRODUCTTYPE__"}'
Write-Host ("MQTT reload target host=" + $mqttHost + ":" + $mqttPort)
Write-Host ("ProductType=__PRODUCTTYPE__")
$result = [MQReload]::PubAndWait($mqttHost, $mqttPort, '/SeatBeltInspection/cli/result', '/SeatBeltInspection/cli/raw', $cmdJson, 2000)
Write-Host ("MQTT reload result: " + $result)
if ($result -match '"Success":true') {
    Write-Host 'AUTO_RELOAD_OK'
} else {
    Write-Host 'AUTO_RELOAD_FAILED - 请手动在界面重载'
}

'@
    # 用占位符替换，避免单引号 here-string 里的 $ 被转义
    $mqttReloadBlock = $mqttReloadBlock.Replace('__MQTTHOST__', $MqttHost)
    $mqttReloadBlock = $mqttReloadBlock.Replace('__MQTTPORT__', $MqttPort.ToString())
    $mqttReloadBlock = $mqttReloadBlock.Replace('__PRODUCTTYPE__', $productType)
}

# 2) 生成远端执行脚本（必须 UTF-8 BOM，否则 PS5.1 按 GBK 读中文路径会乱码）
# 用双引号 here-string，内部 $变量 会被本地 PowerShell 展开（好）；
# 需要远端执行的 $变量 用反引号转义；$_ 用 '$_' 单引号包起来避免展开
$runner = @"
`$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# 清掉上一次的产出，避免回传的 tree.tsv / 日志是旧的（补丁文件请勿用 s001.cs.txt 这种命名）
Get-ChildItem '$RemoteWork' -File | Where-Object { `$_.Name -match '^s\d{3}\.cs\.txt' -or `$_.Name -in @('tree.tsv','scripts_index.tsv','tree.log','apply.log','terminals.tsv','dump.log') } | Remove-Item -Force -ErrorAction SilentlyContinue
`$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
`$ref = 'C:\Program Files\Cognex\VisionPro\ReferencedAssemblies'
`$refs = @("`$ref\Cognex.VisionPro.dll", "`$ref\Cognex.VisionPro.Core.dll", "`$ref\Cognex.VisionPro.ToolGroup.dll")
`$exe = '$RemoteWork\VppTool.exe'
`$files = @('/nologo', "/out:`$exe", '/target:exe', '/r:System.Windows.Forms.dll')
`$files += (`$refs | ForEach-Object { '/r:' + '$_' })
`$files += @('$RemoteWork\VppTool.cs')
& `$csc @files 2>&1 | Out-File '$RemoteWork\csc.txt' -Encoding UTF8
if (-not (Test-Path `$exe)) { 'COMPILE FAILED，见 csc.txt'; exit 2 }
if ('$Cmd' -eq 'tree' -or '$Cmd' -eq 'dump') {
    & `$exe '$Cmd' '$Vpp' '$RemoteWork' '$AppBin' 2>&1 | Out-File '$RemoteWork\run.txt' -Encoding UTF8
    exit `$LASTEXITCODE
}
# apply
& `$exe apply '$Vpp' '$RemoteWork' '$Backup' '$AppBin' 2>&1 | Out-File '$RemoteWork\run.txt' -Encoding UTF8
`$applyCode = `$LASTEXITCODE

# === apply 成功后自动 MQTT reload ===
if (`$applyCode -eq 0) {
$mqttReloadBlock
}

exit `$applyCode
"@
$runnerLocal = Join-Path $LocalWork 'run_vpptool.ps1'
[IO.File]::WriteAllText($runnerLocal, $runner, (New-Object Text.UTF8Encoding($true)))
Upload $runnerLocal "$RemoteWork/" | Out-Null

# 3) 远端执行
$exe = Find-Client
& $exe -role client -url $Url -timeout $TimeoutSec -c "powershell -NoProfile -ExecutionPolicy Bypass -File $RemoteWork\run_vpptool.ps1"
$code = $LASTEXITCODE

# 4) 回传工作目录
$zipRemote = "$RemoteWork.zip"
$zipLocal = Join-Path $LocalWork 'remote_result.zip'
& $exe -role client -url $Url -timeout 300 -c "if(Test-Path '$zipRemote'){Remove-Item '$zipRemote' -Force}; Compress-Archive -Path '$RemoteWork\*' -DestinationPath '$zipRemote' -Force; 'zipped'"
curl.exe -s -X POST "$Url/download" -F "path=$zipRemote" -o $zipLocal
if (Test-Path $zipLocal) {
    Expand-Archive -Path $zipLocal -DestinationPath $LocalWork -Force
    Remove-Item $zipLocal -Force
}

Write-Host ""
Write-Host "远端退出码: $code"
foreach ($n in @('csc.txt', 'run.txt', 'tree.tsv', 'scripts_index.tsv', 'tree.log', 'terminals.tsv', 'dump.log', 'apply.log')) {
    $p = Join-Path $LocalWork $n
    if (Test-Path $p) { Write-Host ("--- " + $n + " ---"); Get-Content $p -Encoding UTF8 }
}
exit $code
