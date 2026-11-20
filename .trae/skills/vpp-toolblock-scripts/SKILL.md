---
name: vpp-toolblock-scripts
description: 远程读取或修改 Cognex VisionPro 配方(.Vproj/.vpp)里 ToolBlock 内嵌的 C# 脚本，自动备份、编译校验、回读校验后替换；也涵盖面积阈值判定、新增输入终端、把阈值放进软件「快速设置」表格、以及把参数读取从 Inputs 层层下发迁移到各块直读 GlobalKV 等结构性修改的经验。用于排查"某个面报错导致后续面不执行"、批量给 ToolBlock 加判空/异常隔离、TempCompile 失败（缺程序集引用）等场景。不适用于纯 VisionPro 界面操作（如修改输入终端绑定）。
---

# VPP ToolBlock 脚本读写

在**不打开 VisionPro 界面**的前提下，读取配方里每个 ToolBlock 的脚本原文，并按补丁改写后写回。
所有读写都在工控机上通过 `VppTool.exe`（本地 `scripts\VppTool.cs` 远端 csc 编译）完成，
传输/编译/执行/回传由 `scripts\Invoke-VppOnIpc.ps1` 编排。

需要**改终端/加快速设置**（而不只是改脚本）时见第 7 节；面积、斑点这类判定模式见 6.6 节；
把阈值从「Inputs 层层下发」改成「各块直读 GlobalKV」见第 8 节。

## 0. 先确认

- 工控机地址、端口（默认 `http://192.168.1.34:10022`）；HTTP Shell 服务端在跑。
- 工控机已装 VisionPro：`C:\Program Files\Cognex\VisionPro\ReferencedAssemblies`。
- `csc.exe`：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`。
- 本机软件的 bin 目录（含 `JustSmart.*.dll`，用于解析配方里的应用自定义类型），
  默认 `D:\JustSmartAuto\SeatBeltInspection_2611161012\JustSmart.App\bin\x64\Release`，路径变了用 `-AppBin` 指定。

## 1. 读取：列出工具树 + 导出脚本

```powershell
$s = "d:\JustSmart\261010优易达安全带\.trae\skills\vpp-toolblock-scripts\scripts\Invoke-VppOnIpc.ps1"
& $s -Cmd tree -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj" -LocalWork "$env:TEMP\vpptree"
```

产出（下载到 `-LocalWork`）：

| 文件 | 内容 |
|---|---|
| `tree.tsv` | `路径<TAB>类型<TAB>脚本长度<TAB>AbortRunOnToolFailure` |
| `scripts_index.tsv` | `路径<TAB>文件名<TAB>长度` |
| `s001.cs.txt` … | 每个有脚本的 ToolBlock 的完整脚本原文 |
| `tree.log` / `run.txt` / `csc.txt` | 执行与编译日志 |

工具路径形如 `/检测/主体后侧面/外观不良/判断`——根节点用配方的字典键（`检测`），
**不要用 ToolBlock.Name**：生产配方里根块 Name 常常是空字符串。

### 1.1 查终端：dump

写脚本前先确认目标块有哪些输入/输出终端：

```powershell
& $s -Cmd dump -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj" -LocalWork "$env:TEMP\vppdump"
```

产出 `terminals.tsv`（`块路径<TAB>IN|OUT<TAB>终端名<TAB>类型<TAB>当前值`）和 `dump.log`
（每块的 in/out 数量、直接子工具）。注意 dump 看不到终端绑定表达式，只看到名字与类型。

## 2. 修改：准备补丁后 apply

补丁目录（即 `-LocalWork`）放两样东西：

**`patch_map.tsv`**，每行 `工具路径<TAB>补丁文件[<TAB>模板块路径]`，`#` 开头为注释：

```
# 给原本没有脚本的根块新增脚本，第 3 列指定从哪个块复制 References/Language
/检测	p_detect.cs.txt	/检测/SaveMultiImagesAutoFileName/CreateFolderPath
/检测/主体后侧面/不良项目串联	p_s5.cs.txt
/检测/主体前侧面/标签/CogToolBlock1	p_s6_front.cs.txt
```

**补丁文件**：完整脚本源文本（不是 diff）。直接从 `tree` 导出的 `sNNN.cs.txt` 复制一份改，
保证 `#region namespace imports`、自动生成的段、类名 `CogToolBlockAdvancedScript`
或 `CogToolBlockSimpleScript` 都不动，只改自己的逻辑段。

```powershell
# 默认 apply 成功后自动 MQTT 重载配方（-AutoReload 默认 true）
& $s -Cmd apply -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj" -LocalWork "$env:TEMP\vpppatch" `
     -Backup "D:\JustSmart\AppBackup\RecipeBackup"

# 关闭自动重载（手动重载或稍后 reload）
& $s -Cmd apply -Vpp "C:\JustSmart\Configs\Recipes\型号A.Vproj" -LocalWork "$env:TEMP\vpppatch" `
     -AutoReload:$false
```

`apply` 依次做：备份原文件（含同目录 `<配方名>_*.json`）→ 打补丁 →
**给每个被补丁的块补上 `JustSmart.GlobalKV.dll` 引用（3.5 步，见坑 #18）** →
逐块 `TempCompile()` → 保存到 `<配方>.new` → 重新加载回读比对 → **全部一致才替换原文件**。
任一步失败都会退出，原配方不动，新文件留在 `.new`。

apply 成功后（退出码 0），脚本会**自动通过 MQTT 发送 `reload_recipe` 命令**重载配方到运行中软件的内存，
无需手动重载或重启软件。参数见下表：

