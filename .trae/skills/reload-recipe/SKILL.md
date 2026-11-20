---
name: reload-recipe
description: 让运行中的安全带检测软件从磁盘重新加载配方（单个/全部），用于外部改写 .Vproj 后刷新软件内存。支持界面操作，也支持 MQTT 主题 /SeatBeltInspection/cli/raw 命令。不负责修改配方文件本身（改文件用 vpp-toolblock-scripts）。
---

# 重载配方

## 什么时候用

用 `vpp-toolblock-scripts` 等工具在外部改完配方文件（`C:\JustSmart\Configs\Recipes\<型号>.Vproj`）后，
软件内存里仍然是旧配方，必须重载才生效：

- PLC 换型入口在同型号时会短路（`AutoLoadConfig`），无法原地刷新；
- 运行中的 `JustSmart.App` 按 `CanCloseApplication()` 拒绝 WM_CLOSE，不能远程关掉重启。

本技能提供的重载入口不需要退出软件。

## 铁律

1. **重载前不要在软件里“保存配方”**。内存是旧的，保存会把旧内容写回磁盘，覆盖刚改好的配方文件。
2. 重载会丢弃当前内存里未保存的修改；需要保留就先在外部备份，不要靠“保存配方”。
3. 重载与 PLC 触发/手动检测互斥（内部使用 `ManualInspectionRunner.CycleLock`），会等当前检测结束后执行。
4. 单个配方约 5 秒（约 490MB）；全部重载按配方数量线性增加。

## 方式一：界面

配方管理 → 配方集合管理（`RecipeCollectionManageWindow`）：

- 选中一行 → 点 **重载选中**：从磁盘重新读取该 `.Vproj` 并应用到内存；若它不是当前配方，相当于切换。
- 点 **全部重载**：按列表顺序逐个从磁盘加载，结束后自动恢复操作前的当前配方，并显示失败明细。

## 方式二：MQTT 软件功能控制

> 完整的指令清单、账户登录与权限（`login` / `logout` / `get_user` / `force_ok`）见 `mqtt-commands` 技能。

- 请求主题：`/SeatBeltInspection/cli/raw`
- 回执主题：`/SeatBeltInspection/cli/result`

连接参数复用 `C:\JustSmart\Configs\LogConfig.json` 里的 `MqttHost` / `MqttPort`（默认 `127.0.0.1:8907`）。
可用以下字段调整（缺省即默认值）：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `EnableMqttCommand` | `true` | 是否启用命令通道 |
| `MqttCommandTopic` | `/SeatBeltInspection/cli/raw` | 请求主题 |
| `MqttCommandResultTopic` | `/SeatBeltInspection/cli/result` | 回执主题 |

### 命令

请求 payload 为单行 JSON，`cmd` 不区分大小写。

| cmd | 参数 | 说明 |
| --- | --- | --- |
| `reload_recipe`（`reload`） | `index` 或 `productType`（二选一） | 重载指定配方 |
| `reload_all`（`reloadall`） | 无 | 全部重载（结束后恢复当前配方） |
| `get_status`（`status`） | 无 | 查询当前配方 |

`index` 与 `productType` 必须能在配方集合（`RecipeCollection.json`）里找到。

### 示例

```bash
# 先订阅回执
mosquitto_sub -h 127.0.0.1 -p 8907 -t "/SeatBeltInspection/cli/result" -v

# 查询当前配方
mosquitto_pub -h 127.0.0.1 -p 8907 -t "/SeatBeltInspection/cli/raw" \
  -m "{\"id\":\"t1\",\"cmd\":\"get_status\"}"

# 重载型号A
mosquitto_pub -h 127.0.0.1 -p 8907 -t "/SeatBeltInspection/cli/raw" \
  -m "{\"id\":\"t2\",\"cmd\":\"reload_recipe\",\"productType\":\"型号A\"}"

# 全部重载
mosquitto_pub -h 127.0.0.1 -p 8907 -t "/SeatBeltInspection/cli/raw" \
  -m "{\"id\":\"t3\",\"cmd\":\"reload_all\"}"
```

