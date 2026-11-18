using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaExport
{
    /// <summary>
    /// Entry point kept free of any Siemens.Engineering types so that the
    /// AssemblyResolve hook is registered before the Openness assemblies are
    /// first needed. Openness requires its assemblies to be loaded from their
    /// original installation folder (Copy Local = false).
    /// </summary>
    internal static class Bootstrap
    {
        private static readonly string[] ResolveRoots =
        {
            @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48",
            @"C:\Program Files\Siemens\Automation\Portal V21\Bin\PublicAPI",
            @"C:\Program Files\Siemens\Automation\Portal V21\Bin\PublicAPI\Client",
            @"C:\Program Files\Siemens\Automation\Portal V21\Bin\PublicAPI\V21\net48",
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            return Program.Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Assembly OnResolve(object sender, ResolveEventArgs e)
        {
            string simpleName = new AssemblyName(e.Name).Name;
            foreach (string root in ResolveRoots)
            {
                string path = Path.Combine(root, simpleName + ".dll");
                if (File.Exists(path))
                {
                    try
                    {
                        Console.WriteLine("  [resolve] " + simpleName + " -> " + path);
                        return Assembly.LoadFrom(path);
                    }
                    catch
                    {
                        // fall through to the next candidate root
                    }
                }
            }
            return null;
        }
    }

    internal static class Program
    {
        private static readonly StringBuilder LogBuf = new StringBuilder();
        private static readonly List<string> Manifest = new List<string>();

        private static void Log(string msg)
        {
            Console.WriteLine(msg);
            LogBuf.AppendLine(msg);
        }

        internal static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: TiaExport <project.apXX> <outDir>");
                return 2;
            }

            string projFile = Path.GetFullPath(args[0]);
            string outDir = Path.GetFullPath(args[1]);
            string xmlDir = Path.Combine(outDir, "xml");
            Directory.CreateDirectory(xmlDir);

            Manifest.Add("Device,Software,GroupPath,BlockName,Number,Type,Language,KnowHowProtected,Consistent,Status,XmlFile,StFile");

            TiaPortal tia = null;
            Project project = null;
            try
            {
                Log("[1] Creating TIA Portal session (TiaPortalMode.WithoutUserInterface) ...");
                tia = new TiaPortal(TiaPortalMode.WithoutUserInterface);
                Log("    Session created. Openness assembly: " + typeof(TiaPortal).Assembly.GetName().FullName);

                Log("[2] Opening project: " + projFile);
                project = tia.Projects.Open(new FileInfo(projFile));
                Log("    Project name: " + project.Name);

                var plcSoftwares = new List<PlcSoftware>();
                foreach (Device device in project.Devices)
                {
                    Log("[3] Device: " + device.Name);
                    foreach (DeviceItem di in AllDeviceItems(device))
                    {
                        SoftwareContainer sc = null;
                        try { sc = di.GetService<SoftwareContainer>(); }
                        catch { }
                        if (sc == null || sc.Software == null) continue;

                        PlcSoftware plc = sc.Software as PlcSoftware;
                        Log("      SoftwareContainer on '" + di.Name + "' -> " + sc.Software.GetType().FullName);
                        if (plc != null) plcSoftwares.Add(plc);
                    }
                }

                if (plcSoftwares.Count == 0)
                {
                    Log("!! No PlcSoftware found in this project.");
                    return 3;
                }

                foreach (PlcSoftware plc in plcSoftwares)
                {
                    ProcessPlc(plc, outDir, xmlDir);
                }
            }
            catch (Exception ex)
            {
                Log("FATAL: " + Describe(ex));
                return 1;
            }
            finally
            {
                try { if (project != null) project.Close(); } catch (Exception e) { Log("close-project error: " + e.Message); }
                try { if (tia != null) tia.Dispose(); } catch (Exception e) { Log("dispose error: " + e.Message); }
                File.WriteAllText(Path.Combine(outDir, "export.log"), LogBuf.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outDir, "manifest.csv"), string.Join(Environment.NewLine, Manifest), new UTF8Encoding(true));
            }

            return 0;
        }

        private static IEnumerable<DeviceItem> AllDeviceItems(Device device)
        {
            foreach (DeviceItem di in device.DeviceItems)
            {
                yield return di;
                foreach (DeviceItem sub in AllDeviceItems(di)) yield return sub;
            }
        }

        private static IEnumerable<DeviceItem> AllDeviceItems(DeviceItem item)
        {
            foreach (DeviceItem di in item.DeviceItems)
            {
                yield return di;
                foreach (DeviceItem sub in AllDeviceItems(di)) yield return sub;
            }
        }

        private static void ProcessPlc(PlcSoftware plc, string outDir, string xmlDir)
        {
            Log("[4] PLC software: '" + plc.Name + "'");

            var collected = new List<PlcBlock>();
            string blockXml = Path.Combine(xmlDir, "blocks");
            Directory.CreateDirectory(blockXml);

            CollectBlocks(plc.BlockGroup, "", plc, blockXml, collected);

            Log("    Blocks found: " + collected.Count);
            var byLang = collected.GroupBy(b => SafeLang(b)).OrderBy(g => g.Key);
            foreach (var g in byLang) Log("      language " + g.Key + " : " + g.Count());
            var byType = collected.GroupBy(b => b.GetType().Name).OrderBy(g => g.Key);
            foreach (var g in byType) Log("      type " + g.Key + " : " + g.Count());

            // --- Generate SCL (external) source from ALL blocks ---
            string srcDir = Path.Combine(outDir, "source");
            Directory.CreateDirectory(srcDir);
            GenerateSources(plc, collected, srcDir);

            // --- Tag tables ---
            string tagDir = Path.Combine(outDir, "tags");
            Directory.CreateDirectory(tagDir);
            ExportTagTables(plc.TagTableGroup, "", plc, tagDir);

            // --- UDTs ---
            string typeDir = Path.Combine(outDir, "types");
            Directory.CreateDirectory(typeDir);
            ExportTypes(plc.TypeGroup, "", plc, typeDir);
        }

        private static string SafeLang(PlcBlock b)
        {
            try { return b.ProgrammingLanguage.ToString(); } catch { return "?"; }
        }

        private static void CollectBlocks(PlcBlockGroup group, string rel, PlcSoftware plc, string xmlDir, List<PlcBlock> collected)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                collected.Add(block);
                string type = block.GetType().Name;
                string lang = SafeLang(block);
                string number = "", status = "OK", kh = "", consist = "";
                try { number = block.Number.ToString(); } catch { }
                try { kh = block.IsKnowHowProtected.ToString(); } catch { }
                try { consist = block.IsConsistent.ToString(); } catch { }

                string dir = Path.Combine(xmlDir, Sanitize(rel));
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, Sanitize(type) + "_" + Sanitize(block.Name) + "_" + Sanitize(number) + ".xml");
                try
                {
                    block.Export(new FileInfo(file), ExportOptions.WithDefaults);
                }
                catch (Exception e)
                {
                    status = "FAIL:" + e.Message.Replace("\r", " ").Replace("\n", " ").Replace(",", ";");
                    Log("      EXPORT FAILED  " + block.Name + " : " + e.Message);
                }

                Manifest.Add(string.Join(",",
                    Csv(plc.Name), Csv(plc.Name), Csv(rel), Csv(block.Name), Csv(number), Csv(type), Csv(lang),
                    kh, consist, Csv(status), Csv(status == "OK" ? Rel(xmlDir, file) : ""), ""));
            }

            foreach (PlcBlockGroup sub in group.Groups)
            {
                CollectBlocks(sub, rel.Length == 0 ? sub.Name : rel + "\\" + sub.Name, plc, xmlDir, collected);
            }
        }

        private static void GenerateSources(PlcSoftware plc, List<PlcBlock> blocks, string srcDir)
        {
            Log("[5] GenerateSource (SCL external source) ...");

            // whole set first
            string all = Path.Combine(srcDir, "AllBlocks.scl");
            try
            {
                plc.ExternalSourceGroup.GenerateSource(blocks, new FileInfo(all), GenerateOptions.None);
                Log("    OK  GenerateSource(all blocks, WithDependencies=None) -> " + all);
            }
            catch (Exception e)
            {
                Log("    FAIL GenerateSource(all blocks): " + e.Message);
                try
                {
                    plc.ExternalSourceGroup.GenerateSource(blocks, new FileInfo(all), GenerateOptions.WithDependencies);
                    Log("    OK  GenerateSource(all blocks, WithDependencies) -> " + all);
                }
                catch (Exception e2)
                {
                    Log("    FAIL GenerateSource(all blocks, WithDependencies): " + e2.Message);
                }
            }

            // per-language sets
            foreach (var g in blocks.GroupBy(b => SafeLang(b)))
            {
                string f = Path.Combine(srcDir, "Lang_" + Sanitize(g.Key) + ".scl");
                try
                {
                    plc.ExternalSourceGroup.GenerateSource(g.ToList(), new FileInfo(f), GenerateOptions.None);
                    Log("    OK  GenerateSource(" + g.Key + ", " + g.Count() + " blocks) -> " + f);
                }
                catch (Exception e)
                {
                    Log("    FAIL GenerateSource(" + g.Key + "): " + e.Message);
                }
            }

            // per block
            string perDir = Path.Combine(srcDir, "per_block");
            Directory.CreateDirectory(perDir);
            foreach (PlcBlock b in blocks)
            {
                string f = Path.Combine(perDir, Sanitize(b.GetType().Name) + "_" + Sanitize(b.Name) + ".scl");
                try
                {
                    plc.ExternalSourceGroup.GenerateSource(new List<PlcBlock> { b }, new FileInfo(f), GenerateOptions.None);
                }
                catch (Exception e)
                {
                    Log("      per-block FAIL " + b.Name + " : " + e.Message.Replace("\r", " ").Replace("\n", " "));
                }
            }
            Log("    per-block generation done.");
        }

        private static void ExportTagTables(PlcTagTableGroup grp, string rel, PlcSoftware plc, string tagDir)
        {
            foreach (PlcTagTable t in grp.TagTables)
            {
                string dir = Path.Combine(tagDir, Sanitize(rel));
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, Sanitize(t.Name) + ".xml");
                try
                {
                    t.Export(new FileInfo(f), ExportOptions.WithDefaults);
                    Log("    tag table OK: " + t.Name);
                }
                catch (Exception e) { Log("    tag table FAIL: " + t.Name + " : " + e.Message); }
            }
            foreach (PlcTagTableGroup sub in grp.Groups)
            {
                ExportTagTables(sub, rel.Length == 0 ? sub.Name : rel + "\\" + sub.Name, plc, tagDir);
            }
        }

        private static void ExportTypes(PlcTypeGroup grp, string rel, PlcSoftware plc, string typeDir)
        {
            foreach (PlcType t in grp.Types)
            {
                string dir = Path.Combine(typeDir, Sanitize(rel));
                Directory.CreateDirectory(dir);
                string f = Path.Combine(dir, Sanitize(t.Name) + ".xml");
                try
                {
                    t.Export(new FileInfo(f), ExportOptions.WithDefaults);
                    Log("    UDT OK: " + t.Name);
                }
                catch (Exception e) { Log("    UDT FAIL: " + t.Name + " : " + e.Message); }
            }
            foreach (PlcTypeGroup sub in grp.Groups)
            {
                ExportTypes(sub, rel.Length == 0 ? sub.Name : rel + "\\" + sub.Name, plc, typeDir);
            }
        }

        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            int depth = 0;
            while (ex != null && depth < 6)
            {
                sb.AppendLine(new string(' ', depth * 2) + "[" + depth + "] " + ex.GetType().FullName + ": " + ex.Message);
                if (ex.StackTrace != null) sb.AppendLine(new string(' ', depth * 2) + "    " + ex.StackTrace.Replace("\r", "").Replace("\n", "\n    "));
                ex = ex.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        private static string Rel(string root, string full)
        {
            string r = root.TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full.Substring(r.Length) : full;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            char[] bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(bad.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            string v = s.Replace("\"", "\"\"");
            return "\"" + v + "\"";
        }
    }
}