| 参数 | 默认值 | 说明 |
|---|---|---|
| `-AutoReload` | `true` | apply 成功后是否自动 MQTT 重载配方 |
| `-MqttHost` | `127.0.0.1` | MQTT broker 地址（软件内嵌 broker） |
| `-MqttPort` | `8907` | MQTT broker 端口 |

MQTT 命令：topic `/SeatBeltInspection/cli/raw`，payload `{"id":"vr1","cmd":"reload_recipe","productType":"型号A"}`，
订阅 `/SeatBeltInspection/cli/result` 确认 `Success=true`。
如果软件没在运行或 MQTT broker 不可达，脚本会打印 `AUTO_RELOAD_FAILED - 请手动在界面重载` 但**不会让 apply 整体失败**。

## 3. 必须知道的几个坑

1. **必须 STA + WinForms 同步上下文**。配方的根对象是应用自己的 `RecipeConfig`，
   它的反序列化会 `new CogToolBlockEditV2()`（WinForms 控件）；在 MTA 线程里会抛异常，
   而该异常被 `RecipeConfig` 的 `GetValue` 吞掉，表现是 **反序列化"成功"但 `VisionBlocks` 是空的**。
   `VppTool` 已内置：`[STAThread]` + `SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext())`。
2. **不是 `CogSerializer.LoadObjectFromFile`**。配方是应用自己用
   `BinaryFormatter{ AssemblyFormat = Simple }.Serialize(RecipeConfig)` 写的，
   用 CogSerializer 读会得到一个空壳。读写都用 `BinaryFormatter`。
3. **类型解析**：反序列化前把 bin 目录下 `JustSmart*.dll` 全部 `LoadFrom`，并挂
   `AppDomain.AssemblyResolve` 在 bin 目录 + VisionPro `ReferencedAssemblies` 里找 DLL，
   否则 `ToolBlockConfig` 之类解析不到。
4. **中文字符串**：远端 `.ps1` **和 `.cs` 都必须存成 UTF-8 带 BOM**，否则 PowerShell 5.1 / csc
   按 GBK 读，含中文的路径（如 `型号A.Vproj`）会变成乱码；
   远端 `csc` 编译带中文注释的 `.cs` 时若无 BOM，报的错非常误导——表现为
   `CS0246: 未能找到类型或命名空间名称"List"/"Dictionary"`（`using System.Collections.Generic;` 被 GBK 解码搞坏），
   而不是报"编码错误"。本机上传前统一转 BOM：
   ```powershell
   $c = [IO.File]::ReadAllText($p)
   [IO.File]::WriteAllText($p, $c, (New-Object Text.UTF8Encoding($true)))
   ```
   HTTP Shell 的 `/download` 对中文路径也会乱码，所以 `Invoke-VppOnIpc.ps1` 一律打包 zip 再用 ASCII 名下载。
5. **脚本对象可能是 null**。没有脚本的 ToolBlock `Script == null`，不能直接赋值 `.Source`；
   要用模板块的 `References` / `Language` 造 `CogScriptSupport(string, ArrayList, Language)`
   再赋给 `Script`（`VppTool` 的 `apply` 已实现，map 第 3 列触发）。
6. **替换前必须回读校验**，`.Vproj` 动辄 400~500MB，写坏了现场无法回滚。
7. **写完配方后脚本会自动 MQTT 重载**（`-AutoReload:$true` 默认）：脚本通过
   工控机上的 MQTT broker（`127.0.0.1:8907`）发 `reload_recipe` 命令，把刚改写的 Vproj
   从磁盘重新加载到运行中软件的内存，**无需重启软件**。
   - 前提：软件已运行且内嵌 MQTT broker 正常启动。
   - 失败降级：MQTT 连不上或重载回执不是 `Success=true` 时，脚本打印
     `AUTO_RELOAD_FAILED - 请手动在界面重载` 但 **apply 整体仍退出 0**（补丁已写盘，
     只是内存没刷新），可稍后在"配方管理"界面手动重载。
   - **重要**：重载前不要在软件里"保存配方"，否则旧内存会覆盖刚写的文件。
   - 远程杀进程重载**不推荐**：运行中的 `JustSmart.App` 会按 `CanCloseApplication()`
     拒绝 WM_CLOSE，杀掉再启动会弹"重复启动确认"对话框。
   - 如需跳过自动重载，加 `-AutoReload:$false`。
8. **新增脚本报 `CogToolBlockAdvancedScript` 未找到**：模板脚本（map 第 3 列）的
   References/Language 复制过来后，编译器看到补丁文件里定义的类和 auto-generated 区域
   里的基类是同一个程序集里编出来的——**补丁必须包含完整两段类定义**
   （自定义逻辑类 + auto-generated 里的基类/adapter 类），不能只写前者。
   写 Advanced 风格：直接继承 `CogToolBlockAdvancedScriptBase`，自己持有 `mToolBlock` 字段；
   写 Simple 风格：必须先继承 `CogToolBlockAdvancedScript`（auto-generated 区里那个），
   脚本里必须同时包含 `#region auto-generated` 段。
9. **`CogToolResultConstants` 可用值只有 `Accept` / `Error` / `Warning`**（VisionPro 9.26 SR1）。
   没有 `.None` 也没有 `.Run`。初始化 `RunTool` 的 result 参数用 `default(CogToolResultConstants)`，
   脚本正常完成后一般置 `Accept`，异常分支置 `Error`。
10. **`ToolBlock.Tools[name]` 返回 `ICogTool`，不是 object**。
    `as ICogTool` 强转后再用，不能直接丢给 `RunTool`。