### 方式二-b：本机 paho-mqtt 直接发（推荐，无需工控机装 mosquitto_pub）

本机已装 `paho-mqtt`，工控机 mosquitto 默认 `allow_anonymous true`，直接 publish 即可：

```python
import paho.mqtt.publish as pub
import json

# 重载型号A
msg = {'id': 'qs1', 'cmd': 'reload_recipe', 'productType': '型号A'}
pub.single('/SeatBeltInspection/cli/raw', json.dumps(msg), hostname='192.168.1.34', port=8907)
print('OK')
```

一行命令（PowerShell）：

```powershell
python -c "import paho.mqtt.publish as pub,json; pub.single('/SeatBeltInspection/cli/raw', json.dumps({'id':'qs1','cmd':'reload_recipe','productType':'型号A'}), hostname='192.168.1.34', port=8907); print('OK')"
```

**排错**：如果 connect 被拒（CONNACK reason code=2），可能是 `allow_zero_length_clientid` 未开启，paho-mqtt 会自动生成随机 clientId，一般不受影响。

### 方式二-c：工控机本机没有 python / paho 时，用裸 TCP 发 MQTT 帧

工控机上 `python -c "import paho.mqtt.publish"` 常报 `ModuleNotFoundError: No module named 'paho'`，
此时用 PowerShell 的 `TcpClient` 直接写一个 QoS0 的 PUBLISH 包即可，不必装任何东西：

```powershell
& "d:\JustSmart\261010优易达安全带\.trae\skills\vpp-toolblock-scripts\scripts\Send-MqttReload.ps1" -ProductType "型号A"
```

两个必踩的坑：

1. **函数参数不能叫 `$host`**——PowerShell 内置只读变量，赋值直接报
   `Cannot overwrite variable host because it is read-only or constant`，改名 `$mqttHost`。
2. **中文 payload 不要走命令行拼接**：用 `@{...} | ConvertTo-Json -Compress` 生成后
   `[Text.Encoding]::UTF8.GetBytes()` 编码，否则产品名里的中文在传参途中就被打坏。

### 回执

```json
{"Id":"t2","Cmd":"reload_recipe","Success":true,"Message":"已重载配方 [1] 型号A","CurrentRecipe":"[1] 型号A","Timestamp":"2026-11-17 16:20:00.123","Details":[]}
```

`Details` 为全部重载时的失败明细数组。

## 校验

1. 回执 `Success=true`，或界面对话框提示“已重载配方”。
2. 订阅日志主题 `/SeatBeltInspection/logs/raw`，确认出现 `配方重载成功` / `全部重载结束`。
3. 重载后跑一次手动检测，确认 ToolBlock 输出与外部修改一致（脚本改动生效）。

## 排错

| 现象 | 原因 / 处理 |
| --- | --- |
| 回执 `配方文件不存在` | `GlbValues.WorkFolder` 下没有该型号 `.Vproj`，检查 `C:\JustSmart\Configs\Recipes` |
| 回执 `配方集合中不存在型号/索引` | `RecipeCollection.json` 里没有该配方，先到配方集合管理里添加并应用 |
| `Success=false` 且 Message 为反序列化异常 | 配方文件损坏或外部写坏，保留现场并用 `vpp-toolblock-scripts` 的回读校验排查 |
| MQTT 无回执 | 检查 Broker 地址/端口、`EnableMqttCommand`；连接失败会每 5 秒重试，日志里搜索 `MQTT 命令通道` |
| 重载后结果仍是旧的 | 确认改的是软件实际加载的那份配方文件；确认没有在重载前做过“保存配方” |
| 工控机 `import paho` 报 `ModuleNotFoundError` | 不要装 paho，改用方式二-c 的裸 TCP 脚本 |
| 发 MQTT 时 `Cannot overwrite variable host` | 参数改名 `$mqttHost`（PowerShell 内置只读变量） |
| 验证阈值是否生效时 `GlobalKV.Instance.GetAll().Count == 0` | 正常：GlobalKV 是进程内单例，外部进程读不到；靠界面「快速设置」或跑一次检测验证 |
