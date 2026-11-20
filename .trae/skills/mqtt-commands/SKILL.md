---
name: mqtt-commands
description: 通过 MQTT 控制运行中的安全带检测软件（登录、强制OK、手动触发、重载配方、查询状态）并读取 JSON 回执。当需要通过 /SeatBeltInspection/cli/raw 下发指令、切换账户权限或排查回执时使用。不负责修改配方文件本身（用 vpp-toolblock-scripts）。
---

# MQTT 指令

## 连接参数

| 项 | 默认 | 说明 |
| --- | --- | --- |
| Broker | `127.0.0.1:8907` | 工控机本机；本机联调用 `192.168.1.34:8907` |
| 请求主题 | `/SeatBeltInspection/cli/raw` | `MqttCommandTopic` |
| 回执主题 | `/SeatBeltInspection/cli/result` | `MqttCommandResultTopic` |

以上字段可在 `C:\JustSmart\Configs\LogConfig.json` 覆盖，`EnableMqttCommand` 控制通道是否启用。
请求为单行 JSON，`cmd` 不区分大小写；`id` 用于把回执和请求对上。

## 账户与权限

| 账户 | 密码 | RoleLevel | 说明 |
| --- | --- | --- | --- |
| `User`（操作员） | 无 | 0 | 软件启动后的默认账户；`logout` 也回到这里 |
| `Admin`（管理员） | `AdminPassword.txt` | 3 | 密码文件在程序目录，工控机为 `...\JustSmart.App\bin\x64\Release\AdminPassword.txt` |

- 登录状态是**进程级**的：MQTT 登录管理员后，界面权限与 PLC 自动流程同样按管理员处理，直到 `logout` 或重启软件。
- `force_ok` 仅 `RoleLevel >= 3` 可用，操作员调用返回「权限不足」。

## 命令表

| cmd | 参数 | 说明 |
| --- | --- | --- |
| `login` | `user`、`password` | `Admin` 需密码；`User` 无需密码 |
| `logout` | 无 | 恢复操作员默认账户 |
| `get_user` | 无 | 返回当前用户、`RoleLevel`、`ForceOk` |
| `force_ok` | `enable`（缺省 true，可 `true/false/1/0`） | 忽略 NG 判定，全部当 OK 返回 |
| `manual_trigger` | 无 | 触发一次手动检测（等同界面「手动检测」） |
| `get_status` | 无 | 查询当前配方 |
| `reload_recipe` | `index` 或 `productType` | 重载指定配方（细节见 `reload-recipe` 技能） |
| `reload_all` | 无 | 全部重载 |
| `inspect_folder` / `inspect_manifest` | 见 MqttCommandService | 读图检测 |
| `test_sets` | 无 | 列出 `D:\JustSmart\AppTest` 下的测试集（用例数、图片数） |
| `test_run` | `testSet`（目录名或完整路径） | 异步启动测试集批量测试，立即返回 |
| `test_status` | 无 | 返回 `Test` 对象：`Running`/`Paused`/`Completed`/`Ok`/`Ng`/`Error`/`State` |
| `test_pause` / `test_resume` / `test_stop` | 无 | 暂停（当前用例结束后生效）/ 继续 / 停止 |

`force_ok` 是常开开关，不会自动复位；下发 `enable=false` 或重启软件才关闭。

## 用脚本下发（推荐）

`scripts/mqtt_cli.py` 自动订阅回执、按 `id` 匹配并打印结果，本机与工控机都可用（均已安装 paho-mqtt）。

```powershell
# 查询当前用户
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd get_user

# 管理员登录（密码取自 AdminPassword.txt）
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd login --arg user=Admin --arg password=123456

# 开启 / 关闭强制OK
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd force_ok --arg enable=true
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd force_ok --arg enable=false

# 触发一次检测
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd manual_trigger --wait 60

# 测试集批量运行（D:\JustSmart\AppTest 下的子目录即测试集）
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd test_sets
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd test_run --arg testSet=型号A_同一个NG品重复性测试_2611191325
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd test_status
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd test_pause
python scripts\mqtt_cli.py --host 192.168.1.34 --cmd test_stop
```

脚本退出码：`0` 成功，`2` 回执 `Success=false`，`1` 超时。

## 回执字段

```json
{"Id":"u4","Cmd":"login","Success":true,"Message":"管理员登录成功（RoleLevel=3）",
 "CurrentRecipe":"[6] 型号A","Timestamp":"2026-11-19 12:59:18.886",
 "User":"Admin","RoleLevel":3,"ForceOk":false}
```

- `User` / `RoleLevel` / `ForceOk` 为新增字段，旧客户端可忽略。
- `Details` 仅 `reload_all` 等批量命令使用。

## 排错

| 现象 | 处理 |
| --- | --- |
| 收不到回执 | 确认软件已启动且日志出现「MQTT 命令通道已连接并订阅」；确认订阅的是 `MqttCommandResultTopic` |
| `管理员密码错误` | 读程序目录的 `AdminPassword.txt`，注意这是运行目录下的文件，不一定等于仓库里的那份 |
| `权限不足：强制OK…` | 先 `login` 管理员；`force_ok` 不接受操作员 |
| `不支持的命令` | 核对 `cmd` 拼写，查看回执 Message 里列出的支持列表 |
| 中文乱码 | payload 用 UTF-8 编码；PowerShell 直传参数易打坏中文，改用 `mqtt_cli.py` 或从 stdin 发送 |
| 连不上 `192.168.1.34:8907` | 用 `http-shell-ipc-powershell` 确认工控机上 mosquitto 在监听 8907 |
