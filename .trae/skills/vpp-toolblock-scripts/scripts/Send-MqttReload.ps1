# Send-MqttReload.ps1
# 在工控机上通过裸 TCP 发一条 MQTT PUBLISH（QoS0），用于 reload_recipe。
# 适用场景：工控机没装 python / paho-mqtt，或命令行拼中文 payload 被搞坏。
#
# 用法：
#   .\Send-MqttReload.ps1 -ProductType 型号A
#   .\Send-MqttReload.ps1 -ProductType 型号A -Cmd reload_recipe -MqttHost 127.0.0.1 -MqttPort 8907

param(
    [Parameter(Mandatory = $true)][string]$ProductType,
    [string]$Cmd = 'reload_recipe',
    [string]$MqttHost = '127.0.0.1',
    [int]$MqttPort = 8907,
    [string]$Topic = '/SeatBeltInspection/cli/raw',
    [string]$Id = 'vr1'
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 注意：参数名不能叫 $host —— PowerShell 内置只读变量，赋值会报
# "Cannot overwrite variable host because it is read-only or constant"。
function Send-MqttPublish($mqttHost, $port, $topic, $payload) {
    $enc = [Text.Encoding]::UTF8
    $topicBytes = $enc.GetBytes($topic)
    $payloadBytes = $enc.GetBytes($payload)

    # 固定头 1 字节 + 剩余长度(最多 4 字节) + 2 字节 topic 长度 + topic + payload
    $buf = New-Object System.Collections.Generic.List[byte]
    $buf.Add(0x30)          # PUBLISH, QoS0, 不保留

    # 剩余长度：可变长编码（每字节 7 位，最高位是"还有后续"标志）
    $remaining = 2 + $topicBytes.Length + $payloadBytes.Length
    do {
        $b = $remaining % 128
        $remaining = [math]::Floor($remaining / 128)
        if ($remaining -gt 0) { $b = $b -bor 0x80 }
        $buf.Add([byte]$b)
    } while ($remaining -gt 0)

    $buf.Add([byte](($topicBytes.Length -shr 8) -band 0xFF))   # topic 长度高字节
    $buf.Add([byte]($topicBytes.Length -band 0xFF))            # topic 长度低字节
    $buf.AddRange($topicBytes)
    $buf.AddRange($payloadBytes)

    $client = New-Object System.Net.Sockets.TcpClient
    $client.Connect($mqttHost, $port)
    $stream = $client.GetStream()
    $stream.Write($buf.ToArray(), 0, $buf.Count)
    $stream.Flush()
    Start-Sleep -Milliseconds 200
    $stream.Close()
    $client.Close()
}

# payload 用 ConvertTo-Json 生成，避免手工拼引号；产品名含中文也安全
$payload = @{ id = $Id; cmd = $Cmd; productType = $ProductType } | ConvertTo-Json -Compress

Write-Host "MQTT $Topic -> $payload"
Send-MqttPublish $MqttHost $MqttPort $Topic $payload
Write-Host "SENT"
