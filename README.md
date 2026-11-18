# AgentSkills

面向工业自动化、机器视觉和工控机远程运维场景的智能体技能与工具集合。

## 技能列表

| 技能 | 用途 |
| --- | --- |
| `http-shell-ipc-powershell` | 通过 HTTP Shell 在工控机上执行 PowerShell、传输文件及使用 noVNC 桌面 |
| `ipc-screenshot` | 远程截取工控机桌面并下载截图 |
| `proxy-joint-debug` | 联调本机与工控机之间的代理、时钟、DNS 和透明代理网络 |
| `vpp-toolblock-scripts` | 读取、修改并校验 Cognex VisionPro ToolBlock 内嵌 C# 脚本 |
| `reload-recipe` | 让运行中的检测软件重新加载磁盘配方 |
| `cognex-insight-xlsx-analyzer` | 分析 Cognex In-Sight 表格作业，生成伪代码、说明文档和单文件 HTML |
| `docs-html-to-markdown` | 将保存的 HTML 网页整理为 Markdown 并更新文档索引 |
| `skill-creator` | 创建和维护智能体技能 |

## 目录结构

```text
.
├── .trae/
│   ├── skills/       # 各技能的说明与配套脚本
│   ├── openness/     # TIA Portal Openness 导出工具
│   └── plcwork/      # PLC 项目、导出文件及工作资料
├── tools/            # 远程运维及命令行工具
└── README.md
```

## 使用方式

每个技能位于 `.trae/skills/<技能名>/`，入口为对应的 `SKILL.md`。使用前请阅读技能说明，确认适用场景、运行前提、参数及安全注意事项；配套脚本通常位于该技能的 `scripts/` 目录。

部分技能依赖 Windows、PowerShell、工控机 HTTP Shell 服务或厂商软件（如 Cognex VisionPro），并包含项目特定路径和网络配置。实际使用时请按现场环境调整，不要直接对生产设备执行未经验证的修改。

## 安全说明

- 修改 PLC、视觉配方或生产软件前，先备份原始文件并确认可恢复。
- 远程操作前确认目标工控机，避免误操作其他设备。
- 本仓库可能包含体积较大的工程文件、可执行文件和归档，克隆前请留意磁盘空间。
- `tools/` 中的系统维护工具应仅在获得授权并了解其影响后使用。

## 远端仓库

<https://github.com/JustSmartAuto/AgentSkills>