11. **模板块复制过来的 References 不包含业务具体命名空间**（如 `Cognex.VisionPro.Classify`）。
    在脚本里直接 `using` 这些命名空间会报 CS0246。
    解决方案：用 **反射**（`GetProperty("Results")` / `GetProperty("ClassName")`）读类型未知的工具结果，
    避免在静态编译时引用具体类型。
12. **`AbortRunOnToolFailure` 是 ToolBlock 属性，脚本改不了**。
    给一个原本没有脚本的 ToolBlock 加上脚本后，它的默认值仍然是 `True`。
    如果脚本里用 `return false` 接管了执行、且每个子工具都被 try/catch 隔离，
    可以容忍 `True`；但如果子工具内部抛框架级未捕获异常，建议到 VisionPro 界面里
    手动把目标 ToolBlock 的 `AbortRunOnToolFailure` 改为 `False`。
13. **`GroupRun return false` 接管执行后，必须自己显式 `RunTool` 子工具**。
    `return false` 会阻止 VisionPro 框架的默认顺序执行——如果你只写了汇总逻辑
    （读子工具 Output_XXX）而没先 `RunTool` 每个子块，结果就是"一个工具也没运行"，
    所有子工具的 Output 都是初始默认值。
    正确模板：**先按依赖顺序 `RunTool` 所有子工具（每个独立 try/catch），再读 Output 汇总**。
    见第 6.2 节完整示例。
14. **接管执行后要 RunTool 所有 `Tools` 集合里的子工具，不能只挑 `CogToolBlock`**。
    生产 ToolBlock 的 `Tools` 里通常混合了多种类型：`CogToolBlock`（子检测块）、
    `CogFixtureTool`（定位/标定）、`CogClassifyTool`（分类）、`CogImageConvertTool`（灰度转换）等
    全是 `ICogTool`。**任何一个漏 RunTool，下游依赖它输出的工具就会拿到 null/空值**。
    正确做法：`foreach (ICogTool tool in mToolBlock.Tools)` 遍历全部（**必须按 `ICogTool` 枚举，见坑 #17**），
    每个元素本身就是工具对象，直接 `RunTool`。汇总阶段用 `as Cognex.VisionPro.ToolBlock.CogToolBlock`
    过滤，因为只有 ToolBlock 才有 `Output_PanDuan` / `Output_BLXM`。
15. **`CogBlobResultCollection` 只能按 `IEnumerable` 遍历**。它不实现非泛型 `IList` / `ICollection`，
    `as IList`、`as ICollection` 都得到 null，写出来就是"面积恒为 -1 → 斑点检测异常"。
    正确：`results.GetType().GetMethod("GetBlobs", Type.EmptyTypes).Invoke(...) as IEnumerable`。
16. **快速设置 `Path` 写成 `Inputs.Item[n].Value`，n 是输入集合里的顺序索引，不是终端名**。
    给块新增输入终端后索引会移位；落盘后必须 dump 该块的输入列表核对 n，否则会绑到别的终端上。
17. **`foreach (string name in mToolBlock.Tools)` 会在运行时抛异常，TempCompile 查不出来**。
    `CogToolCollection` 的枚举器返回的是 `ICogTool` 工具对象，不是工具名字符串，
    写成 `foreach (string toolName in mToolBlock.Tools)` 运行时报
    `无法将类型为"Cognex.VisionPro.ToolBlock.CogToolBlock"的对象强制转换为类型"System.String"`
    （RunStatus 结果是 `0x80131500`）。因为异常发生在 `foreach` 的取元素处、在 try 之外，
    整块 GroupRun 直接中断，表现为"主体前侧面检测报错"。
    正确写法：`foreach (ICogTool tool in mToolBlock.Tools)`，需要工具名时用 `tool.Name`；
    需要按名字取工具时才用 `mToolBlock.Tools[name]`（此时 name 必须是你自己知道的字符串）。
    这个错误编译期完全正常，**必须在代码审查时按模式搜 `foreach (string` 自查**。
18. **给脚本加程序集引用（`Script.References`）不能"无参构造 + 设属性"**。
    `Cognex.VisionPro.Implementation.Internal.CogReferencedAssembly` **没有任何无参构造函数**，
    且 **全部属性只读**（`Name` / `DirectoryPath` / `FullName` / `ExpandedFullName` /
    `DirectoryPathType` 都是 `canW=False`）。实测可用构造只有三个：

    | 构造函数 | 说明 |
    |---|---|
    | `ctor(Assembly assembly)` | **推荐**，直接 `Assembly.LoadFrom(dllPath)` 后传进去 |
    | `ctor(string name, string dirPath, CogReferencedAssemblyPathConstants pathEnum)` | 枚举值含义未验证 |
    | `ctor(SerializationInfo, StreamingContext)` | 反序列化用 |

    正确写法（`VppTool.cs` 3.5 步已实现）：
    ```csharp
    Assembly gkvAsm = Assembly.LoadFrom(dllPath);                 // 先真正加载
    object newRef = Activator.CreateInstance(cogRefAsmType, gkvAsm); // ctor(Assembly)
    ((IList)blk.Script.References).Add(newRef);                   // References 是 ArrayList
    ```
    症状：`ADD-REF FAILED create: 没有为该对象定义无参数的构造函数`，随后
    `TempCompile=False` + `ScriptError` 里一堆 `CS0246`（因为 `using` 的命名空间解析不到）。
    **必须先加引用再 TempCompile**，顺序颠倒必然全红。

    排查手段：`apply.log` 里 3.5 步会打印探测到的 `--- ctors ---` 与 `--- props ---`，
    换 VisionPro 版本后先看这段，别凭记忆写反射。
