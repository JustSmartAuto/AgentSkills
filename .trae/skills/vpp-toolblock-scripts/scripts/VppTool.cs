using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Formatters;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Cognex.VisionPro;
using Cognex.VisionPro.ToolBlock;
using Cognex.VisionPro.ToolGroup;

// VppTool —— 读写 Cognex VisionPro 配方(.Vproj/.vpp)里 ToolBlock 的脚本
//
//   VppTool.exe tree  <vpp> <workDir> [appBinDir]
//   VppTool.exe dump  <vpp> <workDir> [appBinDir]
//   VppTool.exe apply <vpp> <workDir> <backupDir> [appBinDir]
//
// 依赖：工控机上已安装 VisionPro；appBinDir 为本机软件的 bin 目录（含 JustSmart.*.dll），
//       用于解析配方里应用自定义类型。
class VppTool
{
    const string DefaultAppBin = @"D:\JustSmartAuto\SeatBeltInspection_2611161012\JustSmart.App\bin\x64\Release";
    const string VpRefDir = @"C:\Program Files\Cognex\VisionPro\ReferencedAssemblies";

    static readonly List<string> dirs = new List<string>();
    static readonly Dictionary<string, Assembly> cache = new Dictionary<string, Assembly>();
    static TextWriter log;

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        Assembly a;
        if (cache.TryGetValue(name, out a)) return a;
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            string p = Path.Combine(d, name + ".dll");
            if (File.Exists(p))
            {
                try { a = Assembly.LoadFrom(p); cache[name] = a; return a; } catch { }
            }
        }
        return null;
    }

    static void SetupResolver(string appBin)
    {
        dirs.Add(appBin);
        dirs.Add(VpRefDir);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.GetFiles(d, "JustSmart*.dll"))
            {
                try { Assembly a = Assembly.LoadFrom(f); cache[a.GetName().Name] = a; } catch { }
            }
        }
    }

    static object LoadRecipe(string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            BinaryFormatter bf = new BinaryFormatter();
            bf.AssemblyFormat = FormatterAssemblyStyle.Simple;
            return bf.Deserialize(fs);
        }
    }

    static void SaveRecipe(object root, string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            BinaryFormatter bf = new BinaryFormatter();
            bf.AssemblyFormat = FormatterAssemblyStyle.Simple;
            bf.Serialize(fs, root);
        }
    }

    static List<KeyValuePair<string, CogToolBlock>> CollectBlocks(object root)
    {
        IDictionary vb = root.GetType().GetProperty("VisionBlocks").GetValue(root, null) as IDictionary;
        if (vb == null) throw new Exception("配方对象里没有 VisionBlocks 属性，可能不是本软件的 RecipeConfig 格式");
        List<KeyValuePair<string, CogToolBlock>> all = new List<KeyValuePair<string, CogToolBlock>>();
        foreach (DictionaryEntry de in vb)
        {
            ICogTool tb = de.Value.GetType().GetProperty("ToolBlock").GetValue(de.Value, null) as ICogTool;
            if (tb != null) Collect(tb, "/" + de.Key, all);
        }
        return all;
    }

    static void Collect(ICogTool tool, string path, List<KeyValuePair<string, CogToolBlock>> all)
    {
        CogToolBlock blk = tool as CogToolBlock;
        if (blk != null) all.Add(new KeyValuePair<string, CogToolBlock>(path, blk));
        CogToolGroup grp = tool as CogToolGroup;
        if (grp == null) return;
        foreach (ICogTool child in grp.Tools)
            if (child != null) Collect(child, path + "/" + child.Name, all);
    }

    static int ScriptLength(CogToolBlock blk)
    {
        try { return blk.Script == null || blk.Script.Source == null ? 0 : blk.Script.Source.Length; }
        catch { return -1; }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法:");
            Console.Error.WriteLine("  VppTool.exe tree  <vpp> <workDir> [appBinDir]");
            Console.Error.WriteLine("  VppTool.exe dump  <vpp> <workDir> [appBinDir]");
            Console.Error.WriteLine("  VppTool.exe apply <vpp> <workDir> <backupDir> [appBinDir]");
            Environment.Exit(1);
        }

        string cmd = args[0];
        string vpp = args[1];
        string workDir = args[2];
        if (!Directory.Exists(workDir)) Directory.CreateDirectory(workDir);
        log = TextWriter.Synchronized(new StreamWriter(Path.Combine(workDir, cmd + ".log"), false, new UTF8Encoding(false)));

        // tree:  args[3] = appBinDir
        // apply: args[3] = backupDir, args[4] = appBinDir
        string appBin = DefaultAppBin;
        if ((cmd == "tree" || cmd == "dump") && args.Length > 3 && !string.IsNullOrEmpty(args[3])) appBin = args[3];
        if (cmd == "apply" && args.Length > 4 && !string.IsNullOrEmpty(args[4])) appBin = args[4];

        // VisionPro 的 ToolBlockConfig 反序列化会 new CogToolBlockEditV2（WinForms 控件），
        // 必须 STA 线程 + 同步上下文，否则异常被吞掉、VisionBlocks 变成空字典。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        SetupResolver(appBin);

        try
        {
            if (cmd == "tree")
            {
                DoTree(vpp, workDir, appBin);
            }
            else if (cmd == "dump")
            {
                DoDump(vpp, workDir);
            }
            else if (cmd == "apply")
            {
                string backupDir = args.Length > 3 ? args[3] : Path.Combine(workDir, "backup");
                DoApply(vpp, workDir, backupDir, appBin);
            }
            else
            {
                Console.Error.WriteLine("未知命令: " + cmd);
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            log.WriteLine("FATAL " + ex);
            log.Flush();
            Console.Error.WriteLine("FATAL " + ex.Message);
            Environment.Exit(9);
        }
        finally
        {
            log.Flush();
            log.Close();
        }
    }

    // ---------------- tree：列出工具树并导出脚本原文 ----------------
    static void DoTree(string vpp, string workDir, string appBin)
    {
        log.WriteLine("vpp   = " + vpp);
        log.WriteLine("appBin= " + appBin);
        object root = LoadRecipe(vpp);
        List<KeyValuePair<string, CogToolBlock>> all = CollectBlocks(root);

        TextWriter tree = new StreamWriter(Path.Combine(workDir, "tree.tsv"), false, new UTF8Encoding(false));
        TextWriter index = new StreamWriter(Path.Combine(workDir, "scripts_index.tsv"), false, new UTF8Encoding(false));
        tree.WriteLine("path\ttype\tscriptLen\tabortRunOnToolFailure");
        index.WriteLine("path\tfile\tlen");

        int n = 0;
        foreach (KeyValuePair<string, CogToolBlock> kv in all)
        {
            CogToolBlock blk = kv.Value;
            int len = ScriptLength(blk);
            bool abort = false;
            try { abort = blk.AbortRunOnToolFailure; } catch { }
            tree.WriteLine(kv.Key + "\t" + blk.GetType().Name + "\t" + len + "\t" + abort);

            if (len > 0)
            {
                n++;
                string fn = string.Format("s{0:000}.cs.txt", n);
                File.WriteAllText(Path.Combine(workDir, fn), blk.Script.Source, new UTF8Encoding(false));
                index.WriteLine(kv.Key + "\t" + fn + "\t" + len);
            }
            log.WriteLine(string.Format("  {0}  len={1} abort={2}", kv.Key, len, abort));
        }
        tree.Close();
        index.Close();
        log.WriteLine("blocks=" + all.Count + " scripts=" + n);
        Console.WriteLine("OK tree blocks=" + all.Count + " scripts=" + n + " -> " + workDir);
    }

    // ---------------- dump：列出每个 ToolBlock 的输入/输出终端 ----------------
    static void DoDump(string vpp, string workDir)
    {
        log.WriteLine("vpp   = " + vpp);
        object root = LoadRecipe(vpp);
        List<KeyValuePair<string, CogToolBlock>> all = CollectBlocks(root);

        TextWriter w = new StreamWriter(Path.Combine(workDir, "terminals.tsv"), false, new UTF8Encoding(false));
        w.WriteLine("block\tdir\tname\tvalueType\tvalue");

        bool propsLogged = false;
        int total = 0;
        foreach (KeyValuePair<string, CogToolBlock> kv in all)
        {
            CogToolBlock blk = kv.Value;
            if (!propsLogged && blk.Inputs.Count > 0)
            {
                propsLogged = true;
                List<string> pn = new List<string>();
                foreach (PropertyInfo pi in blk.Inputs[0].GetType().GetProperties())
                    pn.Add(pi.Name + ":" + pi.PropertyType.Name);
                log.WriteLine("terminal props = " + string.Join(", ", pn.ToArray()));
            }

            List<string> childNames = new List<string>();
            foreach (ICogTool child in blk.Tools)
                childNames.Add(child == null ? "null" : child.Name + "(" + child.GetType().Name + ")");
            log.WriteLine("  " + kv.Key + "  in=" + blk.Inputs.Count + " out=" + blk.Outputs.Count
                          + " children=[" + string.Join(" | ", childNames.ToArray()) + "]");

            foreach (CogToolBlockTerminal t in blk.Inputs)
            {
                w.WriteLine(kv.Key + "\tIN\t" + t.Name + "\t" + ValueTypeName(t) + "\t" + ShortValue(t));
                total++;
            }
            foreach (CogToolBlockTerminal t in blk.Outputs)
            {
                w.WriteLine(kv.Key + "\tOUT\t" + t.Name + "\t" + ValueTypeName(t) + "\t" + ShortValue(t));
                total++;
            }
        }
        w.Close();
        log.WriteLine("terminals=" + total);
        Console.WriteLine("OK dump terminals=" + total + " -> " + workDir);
    }

    static string ValueTypeName(CogToolBlockTerminal t)
    {
        try
        {
            PropertyInfo pi = t.GetType().GetProperty("ValueType");
            if (pi != null)
            {
                object v = pi.GetValue(t, null);
                if (v != null) return v.ToString();
            }
        }
        catch { }
        try { return t.Value == null ? "-" : t.Value.GetType().Name; }
        catch { return "?"; }
    }

    static string ShortValue(CogToolBlockTerminal t)
    {
        try
        {
            object v = t.Value;
            if (v == null) return "";
            string s = v.ToString();
            return s.Length > 60 ? s.Substring(0, 60) + "..." : s;
        }
        catch { return "?"; }
    }

    // ---------------- apply：按 patch_map.tsv 打补丁 ----------------
    // patch_map.tsv 每行： 工具路径<TAB>补丁文件[<TAB>模板块路径]
    // 补丁文件内容为完整的脚本源文本；第 3 列只在目标块原本没有脚本时使用，
    // 会从模板块复制 References / Language 来新建 Script 对象。
    static void DoApply(string vpp, string workDir, string backupDir, string appBin)
    {
        string mapFile = Path.Combine(workDir, "patch_map.tsv");
        if (!File.Exists(mapFile)) throw new Exception("缺少 " + mapFile);

        List<string[]> map = new List<string[]>();
        foreach (string raw in File.ReadAllLines(mapFile, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            string[] cols = line.Split('\t');
            if (cols.Length < 2) throw new Exception("patch_map.tsv 格式错误: " + raw);
            map.Add(cols);
        }

        // 1) 备份
        if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = Path.GetFileNameWithoutExtension(vpp);
        string bak = Path.Combine(backupDir, baseName + "_" + stamp + Path.GetExtension(vpp));
        File.Copy(vpp, bak, true);
        log.WriteLine("backup -> " + bak + " (" + new FileInfo(bak).Length + ")");
        foreach (string side in Directory.GetFiles(Path.GetDirectoryName(vpp), baseName + "_*.json"))
        {
            string dst = Path.Combine(backupDir, Path.GetFileNameWithoutExtension(side) + "_" + stamp + Path.GetExtension(side));
            File.Copy(side, dst, true);
            log.WriteLine("backup -> " + dst);
        }

        // 2) 加载
        object root = LoadRecipe(vpp);
        List<KeyValuePair<string, CogToolBlock>> all = CollectBlocks(root);
        Dictionary<string, CogToolBlock> byPath = new Dictionary<string, CogToolBlock>(StringComparer.Ordinal);
        for (int i = 0; i < all.Count; i++) byPath[all[i].Key] = all[i].Value;

        PropertyInfo propScript = typeof(CogToolBlock).GetProperty("Script");
        Type csType = propScript.PropertyType;
        MethodInfo tempCompile = csType.GetMethod("TempCompile");
        PropertyInfo propErr = typeof(CogToolBlock).GetProperty("ScriptError");

        // 3) 打补丁
        Dictionary<string, string> expected = new Dictionary<string, string>(StringComparer.Ordinal);
        List<CogToolBlock> touched = new List<CogToolBlock>();
        List<string> touchedPath = new List<string>();

        foreach (string[] cols in map)
        {
            string path = cols[0].Trim().TrimEnd('/');
            if (path.Length == 0) path = "/";
            string pf = Path.Combine(workDir, cols[1].Trim());
            string tmplPath = cols.Length > 2 ? cols[2].Trim() : "";

            CogToolBlock blk;
            if (!byPath.TryGetValue(path, out blk)) throw new Exception("找不到工具块: " + path);
            if (!File.Exists(pf)) throw new Exception("找不到补丁文件: " + pf);
            string text = File.ReadAllText(pf, Encoding.UTF8);

            if (blk.Script == null || ScriptLength(blk) == 0)
            {
                if (tmplPath.Length == 0) throw new Exception("工具块没有脚本，且未指定模板块: " + path);
                CogToolBlock tmpl;
                if (!byPath.TryGetValue(tmplPath, out tmpl) || tmpl.Script == null)
                    throw new Exception("模板块无效或没有脚本: " + tmplPath);
                Type langType = csType.GetProperty("Language").PropertyType;
                ConstructorInfo ci = csType.GetConstructor(new Type[] { typeof(string), typeof(ArrayList), langType });
                if (ci == null) throw new Exception("未找到 CogScriptSupport(string, ArrayList, Language) 构造");
                ArrayList refs = new ArrayList();
                object oldRefs = csType.GetProperty("References").GetValue(tmpl.Script, null);
                if (oldRefs is ICollection) foreach (object o in (ICollection)oldRefs) refs.Add(o);
                object lang = csType.GetProperty("Language").GetValue(tmpl.Script, null);
                propScript.SetValue(blk, ci.Invoke(new object[] { text, refs, lang }), null);
                log.WriteLine("CREATE " + path + " refs=" + refs.Count + " len=" + text.Length);
            }
            else
            {
                int before = ScriptLength(blk);
                blk.Script.Source = text;
                log.WriteLine("PATCH  " + path + "  " + before + " -> " + ScriptLength(blk));
            }
            expected[path] = text;
            touched.Add(blk);
            touchedPath.Add(path);
        }

        // 4) 编译校验（不通过就不落盘）
        bool compileOk = true;
        for (int i = 0; i < touched.Count; i++)
        {
            string res;
            try { res = "TempCompile=" + tempCompile.Invoke(touched[i].Script, null); }
            catch (Exception ex) { res = "TempCompile EXC " + ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
            string err = "";
            try { err = "" + propErr.GetValue(touched[i], null); } catch { }
            bool bad = res.IndexOf("False", StringComparison.Ordinal) >= 0 || res.IndexOf("EXC", StringComparison.Ordinal) >= 0
                       || (err != null && err.IndexOf("error CS", StringComparison.Ordinal) >= 0);
            if (bad) compileOk = false;
            log.WriteLine("COMPILE " + touchedPath[i] + "  " + res + (bad ? "  <<< FAILED" : "  OK"));
            if (bad) log.WriteLine("        " + err);
        }
        if (!compileOk) throw new Exception("脚本编译校验失败，未保存");

        // 5) 保存到临时文件
        string np = vpp + ".new";
        if (File.Exists(np)) File.Delete(np);
        DateTime t0 = DateTime.Now;
        SaveRecipe(root, np);
        log.WriteLine("saved " + np + " in " + (DateTime.Now - t0).TotalSeconds.ToString("F1") + "s size=" + new FileInfo(np).Length);

        // 6) 重新加载校验，通过才替换
        object check = LoadRecipe(np);
        List<KeyValuePair<string, CogToolBlock>> all2 = CollectBlocks(check);
        Dictionary<string, CogToolBlock> byPath2 = new Dictionary<string, CogToolBlock>(StringComparer.Ordinal);
        for (int i = 0; i < all2.Count; i++) byPath2[all2[i].Key] = all2[i].Value;

        int matched = 0;
        foreach (string path in new List<string>(expected.Keys))
        {
            CogToolBlock blk;
            if (!byPath2.TryGetValue(path, out blk)) { log.WriteLine("VERIFY " + path + " MISSING"); continue; }
            string src = blk.Script == null ? null : blk.Script.Source;
            bool same = src == expected[path];
            if (same) matched++;
            log.WriteLine("VERIFY " + path + " len=" + (src == null ? -1 : src.Length) + " match=" + same);
        }
        log.WriteLine("VERIFY matched " + matched + "/" + expected.Count);

        if (matched != expected.Count) throw new Exception("回读校验不通过，原文件未替换，新文件保留在 " + np);

        File.Copy(np, vpp, true);
        File.Delete(np);
        log.WriteLine("REPLACED " + vpp);
        Console.WriteLine("OK apply patched=" + expected.Count + " backup=" + bak);
    }
}