19. **工控机上不一定有 python / paho-mqtt**。`python -c "import paho..."` 报
    `ModuleNotFoundError` 时，用 PowerShell 原生 `TcpClient` 直接发 MQTT PUBLISH 帧即可
    （QoS0 单包，够用），见 [`scripts/Send-MqttReload.ps1`](scripts/Send-MqttReload.ps1)。
    两个必踩的坑：函数参数**不能叫 `$host`**（PowerShell 内置只读变量，赋值直接报
    `Cannot overwrite variable host`），名字改成 `$mqttHost`；payload 里含中文时
    必须显式 `[Text.Encoding]::UTF8.GetBytes(...)`，不要让 Python 命令行拼中文。
20. **`GlobalKV` 是进程内静态单例，跨进程读不到**。在 HTTP Shell 里
    `Add-Type -Path JustSmart.GlobalKV.dll` 然后 `[JustSmart.GlobalKv.GlobalKV]::Instance.GetAll().Count`
    得到 `0` 是**正常的**——那是 PowerShell 进程自己的单例，跟运行中的 `JustSmart.App` 无关。
    验证只能靠：软件界面「快速设置」表格有值、跑一次手动检测看 `Output_BLXM`/日志、
    或让软件自己通过 MQTT 回执暴露状态。

## 4. 能力边界

- 能改：ToolBlock 自己的脚本（`Script.Source`）、以及脚本里 `Initialize` 里
  `EnsureInputTerminal` 之类在运行时补建的终端。
- 不能改：工具树结构（增删子工具）、相机/工具参数、**输入终端的绑定表达式**。
  输入/输出终端的**增删**要另写结构补丁工具，见第 7 节。
  例如面块输入绑 `Results.Item(0).GetPose(.TranslationX)`，PMAlign 无结果时绑定求值就抛
  `'Results' is Nothing`——这类只能靠脚本在外面 try/catch 隔离，或到 VisionPro 界面里
  把绑定改成基于 `Results.Count` 的判空取值。
- **脚本里运行时补建的终端不持久化**：`Initialize` 里 `Inputs.Add` 的终端只活在本进程内存里，
  不会写回 `.Vproj`；配方重新加载后该终端就不存在了。给快速设置做绑定的终端必须是
  **落盘**的终端，只能走第 7 节的结构补丁（或在 VisionPro 界面里手工加）。
- 大配方序列化一次 5s 左右、文件 ~490MB，注意磁盘空间与作业窗口期。

## 5. 排错

| 现象 | 原因 |
|---|---|
| `VisionBlocks=0` / `VisionBlocks count=-1` | 不是 STA 线程，或缺 WinForms 同步上下文 |
| 找不到工具块 `xxx` | 路径不对；先跑 `tree` 看 `tree.tsv` 里的实际路径（根节点是字典键，不是 Name） |
| `TempCompile=False` | 补丁脚本语法/类型错误，看 `apply.log` 里 `ScriptError` 的 `error CS...` 行 |
| `回读校验不通过` | 序列化/反序列化不一致，保留 `.new` 供人工比对，原文件未被改 |
| 远端 `COMPILE FAILED` | 看 `csc.txt`；通常是 VisionPro `ReferencedAssemblies` 路径变了 |
| 远端 `CS0246 List / Dictionary` | `.cs` 没存 UTF-8 BOM，中文注释把 `using` 行搞坏了（坑 #4） |
| `ADD-REF FAILED create: 没有为该对象定义无参数的构造函数` | `CogReferencedAssembly` 无无参构造且属性只读，必须用 `ctor(Assembly)`（坑 #18） |
| 补丁里 `using 自定义命名空间` 后 `TempCompile=False`，`CS0246` 指向该命名空间 | DLL 没加进 `Script.References`；看 `apply.log` 的 ADD-REF 段（坑 #18） |
| HTTP Shell 里读 `GlobalKV.Instance.GetAll().Count == 0` | 正常，跨进程读不到（坑 #20），不是发布失败 |
| `Cannot overwrite variable host` | PowerShell 内置只读变量，MQTT 函数参数改叫 `$mqttHost`（坑 #19） |

## 6. 实用模式速查

本节来自项目内 `VPP脚本_示例/` 目录下 130+ 份生产脚本的实测模式。

### 6.1 脚本风格选择

**生产场景全部用 Advanced 风格**（130+ 脚本无一例外）：

```csharp
public class CogToolBlockAdvancedScript : CogToolBlockAdvancedScriptBase
{
  private Cognex.VisionPro.ToolBlock.CogToolBlock mToolBlock;

  public override bool GroupRun(ref string message, ref CogToolResultConstants result) { ... }
  public override void Initialize(Cognex.VisionPro.ToolGroup.CogToolGroup host) { ... }
}
```

**SimpleScript 风格**：仅当模板块已有此类脚本且基类链完整时才选用，补丁必须同时带上完整的 auto-generated 段。

### 6.2 接管 ToolBlock 执行 + 遍历所有子工具（通用模板）

```csharp
public override bool GroupRun(ref string message, ref CogToolResultConstants result)
{
  // 第 1 步：RunTool 所有子工具（不管类型：ToolBlock / FixtureTool / ClassifyTool 都是 ICogTool）
  // Tools 集合的顺序就是 VisionPro 默认执行顺序（从上到下）
  // 注意：必须按 ICogTool 枚举！Tools 枚举器返回的是工具对象，不是工具名（坑 #17）
  foreach (ICogTool tool in mToolBlock.Tools)
  {
    try
    {
      if (tool == null) continue;
      string toolMsg = "";
      CogToolResultConstants toolRes = default(CogToolResultConstants);
      mToolBlock.RunTool(tool, ref toolMsg, ref toolRes);
    }
    catch { /* 单个失败不中断 */ }
  }

  // 第 2 步：只对 CogToolBlock 类型的子块读 Output 汇总
  int pPan = 1;
  List<string> ngItems = new List<string>();
  foreach (ICogTool childTool in mToolBlock.Tools)
  {
    Cognex.VisionPro.ToolBlock.CogToolBlock sub = childTool as Cognex.VisionPro.ToolBlock.CogToolBlock;
    if (sub == null) continue; // FixtureTool / ClassifyTool 等跳过
    string toolName = childTool.Name;

    if (sub.Outputs.Contains("Output_PanDuan"))
    {
      object v = sub.Outputs["Output_PanDuan"].Value;
      if (v != null) { int pd; if (int.TryParse(v.ToString(), out pd) && pd != 1 && pPan == 1) pPan = pd; }
    }
    if (sub.Outputs.Contains("Output_BLXM"))
    {
      object v = sub.Outputs["Output_BLXM"].Value;
      string blxm = (v == null) ? "" : v.ToString();
      if (blxm != null && blxm.Length > 0 && blxm != "OK") ngItems.Add(toolName + ":" + blxm);
    }
  }

  // 第 3 步：写自身 Output
  if (mToolBlock.Outputs.Contains("Output_PanDuan")) mToolBlock.Outputs["Output_PanDuan"].Value = pPan;
  if (mToolBlock.Outputs.Contains("Output_BLXM")) mToolBlock.Outputs["Output_BLXM"].Value = ngItems.Count == 0 ? "OK" : string.Join(",", ngItems.ToArray());
  return false; // 阻止框架再跑一遍
}
```

### 6.3 运行时创建终端（补丁未预置终端时）

```csharp
public override void Initialize(Cognex.VisionPro.ToolGroup.CogToolGroup host)
{
  base.Initialize(host);
  mToolBlock = (Cognex.VisionPro.ToolBlock.CogToolBlock)host;

  // 补丁脚本写自身 Output_PanDuan 前，必须先确保终端存在
  if (!mToolBlock.Outputs.Contains("Output_PanDuan"))
    mToolBlock.Outputs.Add(new CogToolBlockTerminal("Output_PanDuan", typeof(int)));
  if (!mToolBlock.Outputs.Contains("Output_BLXM"))
    mToolBlock.Outputs.Add(new CogToolBlockTerminal("Output_BLXM", typeof(string)));
}
```

常用类型：`typeof(string)` / `typeof(int)` / `typeof(double)` / `typeof(bool)` /
`typeof(Cognex.VisionPro.ICogImage)` / `typeof(CogTransform2DLinear)` /
`typeof(List<ICogRegion>)` / `typeof(CogBlobResults)` / `typeof(CogBarcodeResults)`。

### 6.4 反射读工具结果（避开模板块 References 缺失的命名空间）

```csharp
// 以 CogClassifyTool 为例（Type 未知时）
PropertyInfo piRes = tool.GetType().GetProperty("Results");
ICollection results = piRes.GetValue(tool, null) as ICollection;
if (results == null || results.Count == 0) { /* NG */ }

foreach (object top in results)
{
  PropertyInfo piCls = top.GetType().GetProperty("ClassName");
  string clsName = piCls.GetValue(top, null) as string;
  if (clsName != "OK") { /* NG */ }
  break;
}
```

### 6.5 脚本里修改/创建子 ToolBlock（高级）

```csharp
var child = new Cognex.VisionPro.ToolBlock.CogToolBlock();
child.Script = new CogScriptSupport(scriptSource, references, 0 /*ScriptCSharp*/);
child.Script.Build(Type.GetType(null), ref compileStatus);
mToolBlock.Tools.Add(child);
```

- `Script.Language = 0` 是 C# 枚举值；
- `Script.Build(null, ref status)` 会把当前程序集里的脚本类编译出来（不同于 `TempCompile` 仅做语法检查）；
- 只在运行时动态生成子工具时用，普通 apply 补丁不需要。

### 6.6 面积阈值类检测：Segment → Blob → 结果块

典型需求：EL Segment 分出"织物/编织线"后，按**面积**判 NG，并合并进本面的 `Output_BLXM`。

前提事实（不要在 Segment 上找面积）：

- `CogSegmentResult` 只暴露 `Class`、`ClassIdentifier`、`Heatmap`（8 位灰度，值越接近 255 类别归属越强），
  **没有 Area**。面积只能从 Blob 工具拿。
- 配方里没有 Blob 工具时，先让用户/工程师在 VisionPro 里加：Segment 结果 → `CogBlobTool`（斑点）
  → `斑点结果` ToolBlock（承接 `Class` / `BlobsCount` 与判定输出）。
- Blob 工具的 `Results` **不会被存进配方**（重新加载后 `GetBlobs()` 为空 / Count=-1），
  面积只能在运行时取，别指望静态 dump 出数值。

父块（面块）里算面积再喂给结果块，分两遍跑：

```csharp
// 第 1 遍：RunTool 全部子工具（结果块的顺序不可靠，可能排在斑点工具之前）
foreach (ICogTool tool in mToolBlock.Tools) { /* RunTool，逐个 try/catch */ }

// 第 2 遍：给每个「斑点结果」块算好面积/阈值，写进它的输入，再显式重跑它
foreach (ICogTool tool in mToolBlock.Tools)
{
  Cognex.VisionPro.ToolBlock.CogToolBlock resultBlock =
    tool as Cognex.VisionPro.ToolBlock.CogToolBlock;
  if (resultBlock == null || !tool.Name.EndsWith("斑点结果")) continue;

  // 命名约定：织物划伤检测1斑点结果 -> 织物划伤检测1斑点
  string blobToolName = tool.Name.Substring(0, tool.Name.Length - 4) + "斑点";
  double sumArea = -1.0;

  foreach (ICogTool sibling in mToolBlock.Tools)
  {
    if (sibling == null || sibling.Name != blobToolName) continue;
    try
    {
      object blobResults = sibling.GetType().GetProperty("Results").GetValue(sibling, null);
      if (blobResults != null)
      {
        System.Reflection.MethodInfo mi =
          blobResults.GetType().GetMethod("GetBlobs", Type.EmptyTypes);
        IEnumerable blobs = (mi == null) ? null : (mi.Invoke(blobResults, null) as IEnumerable);
        if (blobs != null)
        {
          sumArea = 0.0;
          foreach (object b in blobs)   // 注意：必须 IEnumerable，见坑 #15
          {
            object av = b.GetType().GetProperty("Area").GetValue(b, null);
            double a = 0; if (av != null) double.TryParse(av.ToString(), out a);
            sumArea += a;               // 要"最大单块"就改成 if (a > sumArea) sumArea = a;
          }
        }
      }
    }
    catch { sumArea = -1.0; }
    break;
  }

  if (resultBlock.Inputs.Contains("BlobArea")) resultBlock.Inputs["BlobArea"].Value = sumArea;
  if (resultBlock.Inputs.Contains("FabricAreaThreshold"))
    resultBlock.Inputs["FabricAreaThreshold"].Value = fabricThreshold;

  string msg = ""; CogToolResultConstants st = default(CogToolResultConstants);
  mToolBlock.RunTool(resultBlock, ref msg, ref st);   // 必须重跑，否则输出还是第一遍的值
}
```

结果块自己只读输入、不碰兄弟工具：

```csharp
// 斑点结果块（SimpleScript 风格）：-1 = 取面积失败，> 阈值 = NG
if (blobArea < 0)      { Outputs.Output_PanDuan = 2; Outputs.Output_BLXM = "织物斑点检测异常"; }
else if (blobArea > threshold)
                       { Outputs.Output_PanDuan = 2;
                         Outputs.Output_BLXM = string.Format("织物面积总和超限(斑点合计{0:F0}>阈值{1:F0})", blobArea, threshold); }
else                   { Outputs.Output_PanDuan = 1; Outputs.Output_BLXM = "OK"; }
```

要点：

- **口径先确认**：`sumArea`（面积总和）还是最大单块面积，两者对阈值敏感度差很多；改口径只改一行。
- 用 `-1` 表示"取不到面积"，`0` 表示"确实没有斑点"，否则会把异常当成 OK。
- 第二遍 `RunTool` 是必须的：第一遍结果块跑的时候 `BlobArea` 还是默认 0。
- 结果块数量随检测项增加（1/2/3 斑点结果、编织线…），补丁按 `patch_map.tsv` 一行一个块地刷，
  不要只改一个。

## 7. 结构性修改：新增输入终端 / 快速设置项

`apply` 只动 `Script.Source`，**改不了终端集合**。需要"阈值在软件的快速设置表格里可调"这类需求时，
终端必须**落盘**，做法是另写一个结构补丁工具（与 `VppTool.cs` 同一套 STA + BinaryFormatter 读写 + 回读校验）。

### 7.1 新增输入终端

```csharp
void EnsureTerminal(Cognex.VisionPro.ToolBlock.CogToolBlock blk, string name, Type type, object v)
{
  if (blk.Inputs.Contains(name)) return;
  blk.Inputs.Add(new CogToolBlockTerminal(name, type) { Value = v });
}
```

- 一个阈值通常要出现在三处：**根块**（被快速设置指向、脚本里读它）、**面块**（脚本里读它并下发给结果块）、
  每个**结果块**（脚本里读它做判定）。三处同名同类型，脚本才不用做兼容分支。
- 类型用 `typeof(double)`，初值给默认阈值（如 500）。
- 改完必须 `TempCompile()` + 存 `.new` + 重新加载回读比对，全部一致才替换原文件（`apply` 已内置这套流程）。

### 7.2 快速设置项怎么落盘

存放位置：`ToolBlockConfig.AsyncQuickSettings`（`AsyncObservableCollection<IQuickSetting>`），
随 `RecipeConfig` 一起 `BinaryFormatter` 序列化。实现类是
`JustSmart.VisionCenter.ResourceMgr.QuickSetting_ToolBlock`，四个成员：

| 成员 | 含义 |
|---|---|
| `Name` | 「快速设置」表格里显示的名字 |
| `Path` | Cognex DataBinder 路径，指向被绑定块的终端，如 `Inputs.Item[9].Value` |
| `IsReadOnly` | 是否只读 |
| `TagBlockName` | 所属 ToolBlock 的名字（根块常见为 `检测`） |

用反射创建，避免结构补丁工具编译期引用 `JustSmart.*`
（远端 `csc` 只引用 VisionPro 程序集 + WinForms，`IQuickSetting` / `ToolBlockConfig` 这些类型都拿不到）：

```csharp
Type t = Type.GetType(
  "JustSmart.VisionCenter.ResourceMgr.QuickSetting_ToolBlock, JustSmart.VisionCenter.ResourceMgr");
object qs = Activator.CreateInstance(t, new object[] { "检测" });   // 参数 = TagBlockName
t.GetProperty("Name").SetValue(qs, "织物面积阈值", null);
t.GetProperty("Path").SetValue(qs, "Inputs.Item[9].Value", null);    // 见下方索引说明
t.GetProperty("IsReadOnly").SetValue(qs, false, null);

// AsyncQuickSettings 是 AsyncObservableCollection<IQuickSetting>（继承 ObservableCollection<T>，
// 实现非泛型 IList），所以直接按 IList 加，不需要引用 IQuickSetting：
Type cfgType = tbConfig.GetType();
((System.Collections.IList)cfgType.GetProperty("AsyncQuickSettings").GetValue(tbConfig, null)).Add(qs);
```

`tbConfig` 本身也要靠反射从 `RecipeConfig.VisionBlocks` 里按块名取，不要强转成 `ToolBlockConfig`。

索引怎么定：

1. 先加终端，再**回读该块的 `Inputs` 顺序**（`for (i=0; i<Inputs.Count; i++)`），
   用实际位置生成 `Inputs.Item[i].Value`；不要凭名字猜，也不要凭"我记得是第 10 个"。
2. 后续再往同一个块加终端，已有快速设置的 n **可能全部要 +1**，改完重新核对一遍。
3. 落盘后 dump 一次快速设置列表（块名 / 顺序 / Name / Path / Value / IsReadOnly / TagBlockName）确认。

### 7.3 阈值从快速设置流到判定点

快速设置只改它绑定的那个终端（通常是根块），**不会自动广播给子块**。脚本里要显式下发：

```csharp
// 根块：读自己的输入，再写进面块的同名输入
double th = 500.0;
if (mToolBlock.Inputs.Contains("FabricAreaThreshold"))
{
  object v = mToolBlock.Inputs["FabricAreaThreshold"].Value;
  if (v != null) double.TryParse(v.ToString(), out th);
}

foreach (ICogTool tool in mToolBlock.Tools)
{
  Cognex.VisionPro.ToolBlock.CogToolBlock child = tool as Cognex.VisionPro.ToolBlock.CogToolBlock;
  if (child != null && tool.Name == "主体顶面" && child.Inputs.Contains("FabricAreaThreshold"))
    child.Inputs["FabricAreaThreshold"].Value = th;
  // 注意：下发要发生在 child 的 RunTool 之前
}
```

面块再用 6.6 的写法把同一个值写给各结果块。整条链是：
`快速设置表格 → 根块终端 → 面块终端 → 各结果块终端 → 判定`。

### 7.4 验收步骤

1. dump 回读：三处 `FabricAreaThreshold` 终端存在且类型/值正确；
2. 回读快速设置列表，确认 `Path` 指向的索引确实是该终端；
3. MQTT `reload_recipe` 成功（`Success=true`）；
4. **跑一次真实检测**，看日志里的判定串是否带出面积数值（`BLXM=...织物面积总和超限(斑点合计N>阈值M)`），
   数值量级合理再定阈值；`取不到面积`（-1）说明 Blob 链路或遍历方式有问题，先解决再谈阈值。

### 7.5 已验证的快速设置批量添加工具

`d:\JustSmart\261010优易达安全带\.trae\quick-setup-patch\VppPatchQuickSetup.cs`
是 2026-11-18 实测通过的结构补丁程序，可直接复用。它基于 `VppTool.cs` 的验证模式：

- `STAThread` + `WindowsFormsSynchronizationContext`（必须，否则 VisionBlocks=0）
- `BinaryFormatter` + `AssemblyResolve` 兜底搜索 `JustSmart*` + VisionPro DLL
- `SaveRecipe` / `LoadRecipe` 回读比对，Input 数量 + QuickSettings 数量一致才替换原文件
- 备份落到 `backup/型号A_yyyyMMdd_HHmmss.Vproj`

关键实现要点：

1. **AssemblyResolve 必须同时搜 VisionPro ReferencedAssemblies 和 JustSmart bin 目录**：
   配方里的 `RecipeConfig` / `ToolBlockConfig` / `QuickSetting_ToolBlock` 是 JustSmart 类型，
   序列化时 BinaryFormatter 会触发 AssemblyResolve，搜不到就跳过、VisionBlocks 变空。
2. **先 `Assembly.LoadFrom` 预加载** 再反射找类型，`AppDomain.CurrentDomain.GetAssemblies()` 里才有。
3. **CogToolBlockTerminal 构造函数**：`new CogToolBlockTerminal(name, typeof(double))`，然后 `.Value = 500.0`。
4. **QuickSetting_ToolBlock 构造函数参数是 TagBlockName**（根块名，通常是 `"检测"`）。
5. **新增终端的索引 = 原有 Inputs.Count**（先 dump 确认原值，再推算新增项的 Path）。

使用流程：

```powershell
# 1. 上传 VppPatchQuickSetup.cs 到工控机
curl.exe -X POST "http://<ip>:10022/upload" -F "file=@VppPatchQuickSetup.cs" -F "path=D:\Temp\vppquickset/"

# 2. csc 编译（VisionPro DLL 从 ReferencedAssemblies 引用）
csc /nologo /target:exe /out:Patch.exe /r:System.Windows.Forms.dll /r:"C:\Program Files\Cognex\VisionPro\ReferencedAssemblies\Cognex.VisionPro.dll" /r:"...Core.dll" /r:"...ToolGroup.dll" D:\Temp\vppquickset\VppPatchQuickSetup.cs

# 3. 运行（参数: <vproj> <workDir> <appBinDir>）
.\Patch.exe "C:\JustSmart\Configs\Recipes\型号A.Vproj" "D:\Temp\vppquickset" "D:\JustSmartAuto\...\bin\x64\Release"

# 4. 本机 paho-mqtt 发重载命令
python -c "import paho.mqtt.publish as pub,json; pub.single('/SeatBeltInspection/cli/raw', json.dumps({'id':'p1','cmd':'reload_recipe','productType':'型号A'}), hostname='<ip>', port=8907); print('OK')"
```

如果工控机上没有 python / paho，改用 `scripts\Send-MqttReload.ps1`（裸 TCP 发 MQTT 帧，坑 #19）：

```powershell
& "d:\JustSmart\261010优易达安全带\.trae\skills\vpp-toolblock-scripts\scripts\Send-MqttReload.ps1" -ProductType "型号A"
```

## 8. 参数迁移：从「Inputs 层层下发」改为「各块直读 GlobalKV」

老写法是：快速设置绑根块终端 → 根块脚本读出来 → `PushInput` 下发到面块 → 面块再下发给各结果块。
链路长、任何一环改块名/终端名就静默断掉。改为**每个检测块脚本自己直接读 GlobalKV 单例**，
只需保证脚本里有 `using JustSmart.GlobalKv;` 且 `Script.References` 里有 `JustSmart.GlobalKV.dll`（坑 #18）。

### 8.1 脚本侧写法

```csharp
using JustSmart.GlobalKv;

// key 必须与软件「快速设置」表格里 QuickSetting 的 Name 属性完全一致（含中文和斜杠）
double threshold = GlobalKV.Instance.GetDouble("主体顶面/织物划伤检测1/面积阈值", 500.0);
int    pPan      = GlobalKV.Instance.GetInt32("主体后侧面/R标检测/数量阈值", 1);
string label     = GlobalKV.Instance.GetString("某键", "默认值");
bool   enable    = GlobalKV.Instance.GetBoolean("某开关", true);
```

- API：`GetDouble` / `GetInt32` / `GetString` / `GetBoolean` / `Get<T>` / `TryGet<T>` / `SetValue(key, value)` / `GetAll()`。
- **默认值必须写生产合理值**：键不存在时（配方刚换、软件版本没发布）会静默用默认值，不会报错。
- 迁移后根块/面块的 `ReadDouble + PushInput` 逻辑可以整段删掉，别再保留双份来源，
  否则两种来源会打架（快速设置改的是 GlobalKV，根块下发的是旧终端值）。

### 8.2 需要同时改的地方（一次改齐，别只改一半）

1. **快速设置必须先存在**：GlobalKV 的键就是 `QuickSetting_ToolBlock.Name`。
   没有的话先按 7.2 把快速设置落盘，再让软件把值发布进 GlobalKV。
2. **每个真正做判定的块**都要改成直读（结果块、SimpleScript 块都算），
   不能只改父块——父块不再下发，结果块的输入就永远是默认值。
3. **`patch_map.tsv` 一行一个块**，本文实测一次改了 13 个块（根块 + 4 面块 + 8 个判定/结果块）。
4. **`Script.References` 加 DLL 由 `VppTool.cs` 3.5 步自动做**，前提是 `appBin` 目录里有
   `JustSmart.GlobalKV.dll`；没有就从软件 bin 目录拷一份过去。

### 8.3 验收

1. `apply.log`：每个块 `ADD-REF via ctor(Assembly) OK` → `TempCompile=True` → `VERIFY match=True`；
2. `Send-MqttReload.ps1` 或自动重载返回成功；
3. **在软件界面「快速设置」表格里改一个阈值 → 手动检测一次**，看判定串里的数值是否跟着变。
   这一步不可省：`GlobalKV.Instance.GetAll().Count` 在外部进程里读到 0 是正常的（坑 #20），
   不能用它判断发布是否成功。

### 8.4 发布时机与已验证的行为

`QuickSettingGlobalKv`（`JustSmart.VisionCenter.ResourceMgr`）在四处发布，改完配方无需额外操作：

| 时机 | 入口 |
|---|---|
| 配方加载/重载（含 MQTT `reload_recipe`） | `ProjectMgr.LoadRecipe` → `QuickSettingGlobalKv.PublishAll(ProjectObj)` |
| 界面新增参数项 | `QuickSettingEditor.Click_AddQuickSetting` → `Publish` |
| 界面删除参数项 | `Click_RemoveQuickSetting` → `Unpublish` |
| 改参数名 / 改值 | `QuickSetting_ToolBlock.Name` / `Value` 的 setter |

2026-11 实测（`型号A`，20 项快速设置）：进程内反序列化配方 → `ApplyTo` → `PublishAll`
后 `GlobalKV.Count == 20`，值与终端一致（面积 500、Blob 1、得分 0.6、R标 0.5）。

已修复的两个健壮性缺陷（若换环境后仍出现「一个键都没有」或「脚本一直读默认值」，先查这两点）：

1. `PublishAll` 里**单个参数取值异常会中断整轮发布**（`setting.Value` 的 `TagBlock` 解析在 try 之外）。
   现在逐个 `try/catch` 隔离，失败只打印 `[WARN] 快速设置发布失败：<键> - <原因>`。
2. **取值为 null 时也写入 GlobalKV**，脚本 `GetDouble` 读到 null 就退回默认值，表现为「设置没生效」。
   现在 null 直接跳过，保留上一次的有效值。

这两处改动在 `QuickSettingGlobalKv.cs` / `QuickSetting_ToolBlock.cs`。改完必须重编译
`JustSmart.VisionCenter.ResourceMgr`（`MSBuild <csproj> /p:Configuration=Release /p:Platform=x64`），
再把 `bin\x64\Release\JustSmart.VisionCenter.ResourceMgr.dll` 覆盖到软件 bin：
软件运行中 DLL 被占用无法直接覆盖，可先 `Move-Item` 成 `.dll.old` 再拷新文件（**重启软件后生效**）。
