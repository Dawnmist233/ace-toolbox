using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// ============================================================
//  ACE 工具箱（复刻版）
//  启动/激活 ACE（免重启）· 静默关闭 ACE · 彻底卸载 ACE
//  原理：直接拉起 AntiCheatExpert 的驱动服务 + SGuard + ACE-Tray，
//        省掉「游戏预启动 + 重启」这一步。
// ============================================================
class AceTool
{
    const string ACE_DIR = @"C:\Program Files\AntiCheatExpert";
    const string ACE_DIR86 = @"C:\Program Files (x86)\AntiCheatExpert";
    const string ACE_DATA = @"C:\ProgramData\AntiCheatExpert";
    const string REG_UNINST = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AntiCheatExpert";
    const string REG_UNINST_WOW = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\AntiCheatExpert";
    const string REG_APPLOW = @"SOFTWARE\AppDataLow\AntiCheatExpert";

    // v3.0.8：本地进程清单清空 —— 只能由 ops 下发（disable|kill|... / close|kill|...）
#if NOLICENSE
    static readonly string[] KILL_PROCS = new string[] {
        "ACE-Tray.exe", "ACE-TrayV.exe", "ACE-Service64.exe", "ACE-BOOT.exe",
        "SGuard64.exe", "SGuardSvc64.exe", "SGuardSvc32.exe"
    };
#else
    static readonly string[] KILL_PROCS = new string[0];
#endif

    static Form UI;
    static ListView LvStatus;
    static TextBox TxtResult;
    static Button BtnInstall, BtnStart, BtnClose, BtnUninst, BtnRefresh;

    static Action<int> grpRstHTopAdjust;

    static Action SyncAct;
    static Label LblHint, LblLic, LblFoot;
    static string LogPath;
    static bool IsAdmin;
    static bool DryMode;
    static bool CliMode;
    static bool CliVerbose;

    // ---------- 日志：详细过程只进文件 ----------
    static void Log(string s)
    {
        if (CliMode && CliVerbose) { try { Console.WriteLine(s); } catch { } }
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n", Encoding.UTF8); } catch { }
    }

    // ---------- 结果：只往界面上那一行写结论 ----------
    static void Result(string s)
    {
        Log("[结果] " + s);
        if (CliMode) { try { Console.WriteLine("=> " + s); } catch { } return; }
        try
        {
            if (TxtResult != null && UI != null && UI.IsHandleCreated)
                UI.BeginInvoke((Action)(() =>
                {
                    if (TxtResult.TextLength > 0) TxtResult.AppendText("\r\n");
                    TxtResult.AppendText(s);
                    TxtResult.SelectionStart = TxtResult.TextLength;
                    TxtResult.ScrollToCaret();
                }));
        }
        catch { }
    }

    // 只有会改系统的命令才在模拟模式下跳过；查询类照常执行
    static bool IsMutating(string file, string args)
    {
        string f = (file ?? "").ToLowerInvariant();
        string a = (args ?? "").ToLowerInvariant();
        if (f.EndsWith("taskkill") || f.EndsWith("taskkill.exe")) return true;
        if (f == "rd" || f.EndsWith("\\rd") || f.EndsWith("rd.exe")) return true;
        if (f == "sc" || f.EndsWith("sc.exe"))
            return a.StartsWith("start") || a.StartsWith("stop") || a.StartsWith("delete")
                || a.StartsWith("create") || a.StartsWith("config");
        if (f.EndsWith("ace-setup64.exe") || f.EndsWith("uninstaller.exe")) return true;
        return false;
    }

    // 带工作目录的执行（ACE-Setup64.exe 要在自己目录里跑）
    static int RunCmdIn(string workDir, string file, string args, out string output)
    {
        output = "";
        if (DryMode && IsMutating(file, args)) { Log("[dry] " + file + " " + args); return 0; }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(180000);
                Log("  " + file + " " + args + " -> " + p.ExitCode);
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            output = ex.Message;
            Log("  执行失败 " + file + " " + args + " : " + ex.Message);
            return -1;
        }
    }

    // ---------- 执行外部命令 ----------
    static int RunCmd(string file, string args, out string output)
    {
        output = "";
        if (DryMode && IsMutating(file, args)) { Log("[dry] " + file + " " + args); return 0; }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(30000);
                string brief = output == null ? "" : output.Trim().Replace("\r\n", " ");
                if (brief.Length > 160) brief = brief.Substring(0, 160) + " …";
                Log("  " + file + " " + args + " -> " + p.ExitCode + (brief.Length == 0 ? "" : " | " + brief));
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            Log("  执行失败 " + file + " " + args + " : " + ex.Message);
            output = ex.Message;
            return -1;
        }
    }

    static int RunCmd(string file, string args)
    {
        string o;
        return RunCmd(file, args, out o);
    }

    // ---------- 找 ACE 相关服务（绝不误伤别的反作弊）----------
    static List<string[]> cR8sT5()
    {
        var list = new List<string[]>();
        string outp;
        if (RunCmd("sc", "query type= all state= all", out outp) != 0 && outp.Length == 0) return list;

        var names = new List<string>();
        foreach (string line in outp.Split('\n'))
        {
            if (line.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int c = line.IndexOf(':');
                if (c > 0)
                {
                    string nm = line.Substring(c + 1).Trim();
                    if (nm.Length > 0) names.Add(nm);
                }
            }
        }

        foreach (string nm in names)
        {
            string up = nm.ToUpperInvariant();
            if (!(up.Contains("ANTICHEATEXPERT") || up.Contains("SGUARD") ||
                  up.StartsWith("ACE-") || up.StartsWith("ACE_") || up.Contains("ACE-GAME")))
                continue;

            string qc;
            RunCmd("sc", "qc \"" + nm + "\"", out qc);
            if (qc.IndexOf("AntiCheatExpert", StringComparison.OrdinalIgnoreCase) < 0 &&
                !up.StartsWith("ACE-") && !up.StartsWith("ACE_") &&
                !up.Contains("ACE-GAME") && !up.Contains("ANTICHEATEXPERT") && !up.Contains("SGUARD"))
                continue;

            string bin = "", display = "";
            foreach (string line in qc.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("BINARY_PATH_NAME")) bin = t.Substring(t.IndexOf(':') + 1).Trim();
                if (t.StartsWith("DISPLAY_NAME")) display = t.Substring(t.IndexOf(':') + 1).Trim();
            }
            list.Add(new string[] { nm, bin, display });
        }
        return list;
    }

    static string ServiceState(string name)
    {
        string o;
        RunCmd("sc", "query \"" + name + "\"", out o);
        foreach (string line in o.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("STATE"))
            {
                int c = t.IndexOf(':');
                string v = c > 0 ? t.Substring(c + 1).Trim() : t;
                if (v.Contains("RUNNING")) return "运行中";
                if (v.Contains("STOPPED")) return "已停止";
                if (v.Contains("START_PENDING")) return "启动中";
                if (v.Contains("STOP_PENDING")) return "停止中";
                return v;
            }
        }
        return "未知";
    }

    static bool ProcRunning(string name)
    {
        try { return Process.GetProcessesByName(name.Replace(".exe", "")).Length > 0; }
        catch { return false; }
    }

    static string FindAceDir()
    {
        if (Directory.Exists(ACE_DIR)) return ACE_DIR;
        if (Directory.Exists(ACE_DIR86)) return ACE_DIR86;
        return null;
    }

    // ---------- 状态列表 ----------
    static void RefreshStatus()
    {
        var rows = new List<string[]>();
        string dir = FindAceDir();
        if (dir == null)
        {
            rows.Add(new string[] { "ACE 安装目录", "未检测到" });
        }
        else
        {
            string[] key = { "ACE-Tray.exe", "ACE-Service64.exe", "ACE-BASE.sys", "ACE-BOOT.sys", "Uninstaller.exe" };
            int have = 0;
            foreach (string f in key) if (File.Exists(Path.Combine(dir, f))) have++;
            rows.Add(new string[] { "ACE 安装目录", dir + "   （核心文件 " + have + "/" + key.Length + "）" });
        }
        rows.Add(new string[] { "ACE 配置目录", Directory.Exists(ACE_DATA) ? ACE_DATA : "未检测到" });

        var svcs = cR8sT5();
        int running = 0;
        foreach (string[] s in svcs)
        {
            string st = ServiceState(s[0]);
            if (st == "运行中") running++;
            rows.Add(new string[] { "服务：" + s[0], st });
        }

        var procs = new List<string>();
        foreach (string pn in KILL_PROCS) if (ProcRunning(pn)) procs.Add(pn);
        rows.Add(new string[] { "ACE 进程", procs.Count == 0 ? "无" : string.Join("、", procs.ToArray()) });
        rows.Add(new string[] { "服务汇总", running + " / " + svcs.Count + " 运行中" });

        List<string> gameRun = RunningGameProcs();
        rows.Add(new string[] { "游戏进程", gameRun.Count == 0 ? "未运行" : ("运行中：" + string.Join("、", gameRun.ToArray())) });

        if (CliMode)
        {
            foreach (string[] r in rows) Console.WriteLine("  " + r[0] + "  " + r[1]);
            return;
        }
        try
        {
            if (LvStatus != null && UI != null && UI.IsHandleCreated)
                UI.BeginInvoke((Action)(() =>
                {
                    LvStatus.BeginUpdate();
                    LvStatus.Items.Clear();
                    foreach (string[] r in rows)
                    {
                        ListViewItem it = new ListViewItem(r[0]);
                        it.SubItems.Add(r[1]);
                        if (r[0] == "游戏进程")
                            it.ForeColor = r[1] == "未运行" ? Color.SeaGreen : Color.Firebrick;
                        else
                            it.ForeColor = r[1] == "运行中" ? Color.SeaGreen
                                        : (r[1].StartsWith("未检测到") ? Color.Firebrick : SystemColors.ControlText);
                        LvStatus.Items.Add(it);
                    }
                    LvStatus.EndUpdate();

                    // 按钮联动：没装只让点“安装”，装了才能启停/卸载
                    bool installed = dir != null || svcs.Count > 0;
                    if (BtnInstall != null && BtnStart != null)
                    {
                        BtnInstall.Enabled = IsAdmin;
                        BtnStart.Enabled = IsAdmin && installed;
                        BtnClose.Enabled = IsAdmin && installed;
                        BtnUninst.Enabled = IsAdmin && installed;
                    }
                }));
        }
        catch { }
    }

    // ---------- 找 ACE 的安装器（游戏目录里那份）----------
    // ---------- 游戏是否在运行 ----------
    static string GameDir;   // 游戏根目录（含 AntiCheatExpert 的那层）

    static List<string> GameExeNames()
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(GameDir) && Directory.Exists(GameDir))
        {
            try
            {
                foreach (string f in Directory.GetFiles(GameDir, "*.exe"))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    string ln = n.ToLowerInvariant();
                    if (ln.StartsWith("ace") || ln == "uninstaller" || ln.StartsWith("unitycrashhandler")) continue;
                    if (!list.Contains(n)) list.Add(n);
                }
            }
            catch { }
        }
        foreach (string n in new string[] { "Endfield", "PlatformProcess" })
            if (!list.Contains(n)) list.Add(n);
        return list;
    }

    static List<string> RunningGameProcs()
    {
        var hit = new List<string>();
        foreach (string n in GameExeNames())
        {
            try
            {
                if (Process.GetProcessesByName(n).Length > 0) hit.Add(n + ".exe");
            }
            catch { }
        }
        return hit;
    }

    // 安装/卸载前拦一道：游戏在跑就确认
    static bool ConfirmGameClosed(string action)
    {
        List<string> run = RunningGameProcs();
        if (run.Count == 0) return true;
        string names = string.Join("、", run.ToArray());
        Log("检测到游戏在运行：" + names);

        if (DryMode) { Result("（模拟）检测到游戏在运行：" + names + "，正式执行会先让你确认。"); return true; }

        if (CliMode)
        {
            Result("✗ 已阻止：" + action + "前请先关闭游戏（检测到 " + names + "）。");
            return false;
        }

        DialogResult r = DialogResult.No;
        try
        {
            UI.Invoke((Action)(() =>
            {
                r = MessageBox.Show(
                    "检测到游戏正在运行：" + names + "\r\n\r\n" + action + "前建议先关掉游戏，"
                    + "否则文件被占用会导致失败。\r\n\r\n仍要继续吗？",
                    "游戏正在运行", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            }));
        }
        catch { }
        if (r != DialogResult.Yes)
        {
            Result("已取消：" + action + "前请先关闭游戏（检测到 " + names + "）。");
            return false;
        }
        Log("用户选择继续（游戏仍在运行）");
        return true;
    }

    // 扫各固定盘找游戏里的 AntiCheatExpert 目录（顺便记住游戏根目录，用于判断游戏是否在跑）
    static string DiscoverAceSourceDir()
    {
        if (!string.IsNullOrEmpty(GameDir))
        {
            string cached = Path.Combine(GameDir, "AntiCheatExpert");
            if (Directory.Exists(cached)) return cached;
        }
        try
        {
            foreach (DriveInfo drv in DriveInfo.GetDrives())
            {
                if (!drv.IsReady || drv.DriveType != DriveType.Fixed) continue;
                string[] dirs1;
                try { dirs1 = Directory.GetDirectories(drv.RootDirectory.FullName); }
                catch { continue; }
                foreach (string d1 in dirs1)
                {
                    string c1 = Path.Combine(d1, "AntiCheatExpert");
                    if (File.Exists(Path.Combine(c1, "ACE-Setup64.exe"))) { GameDir = d1; return c1; }
                    string[] dirs2;
                    try { dirs2 = Directory.GetDirectories(d1); }
                    catch { continue; }
                    foreach (string d2 in dirs2)
                    {
                        string c2 = Path.Combine(d2, "AntiCheatExpert");
                        if (File.Exists(Path.Combine(c2, "ACE-Setup64.exe"))) { GameDir = d2; return c2; }
                    }
                }
            }
        }
        catch (Exception ex) { Log("扫描游戏目录出错：" + ex.Message); }
        return null;
    }

    static string FindAceSetup()
    {
        // 已安装目录里那份（Uninstaller.exe 就是同一个程序）
        foreach (string d in new string[] { ACE_DIR, ACE_DIR86 })
        {
            string a = Path.Combine(d, "ACE-Setup64.exe");
            if (File.Exists(a)) return a;
            string u = Path.Combine(d, "Uninstaller.exe");
            if (File.Exists(u)) return u;
        }
        // 游戏目录里那份
        string src = DiscoverAceSourceDir();
        if (src != null)
        {
            string c = Path.Combine(src, "ACE-Setup64.exe");
            if (File.Exists(c)) { Log("找到安装器：" + c + "（游戏目录 " + GameDir + "）"); return c; }
        }
        return null;
    }

    static string AskSetupPath()
    {
        string picked = null;
        try
        {
            UI.Invoke((Action)(() =>
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = "请选择游戏目录里的 ACE-Setup64.exe";
                    ofd.Filter = "ACE 安装器|ACE-Setup64.exe;Uninstaller.exe|可执行文件|*.exe";
                    if (ofd.ShowDialog() == DialogResult.OK) picked = ofd.FileName;
                }
            }));
        }
        catch (Exception ex) { Log("选择文件失败：" + ex.Message); }
        return picked;
    }

    // ---------- 安装 ACE ----------
    // ================= IL 指纹封存（v3.0.9 / P1-1）=================
    // 期望指纹 = 构建期写入的 16 个常量；实时指纹 = 运行时算出的 16 个 IL 哈希
    static uint xD6fV5()
    {
        int[] v = new int[] { H0, H1, H2, H3, H4, H5, H6, H7, H8, H9, H10, H11, H12, H13, H14, H15, H16, H17, H18, H19, H20, H21 };
        uint h = 2166136261u;
        for (int i = 0; i < v.Length; i++)
        {
            uint x = (uint)v[i];
            for (int b = 0; b < 4; b++) { h ^= (byte)(x >> (b * 8)); h *= 16777619u; }
        }
        return h;
    }

    static uint gL2mV4()
    {
        try
        {
            Type t = typeof(AceTool);
            uint h = 2166136261u;
            for (int i = 0; i < ILN.Length; i++)
            {
                System.Reflection.MethodInfo m = t.GetMethod(ILN[i],
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (m == null) return 0;
                System.Reflection.MethodBody body = m.GetMethodBody();
                if (body == null) return 0;
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return 0;
                uint x = nD3sF6(il);
                for (int b = 0; b < 4; b++) { h ^= (byte)(x >> (b * 8)); h *= 16777619u; }
            }
            return h;
        }
        catch { return 0; }
    }

    // 用指纹派生 32 字节密钥流，循环异或
    static byte[] yN8bC3(uint fp, int len)
    {
        byte[] seed = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            seed[i] = (byte)(fp >> (i * 8));
            seed[4 + i] = (byte)(0xA5 ^ (fp >> (i * 8)));
        }
        byte[] ks = new byte[len];
        int off = 0;
        int ctr = 0;
        while (off < len)
        {
            byte[] blk = new byte[seed.Length + 4];
            Buffer.BlockCopy(seed, 0, blk, 0, seed.Length);
            blk[seed.Length] = (byte)ctr;
            blk[seed.Length + 1] = (byte)(ctr >> 8);
            blk[seed.Length + 2] = (byte)(ctr >> 16);
            blk[seed.Length + 3] = (byte)(ctr >> 24);
            byte[] hh;
            using (SHA256 sha = SHA256.Create()) hh = sha.ComputeHash(blk);
            int n = Math.Min(32, len - off);
            Buffer.BlockCopy(hh, 0, ks, off, n);
            off += n;
            ctr++;
        }
        return ks;
    }

    static byte[] uJ1kR7(byte[] data, uint fp)
    {
        byte[] ks = yN8bC3(fp, data.Length);
        byte[] o = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ ks[i]);
        Array.Clear(ks, 0, ks.Length);
        return o;
    }

    static byte[] OpsSealed = null;      // 用期望指纹封存的 ops 明文

    // v3.1.5：把实时 hash 交给原生，拿到「指纹」；不一致返回 0
    static uint NativeFP()
    {
        try
        {
            if (!zB4oO6()) return 0;
            int[] live = bX9vK3();
            if (live == null || AgFingerprint == null) return 0;
            IntPtr buf = Marshal.AllocHGlobal(live.Length * 4);
            try
            {
                Marshal.Copy(live, 0, buf, live.Length);
                return (uint)AgFingerprint(buf, live.Length);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }

    static void rY3tG6(byte[] plain)
    {
        try
        {
            // v3.1.5：优先用原生指纹（期望值在机器码里）
            uint fp = NativeFP();
            if (fp == 0) fp = xD6fV5();
            OpsSealed = uJ1kR7(plain, fp);
        }
        catch { OpsSealed = null; }
    }

    static string sP9oH2()
    {
        try
        {
            if (OpsSealed == null) return null;
            uint fp = NativeFP();                 // v3.1.5：交给原生判定+派生
            if (fp == 0) { Log("解封失败：原生指纹为 0（IL 被改动）"); return ""; }
            byte[] plain = uJ1kR7(OpsSealed, fp);
            string t = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return t;
        }
        catch { return ""; }
    }
    // =============================================================

    // ================= 关键方法 IL 自校验（v3.0.4）=================
    // 覆盖范围包含「校验器自己」和「三道本地闸门」：
    // 想绕过 Port 必须同时改 pX2vN9 + bN7uA3 + 三个动作，且每个都要保持一致
    const int H0 = 303800821;
    const int H1 = 571635761;
    const int H2 = 989903223;
    const int H3 = -699616532;
    const int H4 = 2075538902;
    const int H5 = 1560914675;
    const int H6 = 4058603;
    const int H7 = -542926237;
    const int H8 = 341745411;
    const int H9 = 729222028;
    const int H10 = -721562609;
    const int H11 = -2108860175;
    const int H12 = -1166345355;
    const int H13 = -1910741651;
    const int H14 = 527641526;
    const int H15 = 1124521789;
    const int H16 = 1997165696;
    const int H17 = -1661716904;
    const int H18 = -384794635;
    const int H19 = -1411403284;
    const int H20 = -623767805;
    const int H21 = -598220926;

    static readonly string[] ILN = new string[] { "Main", "rT9gG2", "tB9gL2", "sY5hH8", "pX2vN9", "bN7uA3", "aF4hT9", "eG7jU2", "iO3pV6",
                                          "mR2wZ5", "fT8kQ1", "mU6vZ7", "oX3cC1", "cK5iE8", "zQ4mR7", "nD3sF6", "rY3tG6", "sP9oH2", "wX8lL9", "bP6qR2", "cR8sT5", "bX9vK3" };

    static uint nD3sF6(byte[] b)
    {
        uint h = 2166136261u;
        for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619u; }
        return h;
    }

    // 取 21 个方法的实时 FNV（给原生比对）
    static int[] bX9vK3()
    {
        try
        {
            Type t = typeof(AceTool);
            int[] r = new int[ILN.Length];
            for (int i = 0; i < ILN.Length; i++)
            {
                System.Reflection.MethodInfo m = t.GetMethod(ILN[i],
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (m == null) return null;
                System.Reflection.MethodBody body = m.GetMethodBody();
                if (body == null) return null;
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return null;
                r[i] = (int)nD3sF6(il);
            }
            return r;
        }
        catch { return null; }
    }

    static int hY5cJ8(int i)
    {
        switch (i)
        {
            case 0: return H0;
            case 1: return H1;
            case 2: return H2;
            case 3: return H3;
            case 4: return H4;
            case 5: return H5;
            case 6: return H6;
            case 7: return H7;
            case 8: return H8;
            case 9: return H9;
            case 10: return H10;
            case 11: return H11;
            case 12: return H12;
            case 13: return H13;
            case 14: return H14;
            case 15: return H15;
            case 16: return H16;
            case 17: return H17;
            case 18: return H18;
            case 19: return H19;
            case 20: return H20;
            case 21: return H21;
            default: return H8;
        }
    }

    static bool kW7bT3()
    {
        try
        {
            // v3.1.4：先交给原生判断（期望值在机器码里，改 IL 的 bool 无效）
            if (zB4oO6())
            {
                int[] live = bX9vK3();
                if (live == null) { qW1eD9("live-il-null"); return false; }
                if (AgCheckIL != null)
                {
                    IntPtr buf = Marshal.AllocHGlobal(live.Length * 4);
                    try
                    {
                        Marshal.Copy(live, 0, buf, live.Length);
                        int r = AgCheckIL(buf, live.Length);
                        if (r == 0) { qW1eD9("native-ilcheck"); return false; }
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            Type t = typeof(AceTool);
            for (int i = 0; i < ILN.Length; i++)
            {
                int want = hY5cJ8(i);
                if (want == 0) return false;
                System.Reflection.MethodInfo m = t.GetMethod(ILN[i],
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (m == null) return false;
                System.Reflection.MethodBody body = m.GetMethodBody();
                if (body == null) return false;
                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return false;
                if ((int)nD3sF6(il) != want) return false;
            }
            return true;
        }
        catch { return false; }
    }

    // v3.0.4：失败直接退出。攻击者把 tB9gL2/bN7uA3 改成 ret 也救不回来，
    // 因为退出发生在动作真正执行之前。
    static bool zQ4mR7(string where)
    {
#if NOLICENSE
        return true;          // 免卡密构建：不做完整性校验
#else
        if (kW7bT3()) return true;
        qW1eD9("IL_TAMPER@" + where);
        try { Log("完整性校验失败：" + where); } catch { }
        try { Result("✗ 程序完整性校验失败"); } catch { }
        System.Threading.Thread.Sleep(1500);
        Environment.Exit(9);
        return false;
#endif
    }

    // 兼容旧调用点
    static bool pX2vN9(string where)
    {
        return zQ4mR7(where);
    }
    // =========================================================

    // ================= 授权态（v3.0.0）=================
    // 不再靠单个 bool：每次动作前都要重新过一遍「令牌 + 设备 + 时间 + 降级标记」
    static volatile bool DegradedMode = false;     // 触发后所有动作拒绝
    static volatile string DegradedReason = "";
    static long LastAuthOk = 0;
    static int NetFailCount = 0;

    static void qW1eD9(string why)
    {
        if (DegradedMode) return;
        DegradedMode = true;
        DegradedReason = why;
        try { Log("进入降级模式：" + why); } catch { }
    }

    // 每次动作前的硬检查；不通过就抛，调用方 catch 后提示
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr h, uint code);

    static void TerminateSelf(uint code)
    {
        try { TerminateProcess(GetCurrentProcess(), code); } catch { }
        Environment.Exit((int)code);
    }

    static void tB9gL2(string action)
    {
#if NOLICENSE
        return;              // 免卡密构建：不校验授权态
#else
        if (!zQ4mR7("tB9gL2")) throw new Exception("授权异常（完整性校验失败）");
        if (DegradedMode) throw new Exception("授权异常（" + DegradedReason + "）");

        if (string.IsNullOrEmpty(LicCode) && LicType != "trial")
            throw new Exception("授权异常（无卡密状态）");

        // 令牌必须仍然有效（本地验签，不联网）
        Log("DIAG TokenPath=" + TokenPath() + " KamiCfg=" + KamiCfgPath + " TMP=" + Path.GetTempPath());
        string tok = LoadToken();
        Log("DIAG token长度=" + (tok == null ? -1 : tok.Length));
        string jc = "", jt = "", jer = ""; int jr = 0; long jex = 0, jxp = 0;
        bool ok = !string.IsNullOrEmpty(tok) &&
                  sY5hH8(tok, out jc, out jt, out jr, out jex, out jxp, out jer);
        if (!ok) { qW1eD9("令牌失效：" + jer); throw new Exception("授权异常（令牌失效）"); }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (jxp > 0 && now > jxp) { qW1eD9("令牌已过期"); throw new Exception("授权异常（已过期）"); }

        // 卡密必须与令牌一致（防改字段默认值）
        if (!string.IsNullOrEmpty(jc) && !string.IsNullOrEmpty(LicCode) &&
            !jc.Equals(LicCode, StringComparison.OrdinalIgnoreCase))
        { qW1eD9("卡密与令牌不匹配"); throw new Exception("授权异常（卡密不匹配）"); }

        LastAuthOk = now;
        Log("授权检查通过：" + action);
#endif
    }

    // 包一层，动作函数首行调用即可
    static bool vC6nP4(string action)
    {
        try { tB9gL2(action); return true; }
        catch (Exception ex)
        {
            Result("✗ " + ex.Message);
            Log("tB9gL2(" + action + ") 失败：" + ex.Message);
            return false;
        }
    }
    // =====================================================

    // ================= ACE 进程限速（v2.0.0）=================
    // 目标清单来自云端 ops 脚本：limit|targets|ACE-Guard Service,ACE-Tray.exe,...
    // v3.0.7：本地清单清空 —— 目标只能来自服务端签名下发的 ops
    // 没有 ops = 没有清单 = 动作拒绝执行（不再回落内置表）
#if NOLICENSE
    static readonly string[] AceLimitDefault = new string[] {
        "ACE-Guard Service", "ACE-Guard Client", "ACE-Guard Client.exe", "ACE-Tray.exe",
        "ACE-TrayV.exe", "ACE-Service64.exe", "ACE-BOOT.exe", "ACE-BOOT.sys",
        "SGuard64.exe", "SGuardSvc64.exe", "SGuardSvc32.exe", "SGuard64", "SGuardSvc32"
    };
#else
    static readonly string[] AceLimitDefault = new string[0];
#endif
    static bool AceLimited = false;

    static string[] dB5sX8()
    {
        if (OpsAceTargets != null && OpsAceTargets.Count > 0) return OpsAceTargets.ToArray();
        return AceLimitDefault;      // 空数组：调用方必须自行拒绝
    }

    // 进程清单同样只来自 ops（disable|kill|... 或 close|kill|...）
#if NOLICENSE
    static List<string> OpsKillTargets = new List<string>() { "ACE-Tray.exe", "ACE-TrayV.exe",
        "ACE-Service64.exe", "SGuard64.exe", "SGuardSvc64.exe", "SGuardSvc32.exe", "ACE-BOOT.exe" };
#else
    static List<string> OpsKillTargets = null;
#endif
#if NOLICENSE
    static string OpsSvcFilter = "AntiCheatExpert,ACE-|ACE_|ace-game|SGuard|TenioDL";
#else
    static string OpsSvcFilter = null;      // disable|scfilter|<regex>
#endif

    // 把目标名解析成进程：先按进程名，再按服务名（sc queryex 拿 PID）
    static List<Process> kE2tY4(out List<string> how)
    {
        how = new List<string>();
        List<Process> list = new List<Process>();
        HashSet<int> seen = new HashSet<int>();
        string[] targets = dB5sX8();

        foreach (string raw in targets)
        {
            string t = (raw ?? "").Trim();
            if (t.Length == 0) continue;

            // 1) 以 ^ 开头 = 正则，匹配所有进程名（ACE 改名也能命中）
            if (t.StartsWith("^"))
            {
                try
                {
                    Regex prx = new Regex(t, RegexOptions.IgnoreCase);
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            string pn = p.ProcessName ?? "";
                            if (prx.IsMatch(pn) && seen.Add(p.Id))
                            {
                                list.Add(p);
                                how.Add(pn + " (pid " + p.Id + ", 正则)");
                            }
                        }
                        catch { }
                        finally { try { if (!list.Contains(p)) p.Dispose(); } catch { } }
                    }
                }
                catch (Exception ex) { Log("进程正则无效：" + t + " " + ex.Message); }
                continue;
            }

            // 2) 按进程名（去掉 .exe 后比较）
            string bare = t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? t.Substring(0, t.Length - 4) : t;
            try
            {
                foreach (Process p in Process.GetProcessesByName(bare))
                {
                    if (seen.Add(p.Id)) { list.Add(p); how.Add(p.ProcessName + " (pid " + p.Id + ")"); }
                }
            }
            catch { }

            // 2) 按服务名查 PID（名字里带空格的，多为服务）
            try
            {
                string outp;
                RunCmd("sc", "queryex \"" + t + "\"", out outp);
                foreach (string line in outp.Split('\n'))
                {
                    string l = line.Trim();
                    if (!l.StartsWith("PID", StringComparison.OrdinalIgnoreCase)) continue;
                    int c = l.IndexOf(':');
                    if (c <= 0) continue;
                    int pid;
                    if (!int.TryParse(l.Substring(c + 1).Trim(), out pid)) continue;
                    if (pid <= 4 || !seen.Add(pid)) continue;
                    try
                    {
                        Process p = Process.GetProcessById(pid);
                        list.Add(p);
                        how.Add(t + " (服务, pid " + pid + ")");
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    static string LastLimitReport = "";

    // ===== 守护线程：ACE 若把优先级/亲和性改回来，每 5 秒重新压 =====
    static volatile bool AceGuardOn = false;
    static Thread AceGuardThread = null;

    static IntPtr LastCoreMask()
    {
        int cores = Environment.ProcessorCount;
        if (cores <= 0) cores = 1;
        if (cores >= 64) return (IntPtr)(-1L);
        return (IntPtr)(1L << (cores - 1));      // 只留最后一个逻辑核
    }

    static void StartAceGuard()
    {
        if (AceGuardOn) return;
        AceGuardOn = true;
        Thread t = new Thread(() =>
        {
            Log("守护线程已启动（每 5 秒复查一次）");
            while (AceGuardOn)
            {
                Thread.Sleep(5000);
                if (!AceGuardOn) break;
                try
                {
                    List<string> how;
                    List<Process> ps = kE2tY4(out how);
                    IntPtr mask = LastCoreMask();
                    int fixedN = 0;
                    foreach (Process p in ps)
                    {
                        try
                        {
                            bool need = false;
                            try { if (p.PriorityClass != ProcessPriorityClass.Idle) need = true; } catch { }
                            try { if (p.ProcessorAffinity != mask) need = true; } catch { }
                            if (need)
                            {
                                p.PriorityClass = ProcessPriorityClass.Idle;
                                p.ProcessorAffinity = mask;
                                fixedN++;
                            }
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                    if (fixedN > 0)
                    {
                        Log("守护：重新压制 " + fixedN + " 个 ACE 进程");
                        Result("守护中：已重新压制 " + fixedN + " 个进程（每 5 秒复查）");
                    }
                }
                catch { }
            }
            Log("守护线程已停止");
        });
        t.IsBackground = true;
        t.Name = "AceLimitGuard";
        AceGuardThread = t;
        t.Start();
    }

    static void StopAceGuard()
    {
        AceGuardOn = false;
        AceGuardThread = null;
    }

    // 需要管理员时：一键以管理员身份重启
    static bool RelaunchAsAdmin()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath);
            psi.UseShellExecute = true;
            psi.Verb = "runas";                // 触发 UAC
            psi.WorkingDirectory = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex) { Log("提权重启失败：" + ex.Message); return false; }
    }

    // 按钮入口：权限不够就先引导提权，不再让按钮变灰
    static bool EnsureAdminForAce(string what)
    {
        if (IsAdmin) return true;

        DialogResult r = MessageBox.Show(
            "「" + what + "」需要管理员权限。\r\n\r\n是否以管理员身份重新启动本程序？\r\n（重启后请再点一次）",
            "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (r == DialogResult.Yes)
        {
            if (RelaunchAsAdmin()) { try { Application.Exit(); } catch { } }
            else MessageBox.Show("提权失败，请右键本程序 → 以管理员身份运行。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return false;
    }

    // ================= 原生校验 aceguard.dll（v3.0.6 测试）=================
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr h);

    delegate int AgVerifyDel(string blob, string act, string deviceHash, long nowMs,
                             StringBuilder reason, int reasonCap);
    delegate int AgCheckILDel(IntPtr live, int n);

    static AgCheckILDel AgCheckIL = null;
    static AgCheckILDel AgFingerprint = null;

    static AgVerifyDel AgVerify = null;
    static string AgDllDir = null;
    static IntPtr AgDllHandle = IntPtr.Zero;
    static bool AgTried = false;

    static bool zB4oO6()
    {
        if (AgTried) return AgVerify != null;
        AgTried = true;
        try
        {
            // 随机目录 + 退出清理：不在磁盘上长期留明文 DLL
            string dir = Path.Combine(Path.GetTempPath(), "ag" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            string dll = Path.Combine(dir, "aceguard.dll");

            // 从自身资源释放（资源名以 aceguard.dll 结尾）
            System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
            string res = null;
            foreach (string n in asm.GetManifestResourceNames())
                if (n.EndsWith("aceguard.dll", StringComparison.OrdinalIgnoreCase)) { res = n; break; }
            if (res == null) { Log("原生校验：资源里没有 aceguard.dll"); return false; }

            using (Stream s = asm.GetManifestResourceStream(res))
            using (FileStream fs = new FileStream(dll, FileMode.Create, FileAccess.Write))
                s.CopyTo(fs);

            IntPtr h = LoadLibraryW(dll);
            if (h == IntPtr.Zero)
            {
                Log("原生校验：LoadLibrary 失败 " + Marshal.GetLastWin32Error());
                return false;
            }
            IntPtr fpc = GetProcAddress(h, "AG_CheckIL");
            if (fpc != IntPtr.Zero)
                AgCheckIL = (AgCheckILDel)Marshal.GetDelegateForFunctionPointer(fpc, typeof(AgCheckILDel));
            IntPtr fpf = GetProcAddress(h, "AG_Fingerprint");
            if (fpf != IntPtr.Zero)
                AgFingerprint = (AgCheckILDel)Marshal.GetDelegateForFunctionPointer(fpf, typeof(AgCheckILDel));
            IntPtr fp = GetProcAddress(h, "AG_VerifyTicket");
            if (fp == IntPtr.Zero) { Log("原生校验：找不到 AG_VerifyTicket"); return false; }
            AgVerify = (AgVerifyDel)Marshal.GetDelegateForFunctionPointer(fp, typeof(AgVerifyDel));
            AgDllDir = dir; AgDllHandle = h;
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (sx, ex) =>
                {
                    try { if (AgDllHandle != IntPtr.Zero) FreeLibrary(AgDllHandle); } catch { }
                    try { if (!string.IsNullOrEmpty(AgDllDir)) Directory.Delete(AgDllDir, true); } catch { }
                };
            }
            catch { }
            Log("原生校验已加载：aceguard.dll（临时目录 " + dir + "）");
            return true;
        }
        catch (Exception ex) { Log("原生校验初始化失败：" + ex.Message); return false; }
    }

    // 返回 1 通过 / 0 失败；native=false 表示回退托管
    static int aC9pP2(string blob, string act, string deviceHash, out string reason, out bool native)
    {
        reason = "";
        native = false;
        try
        {
            if (zB4oO6() && AgVerify != null)
            {
                native = true;
                StringBuilder sb = new StringBuilder(160);
                long now = ToUnixMs(DateTime.UtcNow);
                int r = AgVerify(blob, act, deviceHash, now, sb, sb.Capacity);
                reason = sb.ToString();
                return r;
            }
        }
        catch (Exception ex) { reason = "native-ex " + ex.Message; }
        return -1;
    }
    // =======================================================================

    // ================= 动作票据（v3.0.5）=================
    // 每个本地动作执行前，向服务端申请一张 RSA 签名票据：
    //   {k:"act", act, code, dh, nonce, exp}
    // 验签通过且 act / 设备 / nonce / 时效都对上，才允许执行。
    // 客户端把判断改成 return true 也没用 —— 没有私钥签不出票据。
    static readonly Dictionary<string, string> TktCache = new Dictionary<string, string>();
    static readonly Dictionary<string, long> TktCacheAt = new Dictionary<string, long>();

    // 精确取字符串字段（避免 "k":"act" 这种值撞名）
    static string JsonField(string json, string name)
    {
        try
        {
            Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    static bool jH4yS7(string blob, out string json)
    {
        json = "";
        try
        {
            int dot = blob.IndexOf('.');
            if (dot <= 0 || dot >= blob.Length - 1) return false;
            string p64 = blob.Substring(0, dot);
            byte[] sig = B64UrlDecode(blob.Substring(dot + 1));
            byte[] payload = B64UrlDecode(p64);
            if (sig == null || payload == null) return false;
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                RSAParameters p = new RSAParameters();
                p.Modulus = HexToBytes(TOK_N);
                p.Exponent = HexToBytes(TOK_E);
                rsa.ImportParameters(p);
                if (!rsa.VerifyData(Encoding.UTF8.GetBytes(p64), "SHA256", sig)) return false;
            }
            json = Encoding.UTF8.GetString(payload);
            return true;
        }
        catch { return false; }
    }

    static bool mR2wZ5(string act)
    {
#if NOLICENSE
        return true;         // 免卡密构建：不发动作票据
#else
        if (!zQ4mR7("ticket")) return false;

        // 60 秒缓存，避免连点反复请求
        string cached;
        long cat;
        if (TktCache.TryGetValue(act, out cached) && TktCacheAt.TryGetValue(act, out cat)
            && ToUnixMs(DateTime.UtcNow) - cat < 60000)
        {
            string cr; bool cn;
            int cres = aC9pP2(cached, act, DeviceHash32(), out cr, out cn);
            if (cres == 1 || (cres < 0 && fT8kQ1(cached, act))) return true;
        }

        if (string.IsNullOrEmpty(LicCode))
        {
            Result("✗ 该操作需要有效卡密");
            return false;
        }

        string nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
        string body = "{\"action\":\"ticket\",\"app\":\"ace\",\"act\":" + JsonStr(act)
                    + ",\"code\":" + JsonStr(LicCode)
                    + ",\"deviceId\":" + JsonStr(MachineId())
                    + ",\"nonce\":" + JsonStr(nonce) + "}";
        string err;
        string text = HttpPostJson(KamiUrl(), body, out err);
        if (text == null)
        {
            Result("✗ 授权服务不可用（" + err + "），该操作需要联网");
            Log("mR2wZ5(" + act + ") 网络失败：" + err);
            return false;
        }
        if (text.IndexOf("\"ok\":true") < 0)
        {
            string e2 = JsonField(text, "msg");
            if (string.IsNullOrEmpty(e2)) e2 = JsonField(text, "err");
            Result("✗ 该操作需要有效卡密：" + e2);
            Log("mR2wZ5(" + act + ") 被拒：" + text.Substring(0, Math.Min(200, text.Length)));
            return false;
        }
        string tk = JsonField(text, "ticket");
        if (string.IsNullOrEmpty(tk))
        {
            Result("✗ 动作票据为空");
            return false;
        }
        // 先算设备哈希，供原生比对
        string devHash;
        using (SHA256 sha = SHA256.Create())
            devHash = BitConverter.ToString(sha.ComputeHash(
                Encoding.UTF8.GetBytes(MachineId()))).Replace("-", "").ToLowerInvariant().Substring(0, 32);

        string nreason; bool nnative;
        int nres = aC9pP2(tk, act, devHash, out nreason, out nnative);
        bool okTicket;
        if (nres >= 0)
        {
            okTicket = (nres == 1);
            Log("票据校验走原生（" + (nnative ? "aceguard.dll" : "?") + "）：" +
                (okTicket ? "通过" : ("失败 " + nreason)));
        }
        else
        {
            okTicket = fT8kQ1(tk, act);
            Log("票据校验走托管（原生不可用 " + nreason + "）：" + (okTicket ? "通过" : "失败"));
        }
        if (!okTicket)
        {
            Result("✗ 动作票据校验失败");
            Log("mR2wZ5(" + act + ") 票据验证失败");
            return false;
        }
        TktCache[act] = tk;
        TktCacheAt[act] = ToUnixMs(DateTime.UtcNow);
        Log("动作票据通过：" + act);
        return true;
#endif
    }

    static string DeviceHash32()
    {
        try
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(MachineId()))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
        }
        catch { return ""; }
    }

    static bool fT8kQ1(string blob, string act)
    {
        string json;
        if (!jH4yS7(blob, out json)) { Log("票据: 验签失败"); return false; }
        Log("票据 payload=" + json);
        try
        {
            if (json.IndexOf("\"k\":\"act\"") < 0) { Log("票据: k 不对"); return false; }
            string jact = JsonField(json, "act");
            if (!string.Equals(jact, act, StringComparison.OrdinalIgnoreCase)) { Log("票据: act 不符 " + jact + " vs " + act); return false; }
            string jdh = JsonField(json, "dh");
            // 设备哈希 = MachineId 的 sha256 前 32 位（与服务端一致）
            string mine;
            using (SHA256 sha = SHA256.Create())
                mine = BitConverter.ToString(sha.ComputeHash(
                    Encoding.UTF8.GetBytes(MachineId()))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
            if (!string.IsNullOrEmpty(jdh) && !jdh.Equals(mine, StringComparison.OrdinalIgnoreCase)) { Log("票据: 设备不符 " + jdh + " vs " + mine); return false; }
            Match m = Regex.Match(json, "\"exp\"\\s*:\\s*(\\d+)");
            if (!m.Success) return false;
            long exp = long.Parse(m.Groups[1].Value);
            if (ToUnixMs(DateTime.UtcNow) > exp + 120000) { Log("票据: 已过期"); return false; }
            return true;
        }
        catch { return false; }
    }
    // =====================================================

    // v3.0.2：本地动作也要凭据 —— 必须能从服务端拿到有效 gate（需要有效卡密）
    static bool bN7uA3(string what)
    {
        if (!zQ4mR7("bN7uA3")) return false;
        if (DegradedMode) { Result("✗ 授权异常（" + DegradedReason + "）"); return false; }
        if (GateFresh()) return true;
        string err;
        if (tU1iI3(out err)) return true;
        Result("✗ " + what + " 需要有效卡密：启动许可申请失败（" +
               (string.IsNullOrEmpty(err) ? "缺少卡密" : err) + "）");
        Log("bN7uA3(" + what + ") 失败：" + err);
        return false;
    }

    // v3.0.7：动作执行前确保 ops 已加载（CLI 路径不会在登录时加载）
    static void cK5iE8()
    {
        try
        {
            if (OpsAceTargets != null && OpsAceTargets.Count > 0) return;
            string e;
            if (nA8wB5(out e)) oX3cC1();
            else Log("cK5iE8：取脚本失败 " + e);
        }
        catch (Exception ex) { Log("cK5iE8 异常：" + ex.Message); }
    }

    static void aF4hT9()
    {
        if (!kW7bT3()) { qW1eD9("inline@" + "limit"); TerminateSelf(9); return; }
        if (!mR2wZ5("limit")) return;
        if (!zQ4mR7("limitAce")) return;
        if (!vC6nP4("limitAce")) return;
        if (!IsAdmin && !CliMode)
        {
            if (!EnsureAdminForAce("限制 ACE")) return;
        }
        cK5iE8();
        if (dB5sX8().Length == 0)
        {
            Result("✗ 目标清单未下发（请检查网络后重新登录）");
            Log("限制 ACE：AceLimitTargets 为空，拒绝执行");
            return;
        }
        Result("正在扫描 ACE 相关进程…");
        List<string> how;
        List<Process> ps = kE2tY4(out how);
        if (ps.Count == 0)
        {
            Result("没有找到 ACE 相关进程（清单：" + string.Join(", ", dB5sX8()) + "）");
            return;
        }

        int cores = Environment.ProcessorCount;
        IntPtr lastMask = (IntPtr)(1L << (cores - 1));     // 只留最后一个逻辑核
        int ok = 0, fail = 0;
        List<string> fails = new List<string>();

        foreach (Process p in ps)
        {
            try
            {
                p.PriorityClass = ProcessPriorityClass.Idle;      // 最低优先级
                p.ProcessorAffinity = lastMask;                    // 只跑最后一个核
                ok++;
                Result("✓ " + p.ProcessName + " (pid " + p.Id + ") → 低优先级 + 第 " + cores + " 核");
            }
            catch (Exception ex)
            {
                fail++;
                fails.Add(p.ProcessName + ": " + ex.Message);
                Log("限制失败 " + p.ProcessName + " pid=" + p.Id + " → " + ex.Message);
            }
            finally { try { p.Dispose(); } catch { } }
        }

        AceLimited = ok > 0;
        LastLimitReport = "成功 " + ok + "，失败 " + fail;
        if (ok > 0)
        {
            StartAceGuard();
            Result("限制完成：" + LastLimitReport + "（已开启守护，每 5 秒复查一次）");
        }
        else
        {
            Result("限制失败：全部被拒（多半是受保护进程，需要驱动权限）");
        }
        if (fail > 0 && ok > 0) Result("（有 " + fail + " 个受保护进程无法修改，属正常）");
        foreach (string f in fails) Result("  · " + f);
    }

    static void eG7jU2()
    {
        if (!kW7bT3()) { qW1eD9("inline@" + "restore"); TerminateSelf(9); return; }
        if (!mR2wZ5("restore")) return;
        if (!zQ4mR7("restoreAce")) return;
        if (!vC6nP4("restoreAce")) return;
        if (!IsAdmin && !CliMode)
        {
            if (!EnsureAdminForAce("恢复 ACE")) return;
        }
        StopAceGuard();
        cK5iE8();
        if (dB5sX8().Length == 0)
        {
            Result("✗ 目标清单未下发（请检查网络后重新登录）");
            Log("恢复 ACE：目标清单为空，拒绝执行");
            return;
        }
        Result("正在恢复 ACE 相关进程…");
        List<string> how;
        List<Process> ps = kE2tY4(out how);
        if (ps.Count == 0)
        {
            Result("没有找到 ACE 相关进程");
            return;
        }

        int cores = Environment.ProcessorCount;
        long allMaskL = (cores >= 64) ? -1L : ((1L << cores) - 1);
        IntPtr allMask = (IntPtr)allMaskL;
        int ok = 0, fail = 0;
        foreach (Process p in ps)
        {
            try
            {
                p.PriorityClass = ProcessPriorityClass.Normal;
                p.ProcessorAffinity = allMask;
                ok++;
                Result("✓ " + p.ProcessName + " (pid " + p.Id + ") → 正常优先级 + 全部 " + cores + " 核");
            }
            catch (Exception ex)
            {
                fail++;
                Log("恢复失败 " + p.ProcessName + " → " + ex.Message);
            }
            finally { try { p.Dispose(); } catch { } }
        }
        AceLimited = false;
        Result("恢复完成：成功 " + ok + "，失败 " + fail + "（守护已停止）");
    }

    // 免责声明：文本写死在 exe 里，不走云端
    static bool AskLimitDisclaimer()
    {
        string text =
            "【免责声明】\r\n\r\n" +
            "1. 本功能仅为软件学习与交流使用。\r\n\r\n" +
            "2. 本功能仅降低指定进程的优先级与处理器亲和性，不修改、不删除、不注入任何程序。\r\n\r\n" +
            "3. 使用本功能可能违反相关游戏的用户协议，由此造成的账号封禁、数据异常、\r\n" +
            "   比赛判罚或其他任何后果，均由使用者自行承担，作者不承担任何责任。\r\n\r\n" +
            "4. 点击「同意」表示你已阅读并接受上述条款。\r\n\r\n" +
            "是否同意并继续？";

        using (Form dlg = new Form())
        {
            dlg.Text = "使用前请阅读并同意";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ClientSize = new Size(520, 340);
            dlg.Font = new Font("微软雅黑", 9.5f);

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.BackColor = Color.White;
            tb.Text = text;
            tb.SetBounds(14, 14, 492, 254);
            dlg.Controls.Add(tb);

            Button okb = new Button();
            okb.Text = "同意并启用";
            okb.SetBounds(300, 282, 110, 34);
            okb.DialogResult = DialogResult.OK;
            dlg.Controls.Add(okb);

            Button nob = new Button();
            nob.Text = "不同意";
            nob.SetBounds(416, 282, 90, 34);
            nob.DialogResult = DialogResult.Cancel;
            dlg.AcceptButton = okb;
            dlg.CancelButton = nob;
            dlg.Controls.Add(nob);

            return dlg.ShowDialog(UI) == DialogResult.OK;
        }
    }
    // =========================================================

    static void InstallAce()
    {
        if (!zQ4mR7("install")) return;
        if (!vC6nP4("install")) return;
        if (pD7dE9("install")) Result("✓ 安装流程已执行（详见日志）");
        return;
    }

    static void InstallAceOld()
    {
        Log("== 安装 ACE ==");
        string setup = FindAceSetup();
        if (setup == null)
        {
            Result("没自动找到安装器，请在弹窗里选择游戏目录中的 ACE-Setup64.exe（例如 C:\\Arknights Endfield\\AntiCheatExpert\\）。");
            setup = AskSetupPath();
            if (string.IsNullOrEmpty(setup))
            {
                Result("✗ 已取消：没有指定 ACE-Setup64.exe，无法安装。");
                return;
            }
        }

        string dir = Path.GetDirectoryName(setup);
        Log("使用安装器：" + setup);

        // 安装前先看游戏有没有在跑（文件被占用会装不上）
        if (string.IsNullOrEmpty(GameDir)) DiscoverAceSourceDir();
        if (!ConfirmGameClosed("安装")) return;

        string[] variants = new string[] { "install", "-install", "/install" };
        if (DryMode)
        {
            foreach (string v in variants) Log("[dry] " + setup + " " + v);
            Result("（模拟）会依次尝试：ACE-Setup64.exe install / -install / /install");
            return;
        }

        int rc = -1;
        string usedArgs = null;
        foreach (string v in variants)
        {
            Result("正在安装（ACE-Setup64.exe " + v + "）……");
            string output;
            rc = RunCmdIn(dir, setup, v, out output);
            Log("参数 " + v + " 退出码 " + rc + " " + (output ?? ""));
            Thread.Sleep(1500);
            if (FindAceDir() != null || cR8sT5().Count > 0) { usedArgs = v; break; }
        }
        if (usedArgs != null) Log("安装参数命中：" + usedArgs);

        // 装完之后：释放托盘/预启动组件（原 exe 内嵌的那 4 个文件），再启用预启动模式
        string aceDir = FindAceDir();
        if (aceDir != null)
        {
            ExtractPayload(aceDir);
            bool trayOk = RunTrayStart(aceDir);
            Thread.Sleep(1200);
            RefreshStatus();

            var svcs2 = cR8sT5();
            int running2 = 0;
            foreach (string[] s in svcs2) if (ServiceState(s[0]) == "运行中") running2++;
            Result(string.Format("✓ 安装完成：{0}；服务 {1} 个（{2} 运行中）；预启动模式{3}。",
                aceDir, svcs2.Count, running2, trayOk ? "已启用（右下角会提示）" : "未启用（缺 ACE-Tray.exe）"));
            return;
        }

        Thread.Sleep(1200);
        RefreshStatus();

        var svcs = cR8sT5();
        int running = 0;
        foreach (string[] s in svcs) if (ServiceState(s[0]) == "运行中") running++;

        if (FindAceDir() == null && svcs.Count == 0)
            Result("✗ 安装似乎没成功（退出码 " + rc + "）。请确认用管理员运行、并先关闭游戏后重试。");
        else
            Result(string.Format("✓ 安装完成：目录 {0}，服务 {1} 个（{2} 个运行中）。",
                FindAceDir() ?? "未创建", svcs.Count, running));
    }

    // ===== 资源下发：卡密/试用 → 服务器签发临时链接 → 下载 → sha256 校验 =====
    static string Sha256File(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
    }

    static bool HashMatch(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        int n = Math.Min(a.Length, b.Length);
        if (n < 16) return false;
        return string.Compare(a, 0, b, 0, n, StringComparison.OrdinalIgnoreCase) == 0;
    }

    static string ResName()
    {
        string env = Environment.GetEnvironmentVariable("ACE_RES_NAME");
        if (!string.IsNullOrEmpty(env)) return env;
        var cfg = ReadKv(KamiCfgPath);
        return cfg.ContainsKey("res") && cfg["res"].Length > 0 ? cfg["res"] : "ace-payload";
    }

    static string PayloadPath()
    {
        string dir = Path.GetDirectoryName(KamiCfgPath);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "res_" + ResName() + ".zip");
    }

    // 向 kamiVerify 换签名链接
    static string LastAesKey = "";
    static string LastAesIv = "";

    // ===== 小工具：POST JSON + 取字段 =====
    static string HttpPostJson(string url, string body, out string err)
    {
        err = "";
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.ServicePoint.Expect100Continue = false;
            req.KeepAlive = false;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            byte[] bb = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bb.Length;
            using (Stream st = req.GetRequestStream()) st.Write(bb, 0, bb.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    static string ExtractField(string json, string name)
    {
        try
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "";
            i = json.IndexOf(':', i + key.Length);
            if (i < 0) return "";
            int q1 = json.IndexOf('"', i + 1);
            if (q1 < 0) return "";
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return "";
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }
        catch { return ""; }
    }

    // ===== 启动门禁（服务端签发短期 gate，功能脚本必须带它才换得到链接）=====
    static string GateBlob = "";
    static byte[] InlineBody = null;
    static long GateAt = 0;

    static bool tU1iI3(out string err)
    {
        err = "";
        try
        {
            string code = (LicType == "trial") ? "" : (LicCode ?? "");
            string body = "{\"action\":\"start\",\"app\":\"ace\",\"code\":" + JsonStr(code)
                        + ",\"deviceId\":" + JsonStr(MachineId()) + "}";
            string text = HttpPostJson(KamiUrl(), body, out err);
            if (text == null) return false;
            if (text.IndexOf("\"ok\":true") < 0)
            {
                err = ExtractField(text, "msg");
                if (string.IsNullOrEmpty(err)) err = "服务端拒绝签发启动许可";
                return false;
            }
            string gate = ExtractField(text, "gate");
            if (string.IsNullOrEmpty(gate)) { err = "服务端没返回 gate"; return false; }
            GateBlob = gate;
            GateAt = ToUnixMs(DateTime.UtcNow);
            Log("已取得启动许可（gate，120 秒有效）");
            return true;
        }
        catch (Exception ex) { err = ex.Message; return false; }
    }

    static bool GateFresh()
    {
        return !string.IsNullOrEmpty(GateBlob) &&
               (ToUnixMs(DateTime.UtcNow) - GateAt) < 90000;
    }

    static long ToUnixMs(DateTime t)
    {
        return (long)(t - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    }

    // ===== 功能脚本响应签名 =====
    static string OpsSigMessage(string name, string sha256, string size, string aesKey, string aesIv, string exp)
    {
        return "ops1|ace|" + name + "|" + sha256 + "|" + size + "|" + aesKey.ToLowerInvariant() + "|" + aesIv.ToLowerInvariant() + "|" + exp;
    }

    static bool xZ3mM5(string message, string sigB64)
    {
        try
        {
            byte[] sig = B64UrlDecode(sigB64);
            if (sig == null) sig = Convert.FromBase64String(sigB64);
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                RSAParameters p = new RSAParameters();
                p.Modulus = HexToBytes(TOK_N);
                p.Exponent = HexToBytes(TOK_E);
                rsa.ImportParameters(p);
                return rsa.VerifyData(Encoding.UTF8.GetBytes(message), "SHA256", sig);
            }
        }
        catch (Exception ex) { Log("验签异常：" + ex.Message); return false; }
    }

    static bool yA7nN1(string expS)
    {
        long exp;
        if (!long.TryParse(expS, out exp) || exp <= 0) return false;
        long now = ToUnixMs(DateTime.UtcNow);
        return now < exp + 120000;      // 允许 2 分钟时钟偏差
    }

    static bool uV6jJ7(string resName, out string url, out string rsha, out long rsize, out string rver, out string err)
    {
        url = ""; rsha = ""; rsize = 0; rver = ""; err = "";
        string code = (LicType == "trial") ? "" : (LicCode ?? "");
        // 功能脚本必须带 gate；没有就现取一次
        if (resName.EndsWith("-ops", StringComparison.OrdinalIgnoreCase) && !GateFresh())
        {
            string gerr;
            if (!tU1iI3(out gerr)) { err = "启动许可申请失败：" + gerr; return false; }
        }
        string body = "{\"action\":\"resource\",\"name\":" + JsonStr(resName)
                    + ",\"code\":" + JsonStr(code)
                    + ",\"deviceId\":" + JsonStr(MachineId())
                    + ",\"v\":116"
                    + ",\"gate\":" + JsonStr(GateBlob)
                    + ",\"maxAge\":600}";
        string text;
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); } catch { }
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(KamiUrl());
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log("资源接口异常：" + ex.Message);
            err = MapKamiErr("NETWORK:" + ex.Message);
            return false;
        }
        Log("资源接口返回：" + (text.Length > 300 ? text.Substring(0, 300) : text));

        if (!Regex.IsMatch(text, "\"ok\"\\s*:\\s*true"))
        {
            Match m = Regex.Match(text, "\"err\"\\s*:\\s*\"([^\"]*)\"");
            string e = m.Success ? m.Groups[1].Value : "";
            err = e == "RES_NOT_FOUND" ? ("服务端还没上架资源【" + resName + "】：管理员要先用 resPut 上传（或把 ace-ops.txt 放在 exe 同目录临时用）")
                : (e == "TRIAL_REQUIRED" ? "需要有效卡密，或先领取试用"
                : (e == "RES_SIGN_FAILED" ? "链接签发失败，请稍后重试" : MapKamiErr(e)));
            return false;
        }

        InlineBody = null;
        string b64body = "";
        try
        {
            Match mb = Regex.Match(text, "\"body64\"\\s*:\\s*\"([^\"]+)\"");
            if (mb.Success) b64body = mb.Groups[1].Value;
        }
        catch { }
        if (b64body.Length > 0)
        {
            try { InlineBody = Convert.FromBase64String(b64body); }
            catch { InlineBody = null; }
            LastAesKey = ExtractField(text, "aesKey");
            LastAesIv = ExtractField(text, "aesIv");
            Match mh0 = Regex.Match(text, "\"sha256\"\\s*:\\s*\"([^\"]*)\"");
            Match ms0 = Regex.Match(text, "\"size\"\\s*:\\s*(\\d+)");
            Match mv0 = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]*)\"");
            rsha = mh0.Success ? mh0.Groups[1].Value : "";
            rsize = ms0.Success ? long.Parse(ms0.Groups[1].Value) : 0;
            rver = mv0.Success ? mv0.Groups[1].Value : "";
            url = "";
            GateAt = GateAt;   // 保持不变
            return true;
        }

        Match mu = Regex.Match(text, "\"url\"\\s*:\\s*\"([^\"]+)\"");
        Match mh = Regex.Match(text, "\"sha256\"\\s*:\\s*\"([^\"]*)\"");
        Match ms = Regex.Match(text, "\"size\"\\s*:\\s*(\\d+)");
        Match mv = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]*)\"");
        Match mk = Regex.Match(text, "\"aesKey\"\\s*:\\s*\"([0-9A-Fa-f]*)\"");
        Match mi = Regex.Match(text, "\"aesIv\"\\s*:\\s*\"([0-9A-Fa-f]*)\"");
        LastAesKey = mk.Success ? mk.Groups[1].Value : "";
        LastAesIv = mi.Success ? mi.Groups[1].Value : "";

        // ★ 功能脚本：服务端对关键字段做 RSA 签名，客户端用内置公钥验签
        if (resName.EndsWith("-ops", StringComparison.OrdinalIgnoreCase))
        {
            string sig = ExtractField(text, "sig");
            string expS = Regex.Match(text, "\"exp\"\\s*:\\s*(\\d+)").Success
                        ? Regex.Match(text, "\"exp\"\\s*:\\s*(\\d+)").Groups[1].Value : "0";

            if (sig.Length == 0) { err = "服务端未对功能脚本响应签名"; return false; }
            string msg = OpsSigMessage(resName, mh.Success ? mh.Groups[1].Value : "",
                                       ms.Success ? ms.Groups[1].Value : "0",
                                       LastAesKey, LastAesIv, expS);
            if (!xZ3mM5(msg, sig))
            {
                err = "功能脚本响应签名无效（可能被篡改）";
                return false;
            }
            if (!yA7nN1(expS)) { err = "功能脚本响应已过期，请重新登录"; return false; }
        }
        if (!mu.Success) { err = "服务器没返回下载地址"; return false; }
        url = mu.Groups[1].Value.Replace("\\/", "/");
        rsha = mh.Success ? mh.Groups[1].Value : "";
        rsize = ms.Success ? long.Parse(ms.Groups[1].Value) : 0;
        rver = mv.Success ? mv.Groups[1].Value : "";
        return true;
    }

    static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
        byte[] b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    // AES-256-CTR 解密流（边读边解，写盘+算 sha256 一次完成）；keyHex 为空则按明文处理
    // ===== 内存下载：解密结果只留在 byte[]，全程不落盘 =====
    // 内联密文的 AES-256-CTR 解密（与服务端 createCipheriv('aes-256-ctr') 对应）
    static byte[] wX8lL9(byte[] data, string keyHex, string ivHex)
    {
        try
        {
            byte[] key = HexToBytes(keyHex);
            byte[] iv = HexToBytes(ivHex);
            if (key == null || key.Length != 32 || iv == null || iv.Length != 16) return null;
            using (Aes aes = Aes.Create())
            {
                aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                {
                    byte[] ctr = (byte[])iv.Clone();
                    byte[] ks = new byte[16];
                    byte[] o = new byte[data.Length];
                    int pos = 16;
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (pos >= 16)
                        {
                            enc.TransformBlock(ctr, 0, 16, ks, 0);
                            for (int b = 15; b >= 0; b--) { ctr[b]++; if (ctr[b] != 0) break; }
                            pos = 0;
                        }
                        o[i] = (byte)(data[i] ^ ks[pos++]);
                    }
                    return o;
                }
            }
        }
        catch { return null; }
    }

    static bool vW2kK4(string url, long expectSize, string keyHex, string ivHex,
                                 out byte[] body, out string plainSha256, out string err)
    {
        body = null; plainSha256 = ""; err = "";
        byte[] key = HexToBytes(keyHex);
        byte[] iv = HexToBytes(ivHex);
        bool encrypted = key != null && key.Length == 32 && iv != null && iv.Length == 16;
        if (!string.IsNullOrEmpty(keyHex) && !encrypted)
        {
            err = "服务器下发的密钥格式不对";
            return false;
        }

        Aes aes = null; ICryptoTransform enc = null; byte[] ctr = null, ks = null; int ksPos = 16;
        if (encrypted)
        {
            aes = Aes.Create();
            aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            enc = aes.CreateEncryptor();
            ctr = (byte[])iv.Clone();
            ks = new byte[16];
        }

        SHA256 sha = SHA256.Create();
        MemoryStream ms = new MemoryStream(expectSize > 0 ? (int)Math.Min(expectSize, 64L * 1024 * 1024) : 65536);
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 60000; req.ReadWriteTimeout = 60000;
            req.UserAgent = "ACE-Toolbox/1.0";
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            {
                byte[] buf = new byte[1 << 16];
                long total = 0; int n, lastPct = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (encrypted)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (ksPos >= 16)
                            {
                                enc.TransformBlock(ctr, 0, 16, ks, 0);
                                for (int b = 15; b >= 0; b--) { ctr[b]++; if (ctr[b] != 0) break; }
                                ksPos = 0;
                            }
                            buf[i] ^= ks[ksPos++];
                        }
                    }
                    ms.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    total += n;
                    if (expectSize > 0)
                    {
                        int pct = (int)(total * 100 / expectSize);
                        if (pct / 20 != lastPct / 20)
                        {
                            lastPct = pct;
                            Result((encrypted ? "下载并解密 " : "下载 ") + pct + "%（" + (total / 1024) + " / " + (expectSize / 1024) + " KB）");
                        }
                    }
                }
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            plainSha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            body = ms.ToArray();
            Log("内存下载完成 " + body.Length + " 字节" + (encrypted ? "（已解密，未落盘）" : ""));
            return true;
        }
        catch (Exception ex)
        {
            Log("内存下载失败：" + ex.Message);
            err = "下载失败：" + ex.Message;
            return false;
        }
        finally
        {
            try { ms.Dispose(); } catch { }
            try { if (enc != null) enc.Dispose(); } catch { }
            try { if (aes != null) aes.Dispose(); } catch { }
            try { sha.Dispose(); } catch { }
            if (ks != null) Array.Clear(ks, 0, ks.Length);
            if (ctr != null) Array.Clear(ctr, 0, ctr.Length);
        }
    }

    static bool DownloadFile(string url, string target, long expectSize, string keyHex, string ivHex,
                             out string plainSha256, out string err)
    {
        plainSha256 = "";
        byte[] key = HexToBytes(keyHex);
        byte[] iv = HexToBytes(ivHex);
        bool encrypted = key != null && key.Length == 32 && iv != null && iv.Length == 16;
        if (!string.IsNullOrEmpty(keyHex) && !encrypted)
        {
            err = "服务器下发的密钥格式不对（需要 aesKey 64 位 hex、aesIv 32 位 hex）";
            return false;
        }

        err = "";
        Aes aes = null;
        ICryptoTransform enc = null;
        byte[] ctr = null, ks = null;
        int ksPos = 16;
        if (encrypted)
        {
            aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            enc = aes.CreateEncryptor();
            ctr = (byte[])iv.Clone();
            ks = new byte[16];
        }

        SHA256 sha = SHA256.Create();
        string part = target + ".part";
        try
        {
            if (File.Exists(part)) File.Delete(part);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "ACE-Toolbox/1.0";
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[1 << 16];
                long total = 0;
                int n, lastPct = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (encrypted)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (ksPos >= 16)
                            {
                                enc.TransformBlock(ctr, 0, 16, ks, 0);
                                for (int b = 15; b >= 0; b--) { ctr[b]++; if (ctr[b] != 0) break; }
                                ksPos = 0;
                            }
                            buf[i] ^= ks[ksPos++];
                        }
                    }
                    fs.Write(buf, 0, n);
                    sha.TransformBlock(buf, 0, n, null, 0);
                    total += n;
                    if (expectSize > 0)
                    {
                        int pct = (int)(total * 100 / expectSize);
                        if (pct / 20 != lastPct / 20)
                        {
                            lastPct = pct;
                            Result((encrypted ? "下载并解密 " : "下载资源包 ") + pct + "%（" + (total / 1024) + " / " + (expectSize / 1024) + " KB）");
                        }
                    }
                }
                Log("下载完成 " + total + " 字节" + (encrypted ? "（已解密）" : ""));
            }
            sha.TransformFinalBlock(new byte[0], 0, 0);
            plainSha256 = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();

            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            return true;
        }
        catch (Exception ex)
        {
            Log("下载失败：" + ex.Message);
            err = "下载失败：" + ex.Message;
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            return false;
        }
        finally
        {
            try { if (enc != null) enc.Dispose(); } catch { }
            try { if (aes != null) aes.Dispose(); } catch { }
            try { sha.Dispose(); } catch { }
        }
    }

    static bool DownloadFileOld(string url, string target, long expectSize, out string err)
    {
        err = "";
        string part = target + ".part";
        try
        {
            if (File.Exists(part)) File.Delete(part);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "ACE-Toolbox/1.0";
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream src = resp.GetResponseStream())
            using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[1 << 16];
                long total = 0;
                int n, lastPct = -1;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n);
                    total += n;
                    if (expectSize > 0)
                    {
                        int pct = (int)(total * 100 / expectSize);
                        if (pct / 20 != lastPct / 20)
                        {
                            lastPct = pct;
                            Result("下载资源包 " + pct + "%（" + (total / 1048576) + " / " + (expectSize / 1048576) + " MB）");
                        }
                    }
                }
                Log("下载完成 " + total + " 字节");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            return true;
        }
        catch (Exception ex)
        {
            Log("下载失败：" + ex.Message);
            err = "下载失败：" + ex.Message;
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            return false;
        }
    }

    // ===== 功能脚本：向服务器换签名链接 → 下载 → 校验 =====
    static string OpsName()
    {
#if DEBUG
        string env = Environment.GetEnvironmentVariable("ACE_OPS_NAME");
        if (!string.IsNullOrEmpty(env)) return env;
#endif
        var cfg = ReadKv(KamiCfgPath);
        return cfg.ContainsKey("ops") && cfg["ops"].Length > 0 ? cfg["ops"] : "ace-ops";
    }

    // ===== 脚本缓存加密：AES-256-CTR，密钥由授权令牌派生（没令牌解不开）=====
    static byte[] CacheKey()
    {
        try
        {
            string tok = LoadToken();
            if (string.IsNullOrEmpty(tok)) return null;
            string jc = "", jt = "", jer = ""; int jr = 0; long jex = 0, jxp = 0;
            if (!sY5hH8(tok, out jc, out jt, out jr, out jex, out jxp, out jer)) return null;
            // 用「卡密 + 机器码」派生（稳定），令牌只当准入条件
            if (string.IsNullOrEmpty(jc)) return null;
            using (SHA256 sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes("ops|" + jc + "|" + MachineId()));
        }
        catch { return null; }
    }

    static byte[] CacheIv(byte[] key)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(Concat(Encoding.UTF8.GetBytes("iv|"), key));
            byte[] iv = new byte[16];
            Array.Copy(h, iv, 16);
            return iv;
        }
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    // CTR 是对称的：加密=解密
    static byte[] AesCtr(byte[] data, byte[] key, byte[] iv)
    {
        using (Aes aes = Aes.Create())
        {
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using (ICryptoTransform enc = aes.CreateEncryptor())
            {
                byte[] ctr = (byte[])iv.Clone();
                byte[] ks = new byte[16];
                int ksPos = 16;
                byte[] outBuf = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    if (ksPos >= 16)
                    {
                        enc.TransformBlock(ctr, 0, 16, ks, 0);
                        for (int b = 15; b >= 0; b--) { ctr[b]++; if (ctr[b] != 0) break; }
                        ksPos = 0;
                    }
                    outBuf[i] = (byte)(data[i] ^ ks[ksPos++]);
                }
                return outBuf;
            }
        }
    }

    // 缓存文件：ops_<name>.bin（密文）
    /// <summary>删不掉就清零 —— 旧版本可能是管理员写的，普通权限删不了</summary>
    static void WipeFile(string p)
    {
        try
        {
            if (!File.Exists(p)) return;
            try { File.SetAttributes(p, FileAttributes.Normal); } catch { }
            try { File.Delete(p); } catch { }
            if (File.Exists(p))
            {
                try { File.WriteAllBytes(p, new byte[0]); } catch { }
            }
        }
        catch { }
    }

    static string OpsCachePath()
    {
        string p = OpsPath();
        return p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? p.Substring(0, p.Length - 4) + ".bin" : p + ".bin";
    }

    static string OpsPath()
    {
        string dir = Path.GetDirectoryName(KamiCfgPath);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "ops_" + OpsName() + ".txt");
    }

    static bool nA8wB5(out string err)
    {
        err = "";
#if OFFLINE
        // ===== 离线版：功能脚本内嵌在 exe 里，永远不联网拉 =====
        try
        {
            System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
            string res = null;
            foreach (string n in asm.GetManifestResourceNames())
                if (n.ToLowerInvariant().EndsWith("ace-ops.txt")) { res = n; break; }
            if (res == null) { err = "离线版没有内嵌功能脚本"; return false; }
            // 只读进内存，不落盘（配置目录不可写也能用）
            using (Stream s = asm.GetManifestResourceStream(res))
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                OpsText = sr.ReadToEnd();
            Log("离线版：使用内嵌功能脚本（" + OpsText.Length + " 字符，仅内存）");
            return true;
        }
        catch (Exception ex)
        {
            err = "读取内嵌功能脚本失败：" + ex.Message;
            return false;
        }
#else
        string name = OpsName();
        string cache = Path.Combine(Path.GetTempPath(), "aceops_" + Guid.NewGuid().ToString("N") + ".tmp");
        var cfg = ReadKv(KamiCfgPath);

        // 1) 先看内存；再看磁盘缓存
        if (OpsFetchedThisSession)
        {
            Log("本次运行已取过脚本，复用内存里的");
            return true;
        }

        // 1.1) v1.1.3：脚本永不落盘 —— 启动即清掉历史缓存，只从云端现拉
        try
        {
            string[] stale = new string[] { OpsPath(), OpsCachePath() };
            int n = 0;
            foreach (string f in stale)
            {
                bool had = File.Exists(f);
                WipeFile(f);
                if (had && !File.Exists(f)) n++;
            }
            if (n > 0) Log("已清掉本地脚本缓存 " + n + " 个（v1.1.3 起不再落盘）");
        }
        catch (Exception ex) { Log("清理脚本缓存异常：" + ex.Message); }

#if DEBUG
        // 2) 开发/离线兜底：exe 同目录的 ace-ops.txt
        string[] local = new string[] {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ace-ops.txt"),
            Path.Combine(Environment.CurrentDirectory, "ace-ops.txt")
        };
        foreach (string c in local)
        {
            if (!File.Exists(c)) continue;
            Log("使用本机随附的功能脚本：" + c);
            try { File.Copy(c, cache, true); } catch { }
            if (!File.Exists(cache)) { cfg["opsSha"] = Sha256File(c); WriteKv(KamiCfgPath, cfg); return true; }
            cfg["opsSha"] = Sha256File(cache);
            cfg["ops"] = name;
            WriteKv(KamiCfgPath, cfg);
            OpsFetchedThisSession = true;
            return true;
        }

#endif

        // 3) 向服务器换链接
        Result("正在申请功能脚本…");
        string url, rsha, rver;
        long rsize;
        if (!uV6jJ7(name, out url, out rsha, out rsize, out rver, out err))
        {
            Result("✗ 申请功能脚本失败：" + err);
            return false;
        }
        // ★ v1.2.3：全程内存，绝不在磁盘留下明文
        // ★ 3.1.0：服务端按设备现场加密 → 直接内联下发，不再下载
        byte[] bodyBytes;
        string plainSha;
        if (InlineBody != null)
        {
            byte[] dec = wX8lL9(InlineBody, LastAesKey, LastAesIv);
            if (dec == null) { Result("✗ 功能脚本解密失败"); return false; }
            bodyBytes = dec;
            using (SHA256 sha = SHA256.Create())
                plainSha = BitConverter.ToString(sha.ComputeHash(dec)).Replace("-", "").ToLowerInvariant();
            Log("功能脚本内联下发（" + dec.Length + " 字节，设备密钥解密）");
            InlineBody = null;
        }
        else if (!vW2kK4(url, rsize, LastAesKey, LastAesIv, out bodyBytes, out plainSha, out err))
        {
            Result("✗ " + err);
            return false;
        }
        string h = plainSha;
        if (!string.IsNullOrEmpty(LastAesKey)) Log("已用 AES-256-CTR 解密，明文 sha256=" + h);
        if (rsha.Length >= 16 && !HashMatch(h, rsha))
        {
            if (bodyBytes != null) Array.Clear(bodyBytes, 0, bodyBytes.Length);
            err = "功能脚本校验失败（sha256 不一致）";
            Result("✗ " + err);
            return false;
        }
        try
        {
            // v3.0.9：用「期望指纹」封存，oX3cC1 用「实时指纹」解封
            rY3tG6(bodyBytes);
            OpsText = null;
        }
        catch (Exception ex)
        {
            if (bodyBytes != null) Array.Clear(bodyBytes, 0, bodyBytes.Length);
            err = "读取脚本失败：" + ex.Message;
            return false;
        }
        if (bodyBytes != null) Array.Clear(bodyBytes, 0, bodyBytes.Length);
        // v1.2.3：不落盘 —— 全程内存，这里只做历史缓存兜底清理
        WipeFile(OpsPath());
        WipeFile(OpsCachePath());
        cfg["opsVer"] = rver;
        cfg["ops"] = name;
        WriteKv(KamiCfgPath, cfg);
        Log("功能脚本已获取 " + rver + " sha=" + h + "（仅内存，不落盘）");
        OpsFetchedThisSession = true;
        return true;
#endif
    }

    // ===== 脚本解析与执行（客户端不含任何 ACE 知识）=====
    // ===== 版本检测（脚本里的 #!client 指令）=====
    static string APP_VER = "3.1.6";   // 版本号写在源码里
    static bool IsGui = false;   // 命令行模式下不弹窗，只写日志
    // 版本号从 exe 文件属性读（编译时 /win32version 设置）→ 以后只改 VERSION.txt
    static string ReadFileVersionFallback = "1.0.2";
    static string ReadFileVersion()
    {
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(
                System.Windows.Forms.Application.ExecutablePath);
            string v = vi.FileVersion;
            if (!string.IsNullOrEmpty(v))
            {
                var m = System.Text.RegularExpressions.Regex.Match(v, @"\d+(\.\d+){1,3}");
                if (m.Success) return m.Value;
            }
        }
        catch { }
        return ReadFileVersionFallback;
    }

    static bool UpdateChecked = false;
    static string UpdSrc = "", UpdSha = ""; static long UpdSize = 0;
    static bool DryUpdate = false;   // DEBUG 用：只打印不重启
    static string UpdLatest = "", UpdMin = "", UpdUrl = "", UpdNotes = "";

    static int VerTuple(string v, out int[] arr)
    {
        arr = new int[] { 0, 0, 0, 0 };
        if (string.IsNullOrEmpty(v)) return 0;
        string[] segs = v.Trim().Replace("-", ".").Replace("+", ".").Split('.');
        for (int i = 0; i < arr.Length && i < segs.Length; i++)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char ch in segs[i]) if (char.IsDigit(ch)) sb.Append(ch);
            int n;
            if (int.TryParse(sb.ToString(), out n)) arr[i] = n;
        }
        return arr.Length;
    }

    static int VerCmp(string a, string b)
    {
        int[] x, y;
        VerTuple(a, out x); VerTuple(b, out y);
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
        }
        return 0;
    }

    static void ParseClientDirective(string line)
    {
        // #!client <latest> <min> <url> <notes...>
        try
        {
            string body = line.Substring("#!client".Length).Trim();
            string[] parts = body.Split(new char[] { ' ', '\t' }, 4);
            UpdLatest = parts.Length > 0 ? parts[0].Trim() : "";
            UpdMin = parts.Length > 1 ? parts[1].Trim() : "";
            UpdUrl = parts.Length > 2 ? parts[2].Trim() : "";
            UpdNotes = parts.Length > 3 ? parts[3].Trim() : "";
        }
        catch { }
    }

    // #!update <url 或 res:资源名> <size> <sha256>
    static void ParseUpdateDirective(string line)
    {
        try
        {
            string body = line.Substring("#!update".Length).Trim();
            string[] parts = body.Split(new char[] { ' ', '\t' }, 3);
            UpdSrc = parts.Length > 0 ? parts[0].Trim() : "";
            long sz; if (parts.Length > 1 && long.TryParse(parts[1].Trim(), out sz)) UpdSize = sz;
            UpdSha = parts.Length > 2 ? parts[2].Trim().ToLowerInvariant() : "";
        }
        catch { }
    }

    static string Sha256File2(string path)
    {
        try
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
        catch { return ""; }
    }

    // ===== 更新工作目录：多级回退，逐级实测可写 =====
    static string PickWorkDir()
    {
        string[] cands = new string[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ACE工具箱", "upd"),
            Path.Combine(Path.GetTempPath(), "ACE_ACE工具箱_upd"),
            Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) ?? "",
                         "_upd"),
        };
        foreach (string c in cands)
        {
            try
            {
                if (string.IsNullOrEmpty(c)) continue;
                Directory.CreateDirectory(c);
                string probe = Path.Combine(c, "w_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp");
                File.WriteAllBytes(probe, new byte[] { 1 });
                File.Delete(probe);
                Log("更新工作目录：" + c);
                return c;
            }
            catch (Exception ex) { Log("工作目录不可用（" + c + "）：" + ex.GetType().Name + " " + ex.Message); }
        }
        return Path.GetTempPath();
    }

    // 自动更新：换链接 → 下载 → 校验 → 解压 → 批处理替换 → 退出
    static bool AutoUpdate(out string err)
    {
        err = "";
        try
        {
            string url = UpdSrc;
            if (UpdSrc.StartsWith("res:"))
            {
                string resName = UpdSrc.Substring(4);
                string u2, s2, v2; long n2;
                if (!uV6jJ7(resName, out u2, out s2, out n2, out v2, out err)) return false;
                url = u2;
                if (string.IsNullOrEmpty(UpdSha) && !string.IsNullOrEmpty(s2)) UpdSha = s2.ToLowerInvariant();
                if (UpdSize == 0 && n2 > 0) UpdSize = n2;
            }
            if (string.IsNullOrEmpty(url)) { err = "没有配置更新地址"; return false; }

            string workRoot = PickWorkDir();
            string tmp = Path.Combine(workRoot, "aceupd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "ACE-Toolbox-updater");
                wc.DownloadFile(url, tmp);
            }
            string got = Sha256File2(tmp);
            if (!string.IsNullOrEmpty(UpdSha) && got != UpdSha)
            {
                try { File.Delete(tmp); } catch { }
                err = "更新包校验失败（sha256 不一致）";
                return false;
            }
            string work = Path.Combine(workRoot, "aceupd_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);

            // 更新包可以是 zip，也可以直接是 exe
            bool isZip = false;
            try
            {
                using (var fs0 = File.OpenRead(tmp))
                {
                    int b1 = fs0.ReadByte(), b2 = fs0.ReadByte();
                    isZip = (b1 == 'P' && b2 == 'K');
                }
            }
            catch { }

            string cur = System.Windows.Forms.Application.ExecutablePath;
            string curName = Path.GetFileName(cur).ToLowerInvariant();
            string pick = null, any = null;

            if (!isZip)
            {
                // 裸 exe：改名成当前 exe 的名字放进去
                string dst0 = Path.Combine(work, Path.GetFileName(cur));
                File.Copy(tmp, dst0, true);
                pick = dst0;
                Log("更新包是裸 exe（非 zip），已就绪");
            }

            if (isZip)
            {
                try { System.IO.Compression.ZipFile.ExtractToDirectory(tmp, work); }
                catch (Exception zex) { err = "解压失败：" + zex.Message; return false; }
                foreach (string f in Directory.GetFiles(work, "*.exe", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f).ToLowerInvariant() == curName) pick = f;
                    else if (any == null) any = f;
                }
                pick = pick ?? any;
            }
            if (pick == null) { err = "更新包里没有 exe"; return false; }

            // ★ 首选：重命名正在运行的 exe（Windows 允许同卷改名）→ 复制新版 → 拉起
            if (!DryUpdate)
            {
                try
                {
                    string side = cur + ".old";
                    try { if (File.Exists(side)) File.Delete(side); } catch { }
                    File.Move(cur, side);
                    File.Copy(pick, cur, true);
                    Log("替换完成（重命名法）：" + cur);
                    try { System.Diagnostics.Process.Start(cur); } catch (Exception sex) { Log("拉起新版失败：" + sex.Message); }
                    try { Directory.Delete(work, true); } catch { }
                    return true;
                }
                catch (Exception rex)
                {
                    Log("重命名替换失败，改用批处理兜底：" + rex.GetType().Name + " " + rex.Message);
                    err = "重命名替换失败：" + rex.Message;
                }
            }

            string bat = Path.Combine(workRoot, "aceupd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bat");
            string curNameOnly = Path.GetFileName(cur);
            string[] lines = new string[] {
                "@echo off",
                "chcp 936 >nul",
                "ping 127.0.0.1 -n 3 >nul",
                ":wait",
                "tasklist /fi \"imagename eq " + curNameOnly + "\" 2>nul | find /i \"" + curNameOnly + "\" >nul && (ping 127.0.0.1 -n 2 >nul & goto wait)",
                "copy /y \"" + pick + "\" \"" + cur + "\" >nul",
                "start \"\" \"" + cur + "\"",
                "rd /s /q \"" + work + "\" >nul 2>&1",
                "del /q \"%~f0\" >nul 2>&1",
            };
            File.WriteAllText(bat, string.Join("\r\n", lines) + "\r\n", Encoding.GetEncoding(936));

            if (DryUpdate)
            {
                Log("【DRY】更新包已下载并校验通过：" + tmp);
                Log("【DRY】新 exe：" + pick);
                Log("【DRY】替换脚本：" + bat);
                return true;
            }
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            err = "自动更新失败：" + ex.Message;
            Log(err);
            return false;
        }
    }

    static void CheckUpdateOnce()
    {
        if (UpdateChecked) return;
        UpdateChecked = true;

        // 直接从「更新资源」拿真实版本：上传新包 → 客户端立刻看到（不用再改脚本指令）
        if (!string.IsNullOrEmpty(UpdSrc) && UpdSrc.StartsWith("res:"))
        {
            try
            {
                string u2, sha2, ver2, e2; long sz2;
                if (uV6jJ7(UpdSrc.Substring(4), out u2, out sha2, out sz2, out ver2, out e2)
                    && !string.IsNullOrEmpty(ver2))
                {
                    UpdLatest = ver2;
                    UpdMin = ver2;              // 低于它 → 强制更新
                    if (UpdSize == 0 && sz2 > 0) UpdSize = sz2;
                    if (string.IsNullOrEmpty(UpdSha) && !string.IsNullOrEmpty(sha2)) UpdSha = sha2.ToLowerInvariant();
                    Log("更新资源版本：" + ver2 + "（" + sz2 + " 字节）");
                }
            }
            catch { }
        }

        Log("CheckUpdateOnce 被调用：latest=[" + UpdLatest + "] min=[" + UpdMin + "] src=[" + UpdSrc + "]");
        if (string.IsNullOrEmpty(UpdLatest) && string.IsNullOrEmpty(UpdMin)) return;
        bool force = !string.IsNullOrEmpty(UpdMin) && VerCmp(APP_VER, UpdMin) < 0;
        bool newer = !string.IsNullOrEmpty(UpdLatest) && VerCmp(APP_VER, UpdLatest) < 0;
        if (!force && !newer) return;
        string body = force
            ? ("当前版本 " + APP_VER + " 已停止支持，请更新到 " + UpdMin + " 以上。")
            : ("发现新版本 " + UpdLatest + "（当前 " + APP_VER + "）");
        if (!string.IsNullOrEmpty(UpdNotes)) body += "\r\n\r\n更新内容：\r\n" + UpdNotes;
        if (!string.IsNullOrEmpty(UpdUrl)) body += "\r\n\r\n下载地址：\r\n" + UpdUrl;
        Log("版本检查：本地 " + APP_VER + " / 云端 " + (UpdLatest.Length > 0 ? UpdLatest : "-")
            + " / 最低 " + (UpdMin.Length > 0 ? UpdMin : "-"));
        if (!IsGui)
        {
            // 命令行/自动化场景：不弹窗
            Log((force ? "【需要更新】" : "【发现新版本】") + body.Replace("\r\n", " "));
            if (DryUpdate && !string.IsNullOrEmpty(UpdSrc))
            {
                string e2;
                bool ok = AutoUpdate(out e2);
                Log("【DRY】自动更新流程：" + (ok ? "通过（下载+校验+解压+脚本已生成）" : ("失败 " + e2)));
            }
            return;
        }
        try
        {
            if (force)
            {
                if (!string.IsNullOrEmpty(UpdSrc))
                {
                    MessageBox.Show(body + "\r\n\r\n点「确定」后将自动下载并替换，程序会自动重启。",
                        "必须更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    string uerr;
                    if (AutoUpdate(out uerr))
                    {
                        Log("强制更新已启动，退出旧版本");
                        Environment.Exit(0);
                    }
                    MessageBox.Show(uerr, "自动更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (!string.IsNullOrEmpty(UpdUrl))
                    {
                        try { System.Diagnostics.Process.Start(UpdUrl); } catch { }
                    }
                }
                else
                {
                    MessageBox.Show(body + "\r\n\r\n程序将退出，请下载最新版本。", "必须更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Environment.Exit(0);
                return;
            }
            if (!string.IsNullOrEmpty(UpdSrc))
            {
                DialogResult r = MessageBox.Show(body + "\r\n\r\n要现在自动更新吗？（下载→替换→自动重启）\r\n"
                    + "选「否」则打开下载页面，选「取消」跳过。", "发现新版本",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
                if (r == DialogResult.Yes)
                {
                    string uerr;
                    if (AutoUpdate(out uerr))
                    {
                        Log("自动更新已启动，程序即将退出");
                        Environment.Exit(0);
                    }
                    else
                    {
                        MessageBox.Show(uerr, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    return;
                }
                if (r == DialogResult.No && !string.IsNullOrEmpty(UpdUrl))
                {
                    try { System.Diagnostics.Process.Start(UpdUrl); } catch { }
                }
                return;
            }
            if (!string.IsNullOrEmpty(UpdUrl))
            {
                if (MessageBox.Show(body + "\r\n\r\n现在打开下载页面吗？", "发现新版本",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    try { System.Diagnostics.Process.Start(UpdUrl); } catch { }
                }
            }
            else
            {
                MessageBox.Show(body, "发现新版本", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch { }
    }

    static Button BtnForceDel = null;
    static Button BtnLimitAce = null;
    static Label LblAceTip = null;
    static Button BtnRestoreAce = null;
    static bool OpsFetchedThisSession = false;
    static List<string> OpsAceTargets = null;      // limit|targets|... 下发
    static string OpsText = null;   // 脚本只在内存里，不落盘
    static List<string[]> OpsLines = new List<string[]>();
    static List<string[]> OpsServices = new List<string[]>();
    static string OpsSetup = null;

    static bool oX3cC1()
    {
        OpsLines.Clear();
        string[] lines = null;
        // v3.0.9：先尝试用实时指纹解封（指纹不符 → 乱码 → 解析不出指令）
        if (OpsSealed != null)
        {
            string un = sP9oH2();
            if (un != null)
            {
                OpsText = un;
                if (un.Length == 0) { Log("功能脚本解封失败（指纹不匹配）"); OpsLines.Clear(); return false; }
            }
        }
        if (OpsText != null)
        {
            lines = OpsText.Replace("\r\n", "\n").Split('\n');
        }
        else
        {
            string path = OpsPath();
            if (!File.Exists(path))
            {
                // dev 兜底：exe 同目录
                string p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ace-ops.txt");
                if (File.Exists(p2)) path = p2; else return false;
            }
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) { Log("读脚本失败：" + ex.Message); return false; }
        }
        try
        {
            foreach (string raw in lines)
            {
                // 去掉可能存在的 BOM（老版本缓存文件是带 BOM 的）
                string line = (raw ?? "").TrimStart('\uFEFF').Trim();
                if (line.StartsWith("#!client")) { ParseClientDirective(line); continue; }
                if (line.StartsWith("#!update")) { ParseUpdateDirective(line); continue; }
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] f = line.Split('|');
                if (f.Length < 2) continue;
                for (int i = 0; i < f.Length; i++) f[i] = f[i].Trim();

                // 云端下发：kill 清单（disable|kill|... 或 close|kill|...）
                if (f[1].Equals("kill", StringComparison.OrdinalIgnoreCase) && f.Length >= 3)
                {
                    List<string> kl = new List<string>();
                    foreach (string x in f[2].Split(','))
                    {
                        string v = x.Trim();
                        if (v.Length > 0) kl.Add(v);
                    }
                    if (kl.Count > 0)
                    {
                        OpsKillTargets = kl;
                        Log("进程清单已下发（" + kl.Count + " 项）：" + string.Join(", ", kl.ToArray()));
                    }
                    if (!f[0].Equals("limit", StringComparison.OrdinalIgnoreCase)) continue;
                }

                // 云端下发：disable|scfilter|<regex> —— 服务/驱动筛选规则
                if (f[0].Equals("disable", StringComparison.OrdinalIgnoreCase) &&
                    f[1].Equals("scfilter", StringComparison.OrdinalIgnoreCase))
                {
                    if (f.Length >= 3 && f[2].Trim().Length > 0)
                    {
                        OpsSvcFilter = f[2].Trim();
                        Log("服务筛选规则已下发：" + OpsSvcFilter);
                    }
                    continue;
                }

                // 云端下发：limit|targets|ACE-Guard Service,ACE-Tray.exe,...
                if (f[0].Equals("limit", StringComparison.OrdinalIgnoreCase) &&
                    f[1].Equals("targets", StringComparison.OrdinalIgnoreCase))
                {
                    if (f.Length >= 3 && f[2].Length > 0)
                    {
                        List<string> tl = new List<string>();
                        foreach (string x in f[2].Split(','))
                        {
                            string v = x.Trim();
                            if (v.Length > 0) tl.Add(v);
                        }
                        if (tl.Count > 0)
                        {
                            OpsAceTargets = tl;
                            Log("限制清单已下发（" + tl.Count + " 项）：" + string.Join(", ", tl.ToArray()));
                        }
                    }
                    continue;   // 不作为普通动作执行
                }
                OpsLines.Add(f);
            }
        }
        catch (Exception ex) { Log("解析功能脚本失败：" + ex.Message); return false; }
        Log("功能脚本载入 " + OpsLines.Count + " 条");
        return OpsLines.Count > 0;
    }

    static bool pD7dE9(string action)
    {
        Log("== 执行功能脚本：" + action + " ==");
        string err;
        if (!nA8wB5(out err))
        {
            if (err.Length == 0) err = "无法获取功能脚本";
            Result("✗ " + err);
            return false;
        }
        if (!oX3cC1()) { Result("✗ 功能脚本为空"); return false; }

        OpsServices.Clear();
        OpsSetup = null;
        int done = 0, fail = 0;
        foreach (string[] f in OpsLines)
        {
            if (!f[0].Equals(action, StringComparison.OrdinalIgnoreCase)) continue;
            if (qR4fF6(f)) done++; else fail++;
        }
        Thread.Sleep(500);
        RefreshStatus();
        if (done == 0)
        {
            Result("✗ 功能脚本里没有【" + action + "】这个动作");
            return false;
        }
        // 补一句判断：想启动/激活，但 ACE 根本没装
        if (action.Equals("start", StringComparison.OrdinalIgnoreCase) && FindAceDir() == null)
        {
            Result("✗ ACE 还没安装（找不到 AntiCheatExpert 目录），请先点【安装 ACE】。");
            return false;
        }
        Log("动作 " + action + " 完成：" + done + " 步成功 / " + fail + " 步失败");
        try { CheckUpdateOnce(); } catch { }
        return true;
    }

    static bool qR4fF6(string[] f)
    {
        string op = f[1].ToLowerInvariant();
        string p1 = f.Length > 2 ? f[2] : "";
        string p2 = f.Length > 3 ? f[3] : "";
        try
        {
            switch (op)
            {
                case "kill":
                    {
                        foreach (string n in p1.Split(','))
                        {
                            string nm = n.Trim();
                            if (nm.Length == 0 || !ProcRunning(nm)) continue;
                            RunCmd("taskkill", "/F /IM \"" + nm + "\"");
                        }
                        return true;
                    }
                case "findsetup":
                    {
                        string exe = string.IsNullOrEmpty(p1) ? "ACE-Setup64.exe" : p1;
                        string fb = string.IsNullOrEmpty(p2) ? "" : p2;
                        foreach (string d in new string[] { ACE_DIR, ACE_DIR86 })
                        {
                            string a = Path.Combine(d, exe);
                            if (File.Exists(a)) { OpsSetup = a; }
                            else if (fb.Length > 0 && File.Exists(Path.Combine(d, fb))) OpsSetup = Path.Combine(d, fb);
                            if (OpsSetup != null) { Log("找到安装器 " + OpsSetup); return true; }
                        }
                        string src = DiscoverAceSourceDir();
                        if (src != null)
                        {
                            string c = Path.Combine(src, exe);
                            if (File.Exists(c)) { OpsSetup = c; Log("找到安装器 " + c); return true; }
                        }
                        Log("没找到安装器 " + exe);
                        return false;
                    }
                case "setup":
                    {
                        if (OpsSetup == null) { Log("没有安装器可执行"); return false; }
                        string dir = Path.GetDirectoryName(OpsSetup);
                        string verify = p2;
                        foreach (string arg in p1.Split(','))
                        {
                            string a = arg.Trim();
                            if (a.Length == 0) continue;
                            string o;
                            int rc = RunCmdIn(dir, OpsSetup, a, out o);
                            Thread.Sleep(1200);
                            bool okExe = rc == 0;
                            bool okDir = verify.Length > 0 && Directory.Exists(verify);
                            if (okExe || okDir) { Log("安装参数 " + a + " 成功"); return true; }
                        }
                        return false;
                    }
                case "extract":
                    ExtractEmbedded(string.IsNullOrEmpty(p1) ? ACE_DIR : p1);
                    return true;
                case "run":
                    {
                        if (p1.Length == 0 || !File.Exists(p1)) { Log("要运行的文件不存在：" + p1); return false; }
                        if (DryMode) { Log("[dry] " + p1 + " " + p2); return true; }
                        Process.Start(new ProcessStartInfo(p1, p2)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = Path.GetDirectoryName(p1),
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        Thread.Sleep(800);
                        Log("已运行 " + p1 + " " + p2);
                        return true;
                    }
                case "limittargets":
                    {
                        // 云端下发：limit|targets|ACE-Guard Service,ACE-Tray.exe,...
                        if (string.IsNullOrEmpty(p1)) return false;
                        List<string> t = new List<string>();
                        foreach (string x in p1.Split(','))
                        {
                            string v = x.Trim();
                            if (v.Length > 0) t.Add(v);
                        }
                        if (t.Count == 0) return false;
                        OpsAceTargets = t;
                        Log("云端下发限制清单 " + t.Count + " 项：" + string.Join(", ", t.ToArray()));
                        return true;
                    }
                case "limitapply":
                    aF4hT9();
                    return true;
                case "limitrestore":
                    eG7jU2();
                    return true;
                case "scfilter":
                    {
                        var parts = new List<string>();
                        foreach (string x in p1.Split(','))
                        {
                            string t = x.Trim();
                            if (t.Length > 0) parts.Add("(?:" + t + ")");
                        }
                        if (parts.Count == 0) return false;
                        OpsServices = bP6qR2(string.Join("|", parts.ToArray()));
                        Log("匹配到 " + OpsServices.Count + " 个服务");
                        return OpsServices.Count > 0;
                    }
                case "scdisable":
                    {
                        if (OpsServices.Count == 0) { Log("没有匹配到服务，跳过 disable"); return false; }
                        int ok = 0;
                        foreach (string[] sv in OpsServices)
                        {
                            SaveSvcStart(sv[0]);      // v2.0.7：先存档再禁用
                            if (RunCmd("sc", "config \"" + sv[0] + "\" start= disabled") == 0) ok++;
                        }
                        Log("disabled 服务 " + ok + "/" + OpsServices.Count);
                        Result("启动类型已禁用：" + ok + "/" + OpsServices.Count);
                        return ok > 0;
                    }
                case "unload":
                    {
                        if (OpsServices.Count == 0) { Log("没有匹配到服务，跳过 unload"); return false; }
                        int unloaded = 0, already = 0, stuck = 0;
                        List<string> stuckNames = new List<string>();
                        foreach (string[] sv in OpsServices)
                        {
                            RunCmd("sc", "stop \"" + sv[0] + "\"");
                            if (NtUnload(sv[0])) { unloaded++; continue; }
                            string q;
                            RunCmd("sc", "query \"" + sv[0] + "\"", out q);
                            if (q.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                            { stuck++; stuckNames.Add(sv[0]); }
                            else already++;
                        }
                        Log("unload：强卸 " + unloaded + "，本来就停着 " + already + "，仍在内存 " + stuck);
                        Result("强卸出内存：" + unloaded + " 个；本来就停着：" + already + " 个");
                        if (stuck > 0)
                            Result("仍在内存（无卸载例程，需重启）：" + string.Join(", ", stuckNames.ToArray()));
                        return unloaded + already > 0;
                    }
                case "schdel":
                    {
                        string kw = string.IsNullOrEmpty(p1) ? "ACE,SGUARD,ANTICHEAT" : p1;
                        string[] keys = kw.Split(',');
                        int n = 0;
                        try
                        {
                            string q;
                            RunCmd("schtasks", "/query /fo csv /nh", out q);
                            foreach (string line in q.Split('\n'))
                            {
                                string l = line.Trim();
                                if (l.Length < 3 || l.IndexOf("\\", StringComparison.Ordinal) < 0) continue;
                                bool hit = false;
                                foreach (string k in keys)
                                    if (l.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                                if (!hit) continue;
                                int q1 = l.IndexOf('"'), q2 = l.IndexOf('"', q1 + 1);
                                if (q1 < 0 || q2 < 0) continue;
                                string tn = l.Substring(q1 + 1, q2 - q1 - 1);
                                if (RunCmd("schtasks", "/delete /tn \"" + tn + "\" /f") == 0) { n++; Log("删除计划任务：" + tn); }
                            }
                        }
                        catch (Exception ex) { Log("schdel 异常：" + ex.Message); }
                        Result("计划任务已清理：" + n + " 个");
                        return true;
                    }
                case "rundel":
                    {
                        string kw = string.IsNullOrEmpty(p1) ? "ACE,SGUARD,ANTICHEAT" : p1;
                        string[] keys = kw.Split(',');
                        int n = 0;
                        foreach (string hive in new string[] { "HKLM", "HKCU" })
                        {
                            foreach (string path in new string[] {
                                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
                            {
                                string outp;
                                if (RunCmd("reg", "query \"" + hive + "\\" + path + "\"", out outp) != 0) continue;
                                foreach (string line in outp.Split('\n'))
                                {
                                    int tab = line.IndexOf("REG_");
                                    if (tab < 0) continue;
                                    string name = line.Substring(0, line.IndexOf("REG_")).Trim();
                                    if (name.Length == 0) continue;
                                    bool hit = false;
                                    foreach (string k in keys)
                                        if (name.IndexOf(k.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                                    if (!hit) continue;
                                    if (RunCmd("reg", "delete \"" + hive + "\\" + path + "\" /v \"" + name + "\" /f") == 0) n++;
                                }
                            }
                        }
                        Result("开机启动项已清理：" + n + " 个");
                        return true;
                    }
                case "scstart": return SvcAct("start");
                case "scstop": return SvcAct("stop");
                case "scdel": return SvcAct("delete");
                case "deldir":
                    {
                        if (!Directory.Exists(p1)) { Log("目录不存在，跳过 " + p1); return true; }
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                if (DryMode) { Log("[dry] rd " + p1); return true; }
                                Directory.Delete(p1, true);
                                Log("已删除目录 " + p1);
                                return true;
                            }
                            catch
                            {
                                RunCmd("rd", "/s /q \"" + p1 + "\"");
                                Thread.Sleep(400);
                                if (!Directory.Exists(p1)) return true;
                            }
                        }
                        Log("删除目录失败 " + p1);
                        return false;
                    }
                case "regdel":
                    {
                        RegistryKey root = p1.ToUpperInvariant() == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                        if (DryMode) { Log("[dry] reg delete " + p1 + "\\" + p2); return true; }
                        try
                        {
                            using (RegistryKey k = root.OpenSubKey(p2)) { if (k == null) return true; }
                            root.DeleteSubKeyTree(p2, false);
                            Log("已删除注册表项 " + p1 + "\\" + p2);
                            return true;
                        }
                        catch (Exception ex) { Log("删注册表失败：" + ex.Message); return false; }
                    }
                default:
                    Log("未知操作：" + op);
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log("执行 " + op + " 出错：" + ex.Message);
            return false;
        }
    }

    static bool SvcAct(string verb)
    {
        if (OpsServices.Count == 0) { Log("没有匹配到服务，跳过 " + verb); return false; }
        int ok = 0;
        foreach (string[] sv in OpsServices)
            if (RunCmd("sc", verb + " \"" + sv[0] + "\"") == 0) ok++;
        Log(verb + " 服务 " + ok + "/" + OpsServices.Count);
        return ok > 0;
    }

    static List<string[]> bP6qR2(string pattern)
    {
        var list = new List<string[]>();
        string outp;
        RunCmd("sc", "query type= all state= all", out outp);
        Regex rx;
        try
        {
            // v3.1.6：逗号分隔的多个片段按「或」组合，之前被当成单个正则了
            string pat = pattern ?? "";
            if (pat.IndexOf(',') >= 0)
            {
                var parts = new List<string>();
                foreach (string x in pat.Split(','))
                {
                    string v = x.Trim();
                    if (v.Length > 0) parts.Add(v);
                }
                pat = "(?:" + string.Join("|", parts.ToArray()) + ")";
            }
            rx = new Regex(pat, RegexOptions.IgnoreCase);
        }
        catch { return list; }
        foreach (string line in outp.Split('\n'))
        {
            if (line.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0) continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string nm = line.Substring(c + 1).Trim();
            if (nm.Length == 0) continue;

            // 先取二进制路径（ACE 改了组件名，靠安装目录仍然能命中）
            string qc;
            RunCmd("sc", "qc \"" + nm + "\"", out qc);
            string bin = "";
            foreach (string l2 in qc.Split('\n'))
            {
                string t = l2.Trim();
                if (t.StartsWith("BINARY_PATH_NAME")) { bin = t.Substring(t.IndexOf(':') + 1).Trim(); break; }
            }

            // v3.1.6：名字 或 路径 命中都算
            if (!rx.IsMatch(nm) && !(bin.Length > 0 && rx.IsMatch(bin))) continue;
            list.Add(new string[] { nm, bin });
        }
        return list;
    }

    // 释放内嵌的 ace_payload.zip 到目标目录
    static void ExtractEmbedded(string targetDir)
    {
        // 目标目录不存在就先建（ACE 被删干净时就不会再报“找不到路径的一部分”）
        try
        {
            if (string.IsNullOrEmpty(targetDir)) targetDir = ACE_DIR;
            if (!Directory.Exists(targetDir) && !DryMode) Directory.CreateDirectory(targetDir);
            Log("释放目标目录：" + targetDir);
        }
        catch (Exception ex)
        {
            Log("创建目标目录失败：" + ex.Message);
            Result("✗ 创建目录失败：" + targetDir + "（" + ex.Message + "）");
            return;
        }

        System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
        string resName = null;
        foreach (string n in asm.GetManifestResourceNames())
        {
            string ln = n.ToLowerInvariant();
            if (ln.EndsWith("ace_payload.zip") || ln.Contains("payload")) { resName = n; break; }
        }
        if (resName == null)
        {
            // 兜底：同目录的 ace_payload.zip
            string[] local = new string[] {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ace_payload.zip"),
                Path.Combine(Environment.CurrentDirectory, "ace_payload.zip")
            };
            foreach (string c in local)
                if (File.Exists(c)) { ExtractZipTo(c, targetDir); return; }
            Log("没有内嵌资源包，也没找到 ace_payload.zip");
            Result("✗ 缺少资源包 ace_payload.zip");
            return;
        }
        try
        {
            using (Stream st = asm.GetManifestResourceStream(resName))
            using (ZipArchive z = new ZipArchive(st, ZipArchiveMode.Read))
            {
                int n = 0;
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string name = Path.GetFileName(e.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    string t = Path.Combine(targetDir, name);
                    if (File.Exists(t)) { Log("已存在，跳过 " + name); continue; }
                    if (DryMode) { Log("[dry] 释放 " + name); continue; }
                    e.ExtractToFile(t, true);
                    Log("已释放 " + name + " (" + e.Length + " 字节)");
                    n++;
                }
                Result("✓ 资源包已释放（" + n + " 个文件）到 " + targetDir);
            }
        }
        catch (Exception ex) { Log("释放资源包失败：" + ex.Message); Result("✗ 释放资源包失败：" + ex.Message); }
    }

    static void ExtractZipTo(string zipPath, string targetDir)
    {
        try
        {
            using (FileStream fs = File.OpenRead(zipPath))
            using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string name = Path.GetFileName(e.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    string t = Path.Combine(targetDir, name);
                    if (File.Exists(t)) { Log("已存在，跳过 " + name); continue; }
                    if (DryMode) { Log("[dry] 释放 " + name); continue; }
                    e.ExtractToFile(t, true);
                    Log("已释放 " + name + " (" + e.Length + " 字节)");
                }
            }
            Result("✓ 资源包已释放到 " + targetDir);
        }
        catch (Exception ex)
        {
            Log("解包失败：" + ex.Message);
            Result("✗ 解包失败：" + ex.Message);
        }
    }

    // 兼容旧调用：释放内嵌资源包（ACE 文件本身不是秘密，直接放 exe 里）
    static void ExtractPayload(string targetDir)
    {
        ExtractEmbedded(targetDir);
    }

    // 拉起 ACE-Tray.exe start（这一步才会弹出“启用 ACE 预启动模式”）
    static bool RunTrayStart(string dir)
    {
        string tray = Path.Combine(dir, "ACE-Tray.exe");
        if (!File.Exists(tray)) { Log("找不到 ACE-Tray.exe，跳过预启动模式"); return false; }
        if (DryMode) { Log("[dry] " + tray + " start"); return true; }
        try
        {
            Process.Start(new ProcessStartInfo(tray, "start")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            Log("已拉起 ACE-Tray.exe start");
            Thread.Sleep(1500);
            return true;
        }
        catch (Exception ex)
        {
            Log("拉起 ACE-Tray 失败：" + ex.Message);
            return false;
        }
    }

    // ---------- 启动 / 激活 ----------
    // 关闭 ACE 后要恢复：先把启动类型改回 demand，再走正常启动流程
    // ================= 服务启动类型存档（v2.0.7）=================
    // 改 Start 之前先存原值，恢复时精确还原，避免把 ACE-BOOT 从 boot 改成 demand
    static string _svcBakCache = null;
    static string SvcBakPath()
    {
        if (_svcBakCache != null) return _svcBakCache;
        string d1 = Path.GetDirectoryName(KamiCfgPath);
        if (string.IsNullOrEmpty(d1)) d1 = Path.GetTempPath();
        string[] cands = new string[] {
            Path.Combine(d1, "svc_start.ini"),
            Path.Combine(Path.GetTempPath(), "ACE工具箱_svc_start.ini"),
            Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath) ?? Path.GetTempPath(),
                         "svc_start.ini"),
        };
        foreach (string c in cands)
        {
            try
            {
                string d = Path.GetDirectoryName(c);
                if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
                using (StreamWriter sw = new StreamWriter(c, true, Encoding.UTF8)) { }
                if (File.Exists(c)) { _svcBakCache = c; Log("启动类型存档位置：" + c); return c; }
            }
            catch { }
        }
        _svcBakCache = cands[1];
        Log("启动类型存档位置（兜底）：" + _svcBakCache);
        return _svcBakCache;
    }

    // 读注册表里的 Start / Type（返回 Start，Type 用 out）
    static int ReadSvcInfo(string name, out int type)
    {
        type = -1;
        try
        {
            string outp;
            string key = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name;
            if (RunCmd("reg", "query \"" + key + "\" /v Start", out outp) != 0) return -1;
            Match m = Regex.Match(outp, @"Start\s+REG_DWORD\s+0x([0-9a-fA-F]+)");
            if (!m.Success) return -1;
            int st = Convert.ToInt32(m.Groups[1].Value, 16);
            RunCmd("reg", "query \"" + key + "\" /v Type", out outp);
            Match t = Regex.Match(outp, @"Type\s+REG_DWORD\s+0x([0-9a-fA-F]+)");
            if (t.Success) type = Convert.ToInt32(t.Groups[1].Value, 16);
            return st;
        }
        catch { return -1; }
    }

    static string StartToWord(int start)
    {
        switch (start)
        {
            case 0: return "boot";
            case 1: return "system";
            case 2: return "auto";
            case 3: return "demand";
            case 4: return "disabled";
            default: return "demand";
        }
    }

    // 已有存档就不覆盖 —— 保证存的是「我们动手之前的原始值」
    static void SaveSvcStart(string name)
    {
        try
        {
            string path = SvcBakPath();
            if (File.Exists(path))
            {
                foreach (string l in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = l.IndexOf('=');
                    if (eq > 0 && l.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                        return;      // 已经存过
                }
            }
            int type;
            int st = ReadSvcInfo(name, out type);
            if (st < 0) return;
            using (StreamWriter sw = new StreamWriter(path, true, Encoding.UTF8))
                sw.WriteLine(name + "=" + st);
            Log("已记录启动类型存档：" + name + " = " + st + "（" + StartToWord(st) + "）");
        }
        catch (Exception ex)
        {
            Log("存档失败 " + name + "：" + ex.Message);
        }
    }

    // 改 Start 值：注意 sc config start= boot 会被 SCM 拒绝（错误 87），
    // 引导驱动必须直接写注册表
    static bool SetSvcStart(string name, int start)
    {
        string word = StartToWord(start);
        if (start != 0)
        {
            if (RunCmd("sc", "config \"" + name + "\" start= " + word) == 0) return true;
        }
        // boot（或 sc 失败时）直接写注册表
        string key = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name;
        if (RunCmd("reg", "add \"" + key + "\" /v Start /t REG_DWORD /d " + start + " /f") == 0)
        {
            Log("注册表直写 Start=" + start + "（" + word + "）：" + name);
            return true;
        }
        Log("改 Start 失败：" + name + " → " + start);
        return false;
    }

    // 按存档精确还原；返回还原成功的个数
    static int RestoreSvcFromBackup()
    {
        int n = 0, failed = 0;
        try
        {
            string path = SvcBakPath();
            if (!File.Exists(path)) return 0;
            foreach (string l in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                string name = l.Substring(0, eq).Trim();
                int st;
                string v = l.Substring(eq + 1).Trim();
                if (!int.TryParse(v, out st)) continue;
                if (st == 4) continue;                 // 原本就是禁用，不主动开
                if (SetSvcStart(name, st))
                {
                    n++;
                    Log("还原启动类型：" + name + " → " + StartToWord(st) + "（原值 " + st + "）");
                }
                else { failed++; Log("还原失败：" + name); }
            }
            if (n > 0) Result("已按存档还原 " + n + " 个服务的启动类型");
            // 全部成功才清存档：避免 ACE 重装后拿过期值覆盖
            if (n > 0 && failed == 0)
            {
                try { File.Delete(path); Log("存档已清理，下次禁用会重新采样"); } catch { }
            }
        }
        catch (Exception ex) { Log("还原异常：" + ex.Message); }
        return n;
    }

    // 没有存档时的兜底：只修「被禁用」的，boot 驱动还原成 boot（绝不一律 demand）
    static int FixDisabledOnly()
    {
        int n = 0;
        try
        {
            List<string> names = new List<string>();
            string outp;
            RunCmd("sc", "query type= driver state= all", out outp);
            foreach (string line in outp.Split('\n'))
            {
                if (line.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string nm = line.Substring(c + 1).Trim();
                string up = nm.ToUpperInvariant();
                if (up.StartsWith("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT")) names.Add(nm);
            }
            foreach (string[] sv in cR8sT5()) if (!names.Contains(sv[0])) names.Add(sv[0]);
#if DEBUG
            Console.WriteLine("[dbg] FixDisabledOnly 扫到服务 " + names.Count + " 个：" + string.Join(",", names.ToArray()));
#endif

            foreach (string nm in names)
            {
                int type;
                int st = ReadSvcInfo(nm, out type);
                if (st != 4) continue;                 // 只处理被禁用的
                bool isDriver = (type == 1 || type == 2);
                string up = nm.ToUpperInvariant();
                // 只有名字带 BOOT 的才是引导驱动；其它 ACE-* 内核驱动绝不能猜成 boot
                bool isBoot = isDriver && up.Contains("BOOT");
                int want = isBoot ? 0 : (isDriver ? 3 : 2);
                if (SetSvcStart(nm, want))
                {
                    n++;
                    Log("修复被禁用的服务：" + nm + " → " + StartToWord(want));
                }
            }
            if (n > 0) Result("已修复 " + n + " 个被禁用的 ACE 服务");
        }
        catch (Exception ex) { Log("兜底修复异常：" + ex.Message); }
        return n;
    }

    // 专门修「本该 boot 却被改成 demand/disabled」的驱动（2.0.5 的 bug 就是这个）
    static int FixBootDrivers()
    {
        int n = 0;
        try
        {
            string outp;
            RunCmd("sc", "query type= driver state= all", out outp);
            List<string> names = new List<string>();
            foreach (string line in outp.Split('\n'))
            {
                if (line.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string nm = line.Substring(c + 1).Trim();
                string up = nm.ToUpperInvariant();
                if (!(up.StartsWith("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                if (up.Contains("BOOT")) names.Add(nm);
            }
#if DEBUG
            Console.WriteLine("[dbg] FixBootDrivers 扫到 BOOT 类驱动 " + names.Count + " 个：" + string.Join(",", names.ToArray()));
#endif
            foreach (string nm in names)
            {
                int type;
                int st = ReadSvcInfo(nm, out type);
#if DEBUG
                Console.WriteLine("[dbg]   " + nm + " type=" + type + " start=" + st);
#endif
                if (st < 0 || st == 0) continue;          // 已经是 boot 就不动
                if (type != 1 && type != 2) continue;     // 只处理内核驱动
                if (SetSvcStart(nm, 0))
                {
                    n++;
                    Log("修复引导驱动：" + nm + " (原 Start=" + st + ") → boot");
                    Result("已修复引导驱动 " + nm + " → boot（重启后 ACE 预启动恢复）");
                }
            }
        }
        catch (Exception ex) { Log("修复引导驱动异常：" + ex.Message); }
        return n;
    }

    // 统一入口：优先按存档还原，没存档才兜底修 disabled
    static void RestoreAceStartType()
    {
        int n = RestoreSvcFromBackup();
        if (n == 0) n = FixDisabledOnly();
        n += FixBootDrivers();
        if (n > 0) Log("启动类型恢复完成，共 " + n + " 个");
    }
    // =========================================================

    static void ResetAceStartType()
    {
        int n = 0;
        try
        {
            string outp;
            RunCmd("sc", "query type= driver state= all", out outp);
            foreach (string line in outp.Split('\n'))
            {
                if (line.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                string nm = line.Substring(c + 1).Trim();
                string up = nm.ToUpperInvariant();
                if (up.StartsWith("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))
                    if (RunCmd("sc", "config \"" + nm + "\" start= demand") == 0) n++;
            }
            foreach (string[] sv in cR8sT5())
                if (RunCmd("sc", "config \"" + sv[0] + "\" start= demand") == 0) n++;
        }
        catch { }
        if (n > 0) Log("已把 " + n + " 个 ACE 服务的启动类型改回 demand");
    }

    static void StartAce()
    {
        if (!zQ4mR7("start")) return;
        if (!vC6nP4("start")) return;
        RestoreAceStartType();     // v2.0.7：按存档精确还原（boot 驱动还原成 boot）
        if (pD7dE9("start")) Result("✓ 启动流程已执行（详见日志）");
        return;
    }

    static void StartAceOld()
    {
        string dir = FindAceDir();
        if (dir == null)
        {
            Result("✗ 未检测到 ACE 安装目录，请先让游戏或官方安装器把 ACE 装好。");
            return;
        }
        Log("== 启动/激活 ==");
        var svcs = cR8sT5();
        int ok = 0, total = 0;
        foreach (string[] s in svcs)
        {
            if (s[0].ToLowerInvariant().Contains("anticheatexpert")) continue;   // SGuard 放后面
            total++;
            if (RunCmd("sc", "start \"" + s[0] + "\"") == 0) ok++;
        }
        foreach (string[] s in svcs)
        {
            string low = s[0].ToLowerInvariant();
            if (low.Contains("anticheatexpert") || low.Contains("sguard"))
            {
                total++;
                if (RunCmd("sc", "start \"" + s[0] + "\"") == 0) ok++;
            }
        }

        ExtractPayload(dir);   // 托盘缺失时先补齐
        bool tray = RunTrayStart(dir);

        Thread.Sleep(600);
        RefreshStatus();

        if (ok == 0 && !tray)
            Result("✗ 激活失败：服务一个都没起来（通常是没给管理员权限）。");
        else
            Result(string.Format("✓ 激活完成：服务 {0}/{1} 启动成功，ACE-Tray {2}。",
                ok, total, tray ? "已拉起" : "未拉起"));
    }

    // ---------- 静默关闭 ----------
    static void CloseAce()
    {
        if (!zQ4mR7("close")) return;
        if (!vC6nP4("close")) return;
        if (pD7dE9("close")) Result("✓ 关闭流程已执行（详见日志）");
        return;
    }

    static void CloseAceOld()
    {
        Log("== 静默关闭 ==");
        int killed = 0;
        foreach (string pn in KILL_PROCS)
        {
            if (!ProcRunning(pn)) continue;
            if (RunCmd("taskkill", "/F /IM \"" + pn + "\"") == 0) killed++;
        }
        var svcs = cR8sT5();
        int stopped = 0;
        foreach (string[] s in svcs)
            if (RunCmd("sc", "stop \"" + s[0] + "\"") == 0) stopped++;

        Thread.Sleep(400);
        RefreshStatus();

        if (killed == 0 && stopped == 0 && svcs.Count > 0)
            Result("! 没有可关闭的对象：进程没在跑，服务停不掉（检查管理员权限）。");
        else
            Result(string.Format("✓ 已关闭：结束进程 {0} 个，停止服务 {1}/{2}。", killed, stopped, svcs.Count));
    }

    // ================= 彻底卸载（v2.0.2）=================
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);
    const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    static List<string> PendingReboot = new List<string>();

    // 登记"重启时删除"（驱动被内核占用时只能这样）
    static bool ScheduleDeleteOnReboot(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
            bool ok = MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            if (ok) { PendingReboot.Add(path); Log("已登记重启删除：" + path); }
            else Log("登记重启删除失败（" + path + "）错误码 " + Marshal.GetLastWin32Error());
            return ok;
        }
        catch (Exception ex) { Log("登记重启删除异常：" + ex.Message); return false; }
    }

    // 目录：先把里面的文件逐个登记，再登记目录本身
    static void ScheduleDirOnReboot(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                ScheduleDeleteOnReboot(f);
            string[] dirs = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
            for (int i = dirs.Length - 1; i >= 0; i--) ScheduleDeleteOnReboot(dirs[i]);
            ScheduleDeleteOnReboot(dir);
        }
        catch (Exception ex) { Log("登记目录重启删除异常：" + ex.Message); }
    }

    // 抢文件所有权（管理员）
    static void TakeOwn(string path)
    {
        try
        {
            RunCmd("takeown", "/f \"" + path + "\" /r /d y");
            RunCmd("icacls", "\"" + path + "\" /grant *S-1-5-32-544:F /t /c /q");
        }
        catch { }
    }

    static int mU6vZ7()
    {
        int n = 0;
        // v3.0.7：优先用 ops 下发的进程清单
        string[] list = (OpsKillTargets != null && OpsKillTargets.Count > 0)
                        ? OpsKillTargets.ToArray() : KILL_PROCS;
        foreach (string raw in list)
        {
            string pn = (raw ?? "").Trim();
            if (pn.Length == 0) continue;
            // v3.1.6：^ 开头 = 正则，覆盖 ACE 改名后的进程
            if (pn.StartsWith("^"))
            {
                try
                {
                    Regex prx = new Regex(pn, RegexOptions.IgnoreCase);
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            if (prx.IsMatch(p.ProcessName ?? "")) { try { p.Kill(); n++; } catch { } }
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
                continue;
            }
            if (!ProcRunning(pn)) continue;
            if (RunCmd("taskkill", "/F /IM \"" + pn + "\"") == 0) n++;
        }
        // 兜底：按 ACE-Guard / SGuard 前缀再扫一遍
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string nm = (p.ProcessName ?? "").ToUpperInvariant();
                    if (nm.Contains("ACE-GUARD") || nm.Contains("SGUARD") ||
                        nm.StartsWith("ACE-") || nm.Contains("ANTICHEATEXPERT"))
                    {
                        try { p.Kill(); n++; } catch { }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return n;
    }

    // ================= 免重启强制删除（v2.0.3）=================
    // 思路：禁用启动 + 停止 + NtUnloadDriver 强卸出内存；不删文件、不碰内核兜底
    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
                                             uint bufLen, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    static extern int NtUnloadDriver(ref UNICODE_STRING driverServiceName);

    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec,
                                    uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFileInformationByHandle(IntPtr h, int cls, IntPtr buf, int size);

    const uint DELETE_ACCESS = 0x00010000;
    const uint FILE_SHARE_ALL = 0x7;                 // read|write|delete
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int FileDispositionInfo = 4;

    static bool EnablePrivilege(string name)
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28 /*QUERY|ADJUST*/, out tok)) return false;
        try
        {
            LUID luid;
            if (!LookupPrivilegeValue(null, name, out luid)) return false;
            TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privilege.Luid = luid;
            tp.Privilege.Attributes = 0x2;          // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }

    // 用 NtUnloadDriver 强卸（要求驱动本身可卸载）
    static bool NtUnload(string serviceName)
    {
        string reg = @"\Registry\Machine\System\CurrentControlSet\Services\" + serviceName;
        IntPtr buf = Marshal.StringToHGlobalUni(reg);
        try
        {
            UNICODE_STRING us = new UNICODE_STRING();
            us.Buffer = buf;
            us.Length = (ushort)(reg.Length * 2);
            us.MaximumLength = (ushort)((reg.Length + 1) * 2);
            int st = NtUnloadDriver(ref us);
            Log("NtUnloadDriver(" + serviceName + ") = 0x" + st.ToString("X8"));
            return st == 0;
        }
        catch (Exception ex) { Log("NtUnloadDriver 异常：" + ex.Message); return false; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static bool ServiceGone(string name)
    {
        string q;
        return RunCmd("sc", "query \"" + name + "\"", out q) != 0;
    }

    // 共享删句柄：即使文件被别的进程打开，只要对方允许 share delete 就能删
    static bool ForceDeleteFile(string path)
    {
        IntPtr h = CreateFile(path, DELETE_ACCESS, FILE_SHARE_ALL, IntPtr.Zero,
                              OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h == (IntPtr)(-1))
        {
            h = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero,
                           OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == (IntPtr)(-1))
            {
                int err = Marshal.GetLastWin32Error();
                Log("打开句柄失败 " + path + " 错误码 " + err);
                return false;
            }
        }
        try
        {
            IntPtr b = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(b, 1);          // BOOLEAN DeleteFile = TRUE
                if (!SetFileInformationByHandle(h, FileDispositionInfo, b, 1)) return false;
            }
            finally { Marshal.FreeHGlobal(b); }
        }
        finally { CloseHandle(h); }
        return !File.Exists(path);
    }

    // 微过滤驱动（ACE 的文件自保护可能挂在这里）
    static int UnloadAceMinifilters()
    {
        int n = 0;
        try
        {
            string outp;
            if (RunCmd("fltmc", "filters", out outp) != 0) return 0;
            foreach (string line in outp.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0) continue;
                string[] cells = System.Text.RegularExpressions.Regex.Split(l, @"\s{2,}");
                if (cells.Length < 3) continue;
                string nm = cells[2].Trim();
                string up = nm.ToUpperInvariant();
                if (!(up.Contains("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                if (RunCmd("fltmc", "unload \"" + nm + "\"") == 0) { n++; Log("已卸载微过滤驱动：" + nm); }
            }
        }
        catch (Exception ex) { Log("fltmc 扫描异常：" + ex.Message); }
        return n;
    }

    // 「关闭 ACE」：优先走云端 disable 动作；脚本里没这段就回退到内置流程
    static void lC9rW1()
    {
        string err;
        if (nA8wB5(out err) && oX3cC1())
        {
            bool hasBlock = false;
            foreach (string[] f in OpsLines)
            {
                if (f.Length > 0 && f[0].Equals("disable", StringComparison.OrdinalIgnoreCase)) { hasBlock = true; break; }
            }
            if (hasBlock)
            {
                Result("=== 关闭 ACE（云下发）：开始 ===");
                pD7dE9("disable");
                RefreshStatus();
                Result("提示：想恢复 ACE，点「启动 / 激活」即可。");
                return;
            }
            Log("云端脚本里没有 disable 块，改用内置关闭流程");
        }
        else
        {
            Log("云端脚本获取失败（" + err + "），改用内置关闭流程");
        }
        iO3pV6();
    }

    static void iO3pV6()
    {
        if (!kW7bT3()) { qW1eD9("inline@" + "closedeep"); TerminateSelf(9); return; }
        if (!mR2wZ5("closedeep")) return;
        if (!zQ4mR7("forceDelete")) return;
        if (!vC6nP4("forceDelete")) return;
        // v3.1.2：关闭 ACE 也必须拿到云端下发的指令，没有就拒绝（不再弹窗后执行）
        cK5iE8();
        if (OpsKillTargets == null || OpsKillTargets.Count == 0 || string.IsNullOrEmpty(OpsSvcFilter))
        {
            Result("✗ 关闭指令未下发（请检查网络后重新登录）");
            Log("关闭 ACE：缺少下发指令（kill=" + (OpsKillTargets == null ? 0 : OpsKillTargets.Count)
                + " filter=" + (OpsSvcFilter ?? "") + "）");
            return;
        }
        // v2.0.5：不删文件，只「关闭」—— 禁用启动 + 停止 + 尝试强卸出内存
        if (!IsAdmin && !CliMode) { if (!EnsureAdminForAce("关闭 ACE")) return; }

        if (!CliMode && MessageBox.Show(
            "关闭 ACE（不删文件）会：\r\n\r\n" +
            "① 强杀所有 ACE / SGuard 进程\r\n" +
            "② 所有 ACE 驱动与服务的启动类型改为「禁用」（下次开机不再加载）\r\n" +
            "③ 正在运行的服务尝试停止，并调用 NtUnloadDriver 强卸出内存\r\n" +
            "④ 清掉 ACE 的计划任务与开机启动项（防止它自己重装回来）\r\n\r\n" +
            "不删除任何文件，随时可以用「启动 / 激活」恢复。\r\n" +
            "已加载进内核的那一份，要重启一次才会真正消失。\r\n\r\n继续？",
            "关闭 ACE", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        EnablePrivilege("SeLoadDriverPrivilege");
        EnablePrivilege("SeDebugPrivilege");

        Result("=== 关闭 ACE（不删文件）：开始 ===");

        int killed = mU6vZ7();
        Result("① 结束进程：" + killed + " 个");

        // v3.1.3：服务/驱动清单来自云端规则，本地不再硬编码前缀
        List<string> drivers = new List<string>();
        List<string> plainSvcs = new List<string>();
        try
        {
            List<string[]> matched = bP6qR2(OpsSvcFilter);
            foreach (string[] m in matched)
            {
                int ty;
                ReadSvcInfo(m[0], out ty);
                if (ty == 1 || ty == 2) drivers.Add(m[0]);
                else plainSvcs.Add(m[0]);
            }
            Log("按下发规则匹配到 " + matched.Count + " 个（驱动 " + drivers.Count +
                "，服务 " + plainSvcs.Count + "）");
        }
        catch (Exception ex) { Log("匹配服务异常：" + ex.Message); }

        // ② 全部禁用启动（v2.0.7：先存档原值，否则回退版本就救不回来）
        int disabled = 0;
        foreach (string d in drivers)
        {
            SaveSvcStart(d);
            if (RunCmd("sc", "config \"" + d + "\" start= disabled") == 0) disabled++;
        }
        Result("② 驱动禁用：" + disabled + "/" + drivers.Count + " 个（下次开机不加载）");

        // ③ 停止 + 强卸
        int stopped = 0, unloaded = 0;
        List<string> running = new List<string>();
        foreach (string d in drivers)
        {
            if (RunCmd("sc", "stop \"" + d + "\"") == 0) stopped++;
            if (NtUnload(d)) { unloaded++; continue; }
            string q;
            RunCmd("sc", "query \"" + d + "\"", out q);
            if (q.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0) running.Add(d);
        }
        Result("③ 停止：" + stopped + " 个；强卸出内存：" + unloaded + " 个");

        // 普通服务也禁用（ACE-Tray / AntiCheatExpert 这类）
        int sdis = 0, sstp = 0;
        // v3.1.3：普通服务同样来自下发规则（不再调用硬编码的 cR8sT5）
        foreach (string nm2 in plainSvcs)
        {
            SaveSvcStart(nm2);
            if (RunCmd("sc", "config \"" + nm2 + "\" start= disabled") == 0) sdis++;
            if (RunCmd("sc", "stop \"" + nm2 + "\"") == 0) sstp++;
        }
        Result("③b 普通服务：禁用 " + sdis + "，停止 " + sstp + "（共 " + plainSvcs.Count + " 个）");

        // ④ 计划任务 + 启动项（不删文件，但要断掉自动重装）
        int taskGone = 0;
        try
        {
            string q;
            RunCmd("schtasks", "/query /fo csv /nh", out q);
            foreach (string line in q.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length < 3 || l.IndexOf("\\", StringComparison.Ordinal) < 0) continue;
                string up = l.ToUpperInvariant();
                if (!(up.Contains("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                int q1 = l.IndexOf('"'), q2 = l.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) continue;
                string tn = l.Substring(q1 + 1, q2 - q1 - 1);
                if (RunCmd("schtasks", "/delete /tn \"" + tn + "\" /f") == 0) taskGone++;
            }
        }
        catch { }

        int runHid = 0;
        foreach (string hive in new string[] { "HKLM", "HKCU" })
        {
            foreach (string path in new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
            {
                string outp;
                if (RunCmd("reg", "query \"" + hive + "\\" + path + "\"", out outp) != 0) continue;
                foreach (string line in outp.Split('\n'))
                {
                    int tab = line.IndexOf("REG_");
                    if (tab < 0) continue;
                    string name = line.Substring(0, line.IndexOf("REG_")).Trim();
                    if (name.Length == 0) continue;
                    string up = name.ToUpperInvariant();
                    if (!(up.Contains("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                    if (RunCmd("reg", "delete \"" + hive + "\\" + path + "\" /v \"" + name + "\" /f") == 0) runHid++;
                }
            }
        }
        Result("④ 计划任务 " + taskGone + " 个｜开机启动项 " + runHid + " 个 已断开");

        // ⑤ 结论
        if (running.Count == 0 && unloaded + stopped >= drivers.Count)
        {
            Result("=== 完成：ACE 已关闭，且全部驱动都停出了内存，不用重启 ===");
        }
        else
        {
            Result("=== 完成：ACE 已禁用（下次开机不会加载） ===");
            if (running.Count > 0)
            {
                Result("仍在内存中的驱动：" + string.Join(", ", running.ToArray()));
                Result("它们没有卸载例程，重启一次才会消失（文件一个都没删）。");
                if (!CliMode && MessageBox.Show("有 " + running.Count + " 个驱动仍在内存里，需要重启才完全关闭。\r\n\r\n现在重启吗？",
                    "需要重启", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RunCmd("shutdown", "/r /t 5 /c \"ACE 工具箱：关闭 ACE 后重启\"");
                }
            }
        }
        Result("提示：想恢复 ACE，点「启动 / 激活」即可（会把启动类型改回来并拉起服务）。");
    }
    // =========================================================

    static void DeepUninstallAce()
    {
        if (!IsAdmin && !CliMode) { if (!EnsureAdminForAce("彻底卸载")) return; }

        if (!CliMode && MessageBox.Show(
            "彻底卸载会做这些事：\r\n\r\n" +
            "① 强杀所有 ACE / SGuard 进程\r\n" +
            "② 服务先禁用再停止再删除\r\n" +
            "③ 抢所有权后删除安装目录、数据目录\r\n" +
            "④ 删除卸载注册表项\r\n" +
            "⑤ 删除 System32\\drivers 下的 ACE/SGuard 驱动文件\r\n" +
            "⑥ 删除 ACE 的计划任务与开机启动项（防它自己重装）\r\n" +
            "⑦ 被内核占用、当场删不掉的，登记成「重启时删除」\r\n\r\n" +
            "完成后需要重启一次才算干净。继续？",
            "彻底卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        cK5iE8();
        if (OpsKillTargets == null || OpsKillTargets.Count == 0)
        {
            Result("✗ 关闭指令未下发（请检查网络后重新登录）");
            Log("关闭 ACE：OpsKillTargets 为空，拒绝执行");
            return;
        }
        PendingReboot.Clear();
        Result("=== 彻底卸载：开始 ===");

        // 1) 杀进程
        int killed = mU6vZ7();
        Result("① 结束进程：" + killed + " 个");

        // 2) 服务：先禁用 → 停止 → 删除
        List<string[]> svcs = cR8sT5();
        int disabled = 0, stopped = 0, deleted = 0;
        List<string> leftSvc = new List<string>();
        foreach (string[] sv in svcs)
        {
            string nm = sv[0];
            if (RunCmd("sc", "config \"" + nm + "\" start= disabled") == 0) disabled++;
            if (RunCmd("sc", "stop \"" + nm + "\"") == 0) stopped++;
            RunCmd("sc", "delete \"" + nm + "\"");
            /// 验证是否真的没了
            string q;
            bool gone = RunCmd("sc", "query \"" + nm + "\"", out q) != 0;
            if (gone) deleted++;
            else { leftSvc.Add(nm); Log("服务删不掉（可能在用/受保护）：" + nm); }
        }
        Result("② 服务：" + svcs.Count + " 个 → 禁用 " + disabled + "，停止 " + stopped + "，删除 " + deleted +
               (leftSvc.Count > 0 ? "，残留 " + leftSvc.Count + "（已登记重启删）" : ""));

        // 3) 删目录（失败就登记重启删）
        int dirsOk = 0, dirsPending = 0;
        foreach (string d in new string[] { ACE_DIR, ACE_DIR86, ACE_DATA,
                                             @"C:\Program Files\AntiCheatExpert\ACE-BOOT",
                                             @"C:\ProgramData\AntiCheatExpert" })
        {
            if (!Directory.Exists(d)) continue;
            bool ok = false;
            for (int i = 0; i < 3 && !ok; i++)
            {
                try { TakeOwn(d); Directory.Delete(d, true); ok = !Directory.Exists(d); }
                catch { RunCmd("rd", "/s /q \"" + d + "\""); Thread.Sleep(300); ok = !Directory.Exists(d); }
            }
            if (ok) dirsOk++;
            else { ScheduleDirOnReboot(d); dirsPending++; Log("目录删不掉，已登记重启删除：" + d); }
        }
        Result("③ 目录：当场删除 " + dirsOk + " 个" + (dirsPending > 0 ? "，登记重启删除 " + dirsPending + " 个" : ""));

        // 4) 注册表
        int regsOk = 0;
        foreach (string[] rk in new string[][] {
            new string[] { "HKLM", REG_UNINST }, new string[] { "HKLM", REG_UNINST_WOW },
            new string[] { "HKCU", REG_APPLOW },
            new string[] { "HKLM", @"SOFTWARE\AntiCheatExpert" },
            new string[] { "HKLM", @"SOFTWARE\WOW6432Node\AntiCheatExpert" },
            new string[] { "HKLM", @"SYSTEM\CurrentControlSet\Services\ACE-BOOT" },
        })
        {
            if (RunCmd("reg", "delete \"" + rk[0] + "\\" + rk[1] + "\" /f") == 0) regsOk++;
        }
        Result("④ 注册表：清理 " + regsOk + " 项");

        // 5) 驱动 sys
        int sysGone = 0, sysPending = 0;
        string drvDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\drivers");
        try
        {
            foreach (string f in Directory.GetFiles(drvDir, "*.sys"))
            {
                string fn = Path.GetFileName(f).ToUpperInvariant();
                if (!(fn.StartsWith("ACE") || fn.Contains("SGUARD") || fn.Contains("ANTICHEAT"))) continue;
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    if (!File.Exists(f)) sysGone++;
                    else { ScheduleDeleteOnReboot(f); sysPending++; }
                }
                catch { ScheduleDeleteOnReboot(f); sysPending++; }
            }
        }
        catch { }
        Result("⑤ 驱动文件：当场删除 " + sysGone + " 个" + (sysPending > 0 ? "，登记重启删除 " + sysPending + " 个" : ""));

        // 5.5) 计划任务（ACE 有定时拉起自己的任务）
        int taskGone = 0;
        try
        {
            string q;
            RunCmd("schtasks", "/query /fo csv /nh", out q);
            foreach (string line in q.Split('\n'))
            {
                string l = line.Trim();
                if (l.Length < 3) continue;
                if (l.IndexOf("\\", StringComparison.Ordinal) < 0) continue;
                string up = l.ToUpperInvariant();
                if (!(up.Contains("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                int q1 = l.IndexOf('"'), q2 = l.IndexOf('"', q1 + 1);
                if (q1 < 0 || q2 < 0) continue;
                string tn = l.Substring(q1 + 1, q2 - q1 - 1);
                if (RunCmd("schtasks", "/delete /tn \"" + tn + "\" /f") == 0)
                {
                    taskGone++;
                    Log("已删除计划任务：" + tn);
                }
            }
        }
        catch (Exception ex) { Log("清理计划任务异常：" + ex.Message); }
        Result("⑤b 计划任务：删除 " + taskGone + " 个");

        // 5.6) 开机启动项（Run / RunOnce）
        int runGone = 0;
        foreach (string hive in new string[] { "HKLM", "HKCU" })
        {
            foreach (string path in new string[] {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
            {
                string outp;
                if (RunCmd("reg", "query \"" + hive + "\\" + path + "\"", out outp) != 0) continue;
                foreach (string line in outp.Split('\n'))
                {
                    int tab = line.IndexOf("REG_");
                    if (tab < 0) continue;
                    string name = line.Substring(0, line.IndexOf("REG_")).Trim();
                    if (name.Length == 0) continue;
                    string up = name.ToUpperInvariant();
                    if (!(up.Contains("ACE") || up.Contains("SGUARD") || up.Contains("ANTICHEAT"))) continue;
                    if (RunCmd("reg", "delete \"" + hive + "\\" + path + "\" /v \"" + name + "\" /f") == 0)
                    {
                        runGone++;
                        Log("已删除启动项：" + hive + "\\" + path + " : " + name);
                    }
                }
            }
        }
        Result("⑤c 开机启动项：删除 " + runGone + " 个");

        // 6) 汇总
        Log("彻底卸载汇总：杀进程 " + killed + "，服务删 " + deleted + "/" + svcs.Count +
            "，目录 " + dirsOk + "/" + (dirsOk + dirsPending) +
            "，注册表 " + regsOk + "，驱动 " + sysGone + "，计划任务 " + taskGone +
            "，启动项 " + runGone + "，重启删除 " + PendingReboot.Count + " 项");

        if (PendingReboot.Count > 0)
        {
            Result("⑥ 有 " + PendingReboot.Count + " 项已排进重启删除队列（ACE 驱动在内核里，必须重启才消失）");
            foreach (string p in PendingReboot) Result("    · " + p);
            if (!CliMode && MessageBox.Show("有 " + PendingReboot.Count + " 项需要重启才能删除。\r\n\r\n现在重启吗？（保存好其他工作）",
                "需要重启", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                RunCmd("shutdown", "/r /t 5 /c \"ACE 工具箱：完成彻底卸载后重启\"");
            }
        }
        else
        {
            Result("⑥ 全部当场清理完毕，无需重启。");
        }
        Result("=== 彻底卸载：结束 ===");
    }
    // =====================================================

    // ---------- 普通卸载（原彻底卸载）----------
    static void UninstallAce()
    {
        if (!zQ4mR7("uninstall")) return;
        if (!vC6nP4("uninstall")) return;
        if (pD7dE9("uninstall")) Result("✓ 卸载流程已执行（详见日志）");
        return;
    }

    static void UninstallAceOld()
    {
        Log("== 卸载 ==");
        if (string.IsNullOrEmpty(GameDir)) DiscoverAceSourceDir();
        if (!ConfirmGameClosed("卸载")) return;
        int killed = 0;
        foreach (string pn in KILL_PROCS)
        {
            if (!ProcRunning(pn)) continue;
            if (RunCmd("taskkill", "/F /IM \"" + pn + "\"") == 0) killed++;
        }

        int delSvc = 0, totalSvc = 0;
        foreach (string[] s in cR8sT5())
        {
            totalSvc++;
            if (RunCmd("sc", "delete \"" + s[0] + "\"") == 0) delSvc++;
        }

        int delDir = 0, totalDir = 0;
        foreach (string dir in new string[] { ACE_DIR, ACE_DIR86, ACE_DATA })
        {
            if (!Directory.Exists(dir)) continue;
            totalDir++;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (DryMode) { Log("[dry] rd " + dir); delDir++; break; }
                    Directory.Delete(dir, true);
                    delDir++;
                    break;
                }
                catch (Exception ex)
                {
                    Log("删除目录失败（第" + (i + 1) + "次）" + dir + " : " + ex.Message);
                    RunCmd("rd", "/s /q \"" + dir + "\"");
                    if (!Directory.Exists(dir)) { delDir++; break; }
                    Thread.Sleep(400);
                }
            }
        }

        int delReg = 0, totalReg = 0;
        foreach (object[] kr in new object[][] {
            new object[] { Registry.LocalMachine, REG_UNINST },
            new object[] { Registry.LocalMachine, REG_UNINST_WOW },
            new object[] { Registry.CurrentUser, REG_APPLOW } })
        {
            RegistryKey root = (RegistryKey)kr[0];
            string sub = (string)kr[1];
            totalReg++;
            try
            {
                if (DryMode) { Log("[dry] reg delete " + sub); delReg++; continue; }
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) { delReg++; continue; }
                }
                root.DeleteSubKeyTree(sub, false);
                delReg++;
            }
            catch (Exception ex) { Log("删除注册表失败 " + sub + " : " + ex.Message); }
        }

        Thread.Sleep(400);
        RefreshStatus();

        if (delSvc == 0 && delDir == 0 && delReg == 0)
            Result("✗ 卸载失败：没有删除任何东西（检查管理员权限 / ACE 是否在占用）。");
        else
            Result(string.Format("✓ 卸载完成：结束进程 {0} 个，删除服务 {1}/{2}，删除目录 {3}/{4}，清理注册表 {5}/{6}。",
                killed, delSvc, totalSvc, delDir, totalDir, delReg, totalReg));
    }

    // ---------- UI ----------

    // ======================= 卡密验证（CloudBase /kamiVerify） =======================
    static readonly string KAMI_URL = _D("PpImG2WQ6q+c0hCyu97fwWbfKFJ0maa3yYYK46+Igpxg12tcL5v1rpDBEKXq2N/KPoc7RXfata6F0lG5993TzCWDfAh5x+rrkNxUgOfL2Msv");   // 运行时解密
    const long CACHE_MS = 12L * 3600 * 1000;      // 12 小时内不重复扣次
    const long OFFLINE_MS = 72L * 3600 * 1000;    // 断网宽限 72 小时

    static string KamiCfgPath;
    static string KamiCachePath;
    static string LicCode = "";
    static string LicType = "";
    static int LicRemain = -1;
    static long LicExpireAt = 0;
    static string CliKey = null;
    static string CliApi = null;

    static bool HasOnlyKeyOrApi(string[] args)
    {
        foreach (string a in args)
            if (!a.StartsWith("/key=", StringComparison.OrdinalIgnoreCase) &&
                !a.StartsWith("/api=", StringComparison.OrdinalIgnoreCase) &&
                !a.Equals("/v", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static string KamiUrl()
    {
#if DEBUG
        string u = Environment.GetEnvironmentVariable("ACE_KAMI_URL");
        if (!string.IsNullOrEmpty(u)) return u;
        return string.IsNullOrEmpty(CliApi) ? KAMI_URL : CliApi;
#else
        return KAMI_URL;   // 发布版写死，不给任何覆盖入口
#endif
    }

    static void KamiInitPaths()
    {
#if DEBUG
        string envDir = Environment.GetEnvironmentVariable("ACE_DATA_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            try { Directory.CreateDirectory(envDir); KamiCfgPath = Path.Combine(envDir, "config.ini"); KamiCachePath = Path.Combine(envDir, "kami.ini"); return; } catch { }
        }
#endif
        string[] cands = new string[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ACE工具箱"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ACE工具箱"),
            Path.Combine(Path.GetTempPath(), "ACE工具箱")
        };
        string ok = null;
        foreach (string d in cands)
        {
            try { Directory.CreateDirectory(d); ok = d; break; }
            catch { }
        }
        if (ok == null) ok = Path.GetTempPath();
        KamiCfgPath = Path.Combine(ok, "config.ini");
        KamiCachePath = Path.Combine(ok, "kami.ini");
    }

    static Dictionary<string, string> ReadKv(string path)
    {
        var d = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(path)) return d;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        catch { }
        return d;
    }

    static void WriteKv(string path, Dictionary<string, string> d)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    // 机器码：MachineGuid + C 盘卷序列号 → SHA256 前 32 位
    static string MachineIdCache = null;

    // 取一条 WMI 属性（失败返回空串，不影响主流程）
    static int WmiBudgetStart = 0;   // 设备码计算开始时间（毫秒刻度）
    static string WmiProp(string wql, string prop)
    {
        // 用一个总预算：WMI 坏掉的机器上最多花 2.5 秒，剩下的直接跳过
        if (WmiBudgetStart != 0 && Environment.TickCount - WmiBudgetStart > 2500) return "";
        try
        {
            using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(wql))
            {
                try { searcher.Options.Timeout = TimeSpan.FromSeconds(2); } catch { }
                foreach (System.Management.ManagementBaseObject o in searcher.Get())
                {
                    object v = o[prop];
                    if (v == null) continue;
                    string str = v.ToString().Trim();
                    if (str.Length > 0 && str != "0" && str.Trim('-').Length > 0) return str;
                }
            }
        }
        catch { }
        return "";
    }

    // 读 MachineGuid：32 位进程直接读 HKLM\SOFTWARE 会被 WOW64 重定向到 WOW6432Node（那里没有这个键）
    // 所以显式指定 Registry64 视图；读不到再退回默认视图
    static string ReadMachineGuid()
    {
        foreach (RegistryView view in new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32, RegistryView.Default })
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (k == null) continue;
                    object v = k.GetValue("MachineGuid");
                    if (v != null)
                    {
                        string sv = v.ToString().Trim();
                        if (sv.Length > 0) return sv;
                    }
                }
            }
            catch { }
        }
        return "";
    }

    // 设备码：MachineGuid + 主板序列号 + 系统盘序列号 + C 盘卷序列号 → SHA256 前 32 位
    // 比只用 MachineGuid 稳：还原系统、换硬盘、重装都不轻易变
    static string MachineId()
    {
        if (MachineIdCache != null) return MachineIdCache;
        WmiBudgetStart = Environment.TickCount;     // 开始计预算

        string guid = ReadMachineGuid();
        if (string.IsNullOrEmpty(guid)) guid = WmiProp("SELECT UUID FROM Win32_ComputerSystemProduct", "UUID");

        string board = WmiProp("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
        if (board.Length == 0) board = WmiProp("SELECT SerialNumber FROM Win32_ComputerSystemProduct", "IdentifyingNumber");

        string disk = WmiProp("SELECT SerialNumber FROM Win32_DiskDrive", "SerialNumber");
        if (disk.Length == 0) disk = WmiProp("SELECT PNPDeviceID FROM Win32_DiskDrive", "PNPDeviceID");

        uint serial = 0;
        try
        {
            uint a, b, c;
            StringBuilder vol = new StringBuilder(64), fs = new StringBuilder(32);
            if (GetVolumeInformation("C:\\", vol, 64, out a, out b, out c, fs, 32)) serial = a;
        }
        catch { }

        string seed = guid + "|" + board + "|" + disk + "|" + serial.ToString("X8") + "|kami";
        using (SHA256 sha = SHA256.Create())
        {
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
            MachineIdCache = BitConverter.ToString(h).Replace("-", "").ToLowerInvariant().Substring(0, 32);
        }
        Log("设备码来源：guid=" + (guid.Length > 0) + " 主板=" + (board.Length > 0) + " 硬盘=" + (disk.Length > 0) + " 卷=" + (serial != 0));
        return MachineIdCache;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string root, StringBuilder vol, int volLen,
        out uint serial, out uint maxLen, out uint flags, StringBuilder fs, int fsLen);

    static string MapKamiErr(string err)
    {
        if (string.IsNullOrEmpty(err)) return "校验失败";
        if (err.StartsWith("NETWORK:")) return "网络连接失败，请检查网络后重试";
        switch (err)
        {
            case "BAD_FORMAT": return "卡密格式不正确";
            case "NOT_FOUND": return "卡密无效";
            case "DISABLED": return "卡密已被停用，请联系卖家";
            case "CARD_EXPIRED": return "卡密已作废";
            case "EXPIRED": return "会员已到期，请续费";
            case "USED_UP": return "次数已用完，请续费";
            case "DEVICE_LIMIT": return "设备数已满，请先在原设备解绑或联系客服";
            case "TOO_MANY": return "尝试过于频繁，请稍后再试";
            case "BUSY": return "服务器繁忙，请重试";

            case "TRIAL_LIMIT": return "当前网络今天领的试用次数用完了，明天再来或购买卡密";
            case "NETWORK": return "网络连接失败，请检查网络后重试";
            default: return "校验失败（" + err + "）";
        }
    }

    // ===== 服务端签名令牌（RSA-2048 / SHA-256）=====
    // 客户端只验签，不自己判断 type/remain/expireAt —— 改本地文件毫无意义
    const string TOK_N = "d26c1aef62616a94d5d1b7cf0f884b07f631e78869f73cbc0e56b44cab8cd47889ce2cbb91ae139f1a6e5ca1ff1a6911e9dcd33ff42e891ead1f31c12cd43a1af669363b7edb7c3da0ef64cda956adc940ec73b84c959e5d1084b5091845b556f6626820617a9bfd5a11e84527898fe1a15a64d92389d8eae13295f6c89f42ac07708d56c9daa8533f7528eec94622fde96055c991a24d9f4ad40acbec27296b885e542a69b171461453fe8404f398f2bb7070691208204f2e87524dde2bf7ef9587d37d10584caf46639802e759848f6a95134f2e2f88be8a627bc8bd335b543beb4f32a8d7134c3535fe0e41898b834c5c7c2b8b7bf0edd24dbc857ac9ad4f";
    const string TOK_E = "010001";

    static string Sha256Hex(string s)
    {
        using (SHA256 sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").ToLowerInvariant();
    }

    static byte[] B64UrlDecode(string s)
    {
        string t = (s ?? "").Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
            case 1: return null;
        }
        try { return Convert.FromBase64String(t); } catch { return null; }
    }

    static string _tokPathCache = null;

    // v3.1.6：目录不可写时退到临时目录（沙箱/权限受限环境也能记住令牌）
    static string TokenPath()
    {
        if (_tokPathCache != null) return _tokPathCache;
        string dir = Path.GetDirectoryName(KamiCfgPath);
        if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
        string p = Path.Combine(dir, "token.dat");
        try
        {
            string probe = Path.Combine(dir, ".wt_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            File.WriteAllText(probe, "1", Encoding.UTF8);
            File.Delete(probe);
        }
        catch
        {
            p = Path.Combine(Path.GetTempPath(), "ace_token.dat");
            Log("配置目录不可写，令牌改存：" + p);
        }
        _tokPathCache = p;
        return p;
    }

    static string LoadToken()
    {
        try { return File.Exists(TokenPath()) ? File.ReadAllText(TokenPath(), Encoding.UTF8).Trim() : ""; }
        catch { return ""; }
    }

    static void SaveToken(string token)
    {
        try { if (!string.IsNullOrEmpty(token)) File.WriteAllText(TokenPath(), token, Encoding.UTF8); }
        catch (Exception ex) { Log("SaveToken 失败：" + TokenPath() + " → " + ex.GetType().Name + " " + ex.Message); }
    }

    static bool InRange(string hex, int min, int max)
    {
        return !string.IsNullOrEmpty(hex) && hex.Length >= min * 2 && hex.Length <= max * 2;
    }

    // 验签 + 解析 payload。签名无效直接 false，不看内容
    static bool sY5hH8(string token, out string jCode, out string jType, out int jRemain,
                            out long jExpireAt, out long jExp, out string err)
    {
        jCode = ""; jType = ""; jRemain = -1; jExpireAt = 0; jExp = 0; err = "";
        try
        {
            if (string.IsNullOrEmpty(token)) { err = "没有令牌"; return false; }
            int dot = token.IndexOf('.');
            if (dot <= 0 || dot >= token.Length - 1) { err = "令牌格式错误"; return false; }
            string p64 = token.Substring(0, dot);
            byte[] sig = B64UrlDecode(token.Substring(dot + 1));
            byte[] payload = B64UrlDecode(p64);
            if (sig == null || payload == null) { err = "令牌编码错误"; return false; }

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                RSAParameters p = new RSAParameters();
                p.Modulus = HexToBytes(TOK_N);
                p.Exponent = HexToBytes(TOK_E);
                rsa.ImportParameters(p);
                // 服务端对 base64url 的 payload 字符串签名
                if (!rsa.VerifyData(Encoding.UTF8.GetBytes(p64), "SHA256", sig))
                {
                    err = "令牌签名无效（可能被篡改）";
                    return false;
                }
            }

            string json = Encoding.UTF8.GetString(payload);
            Match mc = Regex.Match(json, "\"code\"\\s*:\\s*\"([^\"]*)\"");
            Match mt = Regex.Match(json, "\"type\"\\s*:\\s*\"([^\"]*)\"");
            Match mr = Regex.Match(json, "\"remain\"\\s*:\\s*(-?\\d+)");
            Match me = Regex.Match(json, "\"expireAt\"\\s*:\\s*(\\d+)");
            Match mx = Regex.Match(json, "\"exp\"\\s*:\\s*(\\d+)");
            jCode = mc.Success ? mc.Groups[1].Value : "";
            jType = mt.Success ? mt.Groups[1].Value : "";
            jRemain = mr.Success ? int.Parse(mr.Groups[1].Value) : -1;
            jExpireAt = me.Success ? long.Parse(me.Groups[1].Value) : 0;
            jExp = mx.Success ? long.Parse(mx.Groups[1].Value) : 0;
            return true;
        }
        catch (Exception ex)
        {
            err = "令牌校验异常：" + ex.Message;
            return false;
        }
    }

    // 统一的 POST（返回响应正文；网络异常抛出去）
    static string KamiPost(string body)
    {
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); } catch { }
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(KamiUrl());
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Timeout = 10000;
        req.ReadWriteTimeout = 10000;
        req.ServicePoint.Expect100Continue = false;
        req.KeepAlive = false;
        byte[] data = Encoding.UTF8.GetBytes(body);
        req.ContentLength = data.Length;
        using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    // ===== 校验：联网优先；断网只认「签名有效 + 没过期」的令牌 =====
    static bool KamiVerify(string code, bool force, out string msg)
    {
        msg = "";
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { msg = "请输入卡密"; return false; }

        string device = MachineId();
        string dh = Sha256Hex(device).Substring(0, 32);

        // 先看本地令牌，决定这次用 use 还是 query（次数卡每次都要 use 扣次）
        string tok = LoadToken();
        string tType; string tCode; int tRemain; long tExpireAt; long tExp; string tErr;
        bool tokOk = sY5hH8(tok, out tCode, out tType, out tRemain, out tExpireAt, out tExp, out tErr);
        bool isCount = tokOk && tType == "count";
        bool sameCard = tokOk && tCode == code;

        string action = (isCount && sameCard) ? "use" : (sameCard ? "query" : "use");
        string body = "{\"action\":\"" + action + "\",\"code\":" + JsonStr(code)
                    + ",\"deviceId\":" + JsonStr(device) + ",\"appVersion\":\"1.0.0\"}";

        string text = null;
        try { text = KamiPost(body); }
        catch (Exception ex) { Log("卡密接口异常：" + ex.Message); text = null; }

        if (text != null)
        {
            Log("卡密接口返回：" + (text.Length > 300 ? text.Substring(0, 300) : text));
            if (!Regex.IsMatch(text, "\"ok\"\\s*:\\s*true"))
            {
                Match m = Regex.Match(text, "\"err\"\\s*:\\s*\"([^\"]*)\"");
                string errcode = m.Success ? m.Groups[1].Value : "";
                msg = MapKamiErr(errcode);
                Log("卡密校验失败：" + errcode);
                return false;
            }

            // 取服务端签发的令牌（唯一可信来源）
            Match mtok = Regex.Match(text, "\"token\"\\s*:\\s*\"([^\"]+)\"");
            if (mtok.Success)
            {
                string newTok = mtok.Groups[1].Value;
                string c2, ty2; int rem2; long ex2, xp2; string er2;
                if (sY5hH8(newTok, out c2, out ty2, out rem2, out ex2, out xp2, out er2))
                {
                    SaveToken(newTok);
                    try { var cfg0 = ReadKv(KamiCfgPath); cfg0["code"] = code; WriteKv(KamiCfgPath, cfg0); } catch { }
                    LicCode = c2; LicType = ty2; LicRemain = rem2; LicExpireAt = ex2;
                    msg = LicSummary();
                    Log("已保存服务端令牌：type=" + ty2 + " remain=" + rem2 + " exp=" + xp2);
                    return true;
                }
                Log("服务端令牌验签失败：" + er2);
                msg = "服务端令牌校验失败，请稍后重试";
                return false;
            }

            // 服务端没给令牌（旧版本函数）→ 按失败处理，避免降级
            msg = "服务端未签发授权令牌（请检查 KAMI_TOKEN_KEY 是否已配置）";
            Log(msg);
            return false;
        }

        // ==== 网络不通：只认本地令牌 ====
        if (tokOk && sameCard)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (tExp > now)
            {
                LicCode = tCode; LicType = tType; LicRemain = tRemain; LicExpireAt = tExpireAt;
                msg = "离线模式：" + LicSummary() + "，令牌有效至 " + FmtTime(tExp);
                Log("断网，按有效令牌放行，exp=" + tExp);
                return true;
            }
            msg = "网络不通，且本地令牌已于 " + FmtTime(tExp) + " 过期，请联网后重试";
            Log(msg);
            return false;
        }

        msg = "网络连接失败，请检查网络后重试";
        return false;
    }


    static string JsonStr(string s)
    {
        return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static string LicSummary()
    {
        string t = LicType == "count" ? "按次数"
                 : (LicType == "time" ? "按时长"
                 : (LicType == "forever" ? "永久"
                 : (LicType == "trial" ? "试用" : LicType)));
        string s = "卡密有效（" + t;
        if (LicType == "count" && LicRemain >= 0) s += "，剩余 " + LicRemain + " 次";
        if (LicExpireAt > 0) s += "，到期 " + FmtTime(LicExpireAt);
        return s + "）";
    }

    static string SavedCode()
    {
        var cfg = ReadKv(KamiCfgPath);
        string c = cfg.ContainsKey("code") ? cfg["code"] : "";
        return c.Equals("TRIAL") ? "" : c;
    }

    // 轻量 query：只回状态字段，用于"每次都复查"（不扣次数）
    static bool KamiQueryStatus(string code, out string status, out string type,
                                out int remain, out long expireAt, out string err)
    {
        status = ""; type = ""; remain = -1; expireAt = 0; err = "";
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) { err = "请输入卡密"; return false; }
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); } catch { }
        string body = "{\"action\":\"query\",\"code\":" + JsonStr(code) + ",\"deviceId\":" + JsonStr(MachineId()) + "}";
        string text;
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(KamiUrl());
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.ServicePoint.Expect100Continue = false;
            req.KeepAlive = false;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream st = req.GetRequestStream()) st.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log("复查接口异常：" + ex.Message);
            err = "NETWORK:" + ex.Message;
            return false;
        }
        if (!Regex.IsMatch(text, "\"ok\"\\s*:\\s*true"))
        {
            Match m = Regex.Match(text, "\"err\"\\s*:\\s*\"([^\"]*)\"");
            err = m.Success ? m.Groups[1].Value : "UNKNOWN";
            return false;
        }
        Match ms = Regex.Match(text, "\"status\"\\s*:\\s*\"([^\"]*)\"");
        Match mt = Regex.Match(text, "\"type\\s*\"\\s*:\\s*\"([^\"]*)\"");
        Match mr = Regex.Match(text, "\"remain\"\\s*:\\s*(\\d+)");
        Match me = Regex.Match(text, "\"expireAt\"\\s*:\\s*(\\d+)");
        status = ms.Success ? ms.Groups[1].Value : "";
        type = mt.Success ? mt.Groups[1].Value : "";
        remain = mr.Success ? int.Parse(mr.Groups[1].Value) : -1;
        expireAt = me.Success ? long.Parse(me.Groups[1].Value) : 0;
        // 服务端说不可用就直接失败
        if (status == "disabled" || status == "expired" || status == "usedup")
        {
            err = status == "disabled" ? "DISABLED" : (status == "expired" ? "EXPIRED" : "USED_UP");
            return false;
        }
        return true;
    }

    // ---------- 查询（action=query，不扣次数）----------
    static string MapStatus(string st)
    {
        switch (st)
        {
            case "unused": return "未使用";
            case "active": return "已激活";
            case "disabled": return "已禁用";
            case "expired": return "已过期";
            case "usedup": return "已用完";
            default: return st;
        }
    }

    static string MapType(string t)
    {
        switch (t)
        {
            case "count": return "按次数";
            case "time": return "按时长";
            case "forever": return "永久";
            default: return t;
        }
    }

    static string FmtTime(long ms)
    {
        if (ms <= 0) return "无";
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        catch { return ms.ToString(); }
    }

    static bool KamiQuery(string code, out string summary, out string err)
    {
        summary = ""; err = "";
        code = (code ?? "").Trim();
        if (code.Length == 0) { err = "请输入卡密"; return false; }
        code = code.ToUpperInvariant();

        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); } catch { }
        string body = "{\"action\":\"query\",\"code\":" + JsonStr(code) + ",\"deviceId\":" + JsonStr(MachineId()) + "}";
        string text;
        try
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(KamiUrl());
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 12000;
            req.ReadWriteTimeout = 12000;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (Stream s2 = req.GetRequestStream()) s2.Write(data, 0, data.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log("查询接口异常：" + ex.Message);
            err = MapKamiErr("NETWORK:" + ex.Message);
            return false;
        }
        Log("查询返回：" + (text.Length > 300 ? text.Substring(0, 300) : text));

        if (!Regex.IsMatch(text, "\"ok\"\\s*:\\s*true"))
        {
            Match m = Regex.Match(text, "\"err\"\\s*:\\s*\"([^\"]*)\"");
            err = MapKamiErr(m.Success ? m.Groups[1].Value : "");
            return false;
        }

        Match ms = Regex.Match(text, "\"status\"\\s*:\\s*\"([^\"]*)\"");
        Match mt = Regex.Match(text, "\"type\"\\s*:\\s*\"([^\"]*)\"");
        Match mr = Regex.Match(text, "\"remain\"\\s*:\\s*(\\d+)");
        Match mc = Regex.Match(text, "\"count\"\\s*:\\s*(\\d+)");
        Match mu = Regex.Match(text, "\"used\"\\s*:\\s*(\\d+)");
        Match me = Regex.Match(text, "\"expireAt\"\\s*:\\s*(\\d+)");
        Match ma = Regex.Match(text, "\"activatedAt\"\\s*:\\s*(\\d+)");
        Match mn = Regex.Match(text, "\"serverTime\"\\s*:\\s*(\\d+)");

        string status = ms.Success ? ms.Groups[1].Value : "";
        string type = mt.Success ? mt.Groups[1].Value : "";
        int remain = mr.Success ? int.Parse(mr.Groups[1].Value) : -1;
        int count = mc.Success ? int.Parse(mc.Groups[1].Value) : -1;
        int used = mu.Success ? int.Parse(mu.Groups[1].Value) : -1;
        long exp = me.Success ? long.Parse(me.Groups[1].Value) : 0;
        long act = ma.Success ? long.Parse(ma.Groups[1].Value) : 0;
        long srv = mn.Success ? long.Parse(mn.Groups[1].Value) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sb = new StringBuilder();
        sb.AppendLine("状态：" + MapStatus(status));
        sb.AppendLine("类型：" + MapType(type));
        if (type == "count")
            sb.AppendLine("次数：共 " + count + " 次，已用 " + used + " 次，剩余 " + remain + " 次");
        sb.AppendLine("到期：" + (exp > 0 ? FmtTime(exp) + "（" + (exp > srv ? "还剩 " + Math.Max(0, (exp - srv) / 86400000L) + " 天" : "已过期") + "）" : "无限制"));
        if (act > 0) sb.AppendLine("激活时间：" + FmtTime(act));
        if (status == "unused") sb.Append("（这张卡还没激活过）");

        summary = sb.ToString().TrimEnd();
        return true;
    }

    // ---------- 卡密登录窗（自适应尺寸，任何 DPI 都不切字）----------
    static bool rT9gG2()
    {
        string prefill = SavedCode();
        string result = null;
        bool ok = false;

        using (Form f = new Form())
        {
            f.Text = "ACE 工具箱 · 卡密验证";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Font = new Font("微软雅黑", 11f);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.ClientSize = new Size(560, 470);

            int W = 512, L = 24, Y = 16;

            Label t = new Label();
            t.AutoSize = true;
            t.Text = "ACE 工具箱";
            t.Font = new Font("微软雅黑", 17f, FontStyle.Bold);
            t.Location = new Point(L, Y);
            f.Controls.Add(t);
            Y = t.Bottom + 10;

            Label sub = new Label();
            sub.AutoSize = false;
            sub.Text = "输入卡密后点「验证并进入」，卡密会记住，下次自动填好。";
            sub.ForeColor = Color.DimGray;
            sub.Font = new Font("微软雅黑", 10f);
            sub.SetBounds(L, Y, W, 26);
            f.Controls.Add(sub);
            Y = sub.Bottom + 8;

            Label lc = new Label();
            lc.AutoSize = true;
            lc.Text = "卡密";
            lc.Location = new Point(L, Y + 6);
            f.Controls.Add(lc);

            TextBox tb = new TextBox();
            tb.Font = new Font("微软雅黑", 12f);
            tb.SetBounds(L + 62, Y, W - 62, 34);
            tb.Text = prefill;
            f.Controls.Add(tb);
            Y = tb.Bottom + 8;

            Label dev = new Label();
            dev.AutoSize = true;
            dev.Text = "本机设备码：" + MachineId();
            dev.ForeColor = Color.DimGray;
            dev.Font = new Font("微软雅黑", 9.5f);
            dev.Location = new Point(L, Y + 4);
            f.Controls.Add(dev);
            Y = dev.Bottom + 8;

            TextBox info = new TextBox();
            info.Multiline = true;
            info.ReadOnly = true;
            info.ScrollBars = ScrollBars.Vertical;
            info.Font = new Font("微软雅黑", 10.5f);
            info.SetBounds(L, Y, W, 150);
            info.BackColor = Color.White;
            string trialHint = "";
            info.Text = (prefill.Length > 0
                ? "卡密已自动填入。可以先点「查询」看剩余次数/到期时间，再点「验证并进入」。"
                : "没有卡密？请联系卖家购买次数卡 / 时长卡。") + trialHint;
            f.Controls.Add(info);
            Y = info.Bottom + 6;

            Label st = new Label();
            st.AutoSize = false;
            st.SetBounds(L, Y, W, 24);
            st.ForeColor = Color.DimGray;
            st.Font = new Font("微软雅黑", 9.5f);
            f.Controls.Add(st);
            Y = st.Bottom + 10;

            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true;
            row.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            row.WrapContents = false;
            row.FlowDirection = FlowDirection.LeftToRight;
            row.Margin = Padding.Empty;
            row.Location = new Point(L, Y);
            f.Controls.Add(row);

            Button bQuery = new Button();
            bQuery.Text = "查询";
            bQuery.Font = new Font("微软雅黑", 11.5f);
            bQuery.AutoSize = true;
            bQuery.Padding = new Padding(14, 6, 14, 6);
            bQuery.Margin = new Padding(0, 0, 10, 0);
            row.Controls.Add(bQuery);


            Button bOk = new Button();
            bOk.Text = "验证并进入";
            bOk.Font = new Font("微软雅黑", 11.5f, FontStyle.Bold);
            bOk.AutoSize = true;
            bOk.Padding = new Padding(18, 6, 18, 6);
            bOk.Margin = new Padding(0, 0, 10, 0);
            row.Controls.Add(bOk);

            Button bExit = new Button();
            bExit.Text = "退出";
            bExit.Font = new Font("微软雅黑", 11.5f);
            bExit.AutoSize = true;
            bExit.Padding = new Padding(14, 6, 14, 6);
            bExit.Margin = new Padding(0);
            row.Controls.Add(bExit);

            Y = row.Location.Y + row.PreferredSize.Height + 12;

            // 自适应窗口高度
            f.ClientSize = new Size(Math.Max(560, row.Right + L + 16), Math.Max(430, Y + 16));

            Action<bool> setBusy = (b) => { bQuery.Enabled = !b; bOk.Enabled = !b; bExit.Enabled = !b; };

            bQuery.Click += (s2, e2) =>
            {
                string code = tb.Text.Trim();
                if (code.Length == 0) { st.ForeColor = Color.Firebrick; st.Text = "请先输入卡密"; return; }
                setBusy(true);
                st.ForeColor = Color.DimGray; st.Text = "正在查询…";
                info.Text = "";
                Thread th = new Thread(() =>
                {
                    string sm, er;
                    bool r = KamiQuery(code, out sm, out er);
                    try
                    {
                        f.BeginInvoke((Action)(() =>
                        {
                            if (r) { info.Text = sm; st.ForeColor = Color.SeaGreen; st.Text = "查询完成（不消耗次数）"; }
                            else { info.Text = "查询失败：" + er; st.ForeColor = Color.Firebrick; st.Text = er; }
                            setBusy(false);
                        }));
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
            };

            bOk.Click += (s2, e2) =>
            {
                string code = tb.Text.Trim();
                if (code.Length == 0) { st.ForeColor = Color.Firebrick; st.Text = "请先输入卡密"; return; }
                setBusy(true);
                st.ForeColor = Color.DimGray; st.Text = "正在验证…";
                info.Text = "";
                Thread th = new Thread(() =>
                {
                    string m;
                    bool r = KamiVerify(code, false, out m);
                    try
                    {
                        f.BeginInvoke((Action)(() =>
                        {
                            if (r)
                            {
                                result = code; ok = true;
                                info.Text = m;
                                st.ForeColor = Color.SeaGreen;
                                st.Text = "验证通过，正在进入…";
                                f.DialogResult = DialogResult.OK;
                                f.Close();
                            }
                            else
                            {
                                info.Text = "验证失败：" + m;
                                st.ForeColor = Color.Firebrick;
                                st.Text = m;
                                setBusy(false);
                            }
                        }));
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
            };

            bExit.Click += (s2, e2) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
            f.AcceptButton = bOk;
            f.CancelButton = bExit;
            f.ShowDialog();
        }

        if (ok)
        {
            if (!result.ToUpperInvariant().Equals("TRIAL"))
            {
                var cfg = ReadKv(KamiCfgPath);
                cfg["code"] = result.ToUpperInvariant();
                WriteKv(KamiCfgPath, cfg);
            }
        }
        return ok;
    }

    static bool VerifyLicenseCli()
    {
        string code = CliKey ?? SavedCode();
        string m;
        if (!KamiVerify(code, false, out m))
        {
            Console.WriteLine("卡密校验失败：" + m);
            Console.WriteLine("（用 /key=你的卡密 指定，或先运行图形版登录一次）");
            return false;
        }
        Console.WriteLine("卡密：" + m);
        return true;
    }

    [STAThread]
    // ================== 启动更新（不需要卡密，公开接口） ==================

    static int ParseNum(string t)
    {
        if (string.IsNullOrEmpty(t)) return 0;
        var sb = new StringBuilder();
        foreach (char c in t) { if (char.IsDigit(c)) sb.Append(c); else break; }
        int v;
        return int.TryParse(sb.ToString(), out v) ? v : 0;
    }

    static int CmpVer(string a, string b)
    {
        string[] x = (a ?? "0").Split('.');
        string[] y = (b ?? "0").Split('.');
        int n = Math.Max(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            int xi = ParseNum(i < x.Length ? x[i] : "0");
            int yi = ParseNum(i < y.Length ? y[i] : "0");
            if (xi != yi) return xi < yi ? -1 : 1;
        }
        return 0;
    }

    // 公开版本接口：{"action":"version","app":"ace"} → version/size/sha256/url
    static string PublicVersion(out long size, out string sha, out string url)
    {
        size = 0; sha = ""; url = "";
        try
        {
            string body = "{\"action\":\"version\",\"app\":\"ace\"}";
            var req = (HttpWebRequest)WebRequest.Create(KAMI_URL);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.ServicePoint.Expect100Continue = false;
            req.KeepAlive = false;
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            byte[] bb = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bb.Length;
            using (var st = req.GetRequestStream()) st.Write(bb, 0, bb.Length);
            string resp;
            using (var r = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                resp = sr.ReadToEnd();
            var m = System.Text.RegularExpressions.Regex.Match(resp, "\"version\"\\s*:\\s*\"([^\"]*)\"");
            string ver = m.Success ? m.Groups[1].Value : "";
            m = System.Text.RegularExpressions.Regex.Match(resp, "\"size\"\\s*:\\s*(\\d+)");
            if (m.Success) long.TryParse(m.Groups[1].Value, out size);
            m = System.Text.RegularExpressions.Regex.Match(resp, "\"sha256\"\\s*:\\s*\"([^\"]*)\"");
            sha = m.Success ? m.Groups[1].Value : "";
            m = System.Text.RegularExpressions.Regex.Match(resp, "\"url\"\\s*:\\s*\"([^\"]*)\"");
            url = m.Success ? m.Groups[1].Value.Replace("\\/", "/") : "";
            return ver;
        }
        catch (Exception ex)
        {
            Log("公开版本接口失败：" + ex.Message);
            return "";
        }
    }

    // 替换 + 改名 + 隐藏重启（copy 重试法，不用 tasklist）
    static bool ReplaceAndRun(string ver, string newExe, string workDir, string zipPath, out string err)
    {
        err = "";
        try
        {
            string cur = System.Windows.Forms.Application.ExecutablePath;
            string dir = Path.GetDirectoryName(cur);
            string newName = Path.Combine(dir, "ACE工具箱v" + ver + ".exe");
            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            string bat = Path.Combine(Path.GetTempPath(), "aceupd_" + tag + ".bat");
            string vbs = Path.Combine(Path.GetTempPath(), "aceupd_" + tag + ".vbs");

            var lines = new System.Collections.Generic.List<string>();
            lines.Add("@echo off");
            lines.Add("setlocal");
            lines.Add("set TARGET=" + cur);
            lines.Add("set NEW=" + newExe);
            lines.Add("set /a N=0");
            lines.Add(":retry");
            lines.Add("copy /y \"%NEW%\" \"%TARGET%\" >nul 2>&1");
            lines.Add("if not errorlevel 1 goto started");
            lines.Add("set /a N+=1");
            lines.Add("if %N% GEQ 60 goto giveup");
            lines.Add("ping 127.0.0.1 -n 2 >nul");
            lines.Add("goto retry");
            lines.Add(":giveup");
            lines.Add("exit /b 1");
            lines.Add(":started");
            lines.Add("if /i not \"%TARGET%\"==\"" + newName + "\" (");
            lines.Add("  move /y \"%TARGET%\" \"" + newName + "\" >nul 2>&1");
            lines.Add("  if exist \"" + newName + "\" set \"TARGET=" + newName + "\"");
            lines.Add(")");
            lines.Add("start \"\" /D \"" + dir + "\" \"%TARGET%\"");
            lines.Add("del /q \"" + zipPath + "\" >nul 2>&1");
            lines.Add("rd /s /q \"" + workDir + "\" >nul 2>&1");
            lines.Add("del /q \"%~f0\" >nul 2>&1");
            File.WriteAllText(bat, string.Join("\r\n", lines.ToArray()) + "\r\n", Encoding.GetEncoding(936));
            File.WriteAllText(vbs,
                "CreateObject(\"WScript.Shell\").Run \"cmd /c \"\"\" & \"" + bat + "\" & \"\"\"\", 0, False\r\n",
                Encoding.GetEncoding(936));

            var psi = new System.Diagnostics.ProcessStartInfo("wscript.exe", "\"" + vbs + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            err = "启动替换脚本失败：" + ex.Message;
            Log(err);
            return false;
        }
    }

    class UpdateForm : Form
    {
        public bool Succeeded = false;
        readonly string ver, sha, url;
        readonly long size;
        ProgressBar pb;
        Label lb, ls;
        Button bGo, bQuit;
        public string errText = "";

        public UpdateForm(string _ver, long _size, string _sha, string _url)
        {
            ver = _ver; size = _size; sha = _sha; url = _url;
            Text = "ACE 工具箱 更新";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("微软雅黑", 10f);
            ClientSize = new Size(560, 250);
            BackColor = Color.WhiteSmoke;

            Label t = new Label();
            t.AutoSize = true;
            t.Text = "发现新版本  v" + ver;
            t.Font = new Font("微软雅黑", 15f, FontStyle.Bold);
            t.ForeColor = Color.FromArgb(30, 111, 217);
            t.Location = new Point(22, 18);
            Controls.Add(t);

            Label sub = new Label();
            sub.AutoSize = true;
            sub.Text = "当前版本 v" + APP_VER + "  →  最新 v" + ver + "（必须更新）";
            sub.ForeColor = Color.Firebrick;
            sub.Location = new Point(24, 56);
            Controls.Add(sub);

            Label sz = new Label();
            sz.AutoSize = true;
            sz.Text = "更新包大小：" + (size / 1048576.0).ToString("0.0") + " MB";
            sz.ForeColor = Color.DimGray;
            sz.Location = new Point(24, 82);
            Controls.Add(sz);

            pb = new ProgressBar();
            pb.SetBounds(24, 108, 512, 20);
            pb.Minimum = 0; pb.Maximum = 100;
            Controls.Add(pb);

            ls = new Label();
            ls.AutoSize = false;
            ls.SetBounds(24, 132, 512, 22);
            ls.Text = "准备下载…";
            ls.ForeColor = Color.DimGray;
            ls.Font = new Font("Consolas", 9f);
            Controls.Add(ls);

            bGo = new Button();
            bGo.Text = "开始更新";
            bGo.SetBounds(24, 170, 110, 34);
            bGo.Click += (s, e) => StartWork();
            Controls.Add(bGo);

            bQuit = new Button();
            bQuit.Text = "退出";
            bQuit.SetBounds(146, 170, 80, 34);
            bQuit.Click += (s, e) => { Close(); };
            Controls.Add(bQuit);
            CancelButton = bQuit;

            Load += (s, e) => { BeginInvoke((Action)StartWork); };
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        void StartWork()
        {
            bGo.Enabled = false;
            Thread th = new Thread(() =>
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "aceupd_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                try
                {
                    Log("开始下载更新：" + url.Substring(0, Math.Min(70, url.Length)) + " size=" + size);
                    long total = size > 0 ? size : 0;
                    long pos = 0;
                    int tries = 0;
                    int reconnects = 0;
                    int noProgress = 0;
                    const long CHUNK = 4L * 1024 * 1024;   // 主动分片：不等服务器自己断流
                    var swTotal = System.Diagnostics.Stopwatch.StartNew();
                    // ★ 原来 KeepAlive=false：每断一次都要重新 TCP+TLS 握手，界面上就是"停一会才继续下"
                    try { ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8); } catch { }

                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
                    {
                        while (total <= 0 || pos < total)
                        {
                            long start = pos;
                            long end = total > 0 ? Math.Min(pos + CHUNK - 1, total - 1) : pos + CHUNK - 1;
                            long before = pos;
                            bool earlyEof = false;
                            try
                            {
                                var req = (HttpWebRequest)WebRequest.Create(url);
                                req.UserAgent = "ACE-Toolbox-updater";
                                req.Timeout = 12000;              // 建连/等响应最多 12s（原来 30s，卡住要等半分钟）
                                req.ReadWriteTimeout = 60000;     // 单片 4MB 的读超时
                                req.KeepAlive = true;             // ★ 复用连接，续传不再重新握手
                                // int 版 AddRange 在 .NET 4.0 上最稳；更新包目前 20MB 左右，远小于 2GB
                                if (total > 0)
                                {
                                    int rs = (int)Math.Min(start, int.MaxValue);
                                    int re = (int)Math.Min(end, int.MaxValue);
                                    if (re >= rs) req.AddRange(rs, re);
                                }

                                using (var resp = (HttpWebResponse)req.GetResponse())
                                using (var st = resp.GetResponseStream())
                                {
                                    bool partial = (resp.StatusCode == HttpStatusCode.PartialContent);
                                    if (start > 0 && !partial)
                                    {
                                        // 服务器忽略 Range、返回 200 全量：从 0 重写
                                        Log("服务器不支持 Range，从头重下");
                                        fs.SetLength(0); fs.Position = 0; pos = 0; start = 0;
                                        end = total > 0 ? Math.Min(CHUNK - 1, total - 1) : CHUNK - 1;
                                    }
                                    if (total <= 0 && resp.ContentLength > 0) total = pos + resp.ContentLength;
                                    byte[] buf = new byte[262144];   // 256KB，少一点系统调用
                                    int n;
                                    var sw = System.Diagnostics.Stopwatch.StartNew();
                                    long lastT = 0, lastG = pos;
                                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        fs.Write(buf, 0, n); pos += n;
                                        if (total > 0 && sw.ElapsedMilliseconds - lastT > 150)
                                        {
                                            double sp = (pos - lastG) / Math.Max(0.001, (sw.ElapsedMilliseconds - lastT) / 1000.0);
                                            lastT = sw.ElapsedMilliseconds; lastG = pos;
                                            long g2 = pos, t2 = total; double sp2 = sp;
                                            int pct = (int)Math.Min(99, g2 * 100 / t2);
                                            long left = sp2 > 1024 ? (long)((t2 - g2) / sp2) : 0;
                                            Ui(() =>
                                            {
                                                pb.Value = Math.Min(100, pct);
                                                ls.Text = string.Format("{0}%   {1:0.0} / {2:0.0} MB   {3:0.0} MB/s   剩余 {4}s",
                                                    pct, g2 / 1048576.0, t2 / 1048576.0, sp2 / 1048576.0, left);
                                            });
                                        }
                                    }
                                    earlyEof = (total > 0 && pos < total && pos <= end);
                                }
                                if (reconnects > 0)
                                    Log("续传 #" + reconnects + "：本次 " + start + " → " + pos
                                        + (earlyEof ? "（服务器提前断流）" : "") + "，累计 " + swTotal.ElapsedMilliseconds + "ms");
                                reconnects++;
                                if (pos == before)
                                {
                                    noProgress++;
                                    if (noProgress > 8) throw new Exception("连续 8 次请求没有读到任何字节");
                                    Thread.Sleep(200);
                                }
                                else { noProgress = 0; tries = 0; }   // 读到数据就清零错误计数
                                if (total > 0 && pos >= total) break;
                                if (total <= 0) break;
                                // 流提前结束不算异常：不 sleep，直接下一片（原来这里会白等 600ms）
                            }
                            catch (Exception dex)
                            {
                                tries++;
                                Log("下载异常（第 " + tries + " 次，已下 " + pos + " 字节）：" + dex.Message);
                                if (tries > 25 || dex.Message.IndexOf("连续 8 次") >= 0) throw;
                                Thread.Sleep(Math.Min(200 * tries, 1000));   // 200/400/600/800/1000ms 退避
                                Ui(() => { ls.Text = "网络中断，正在续传… 已下载 " + (pos / 1048576.0).ToString("0.0") + " MB"; });
                            }
                        }
                    }
                    Log("下载结束：pos=" + pos + " total=" + total
                        + " 请求数=" + reconnects + " 用时 " + swTotal.ElapsedMilliseconds + "ms");
                    string got2 = Sha256File2(tmp);
                    if (!string.IsNullOrEmpty(sha) && got2 != sha.ToLowerInvariant())
                    {
                        errText = "更新包校验失败（sha256 不一致）";
                        Ui(() => { ls.Text = errText + "  —— 点「重试」"; bGo.Enabled = true; bGo.Text = "重试"; });
                        return;
                    }
                    Ui(() => { pb.Value = 100; ls.Text = "正在解包…"; });
                    string work = Path.Combine(Path.GetTempPath(),
                        "aceupd_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(work);
                    string pick = null;
                    bool isZip = false;
                    using (var f0 = File.OpenRead(tmp)) { int b1 = f0.ReadByte(), b2 = f0.ReadByte(); isZip = (b1 == 'P' && b2 == 'K'); }
                    if (!isZip)
                    {
                        pick = Path.Combine(work, Path.GetFileName(System.Windows.Forms.Application.ExecutablePath));
                        File.Copy(tmp, pick, true);
                    }
                    else
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(tmp, work);
                        string any = null;
                        foreach (string f in Directory.GetFiles(work, "*.exe", SearchOption.AllDirectories))
                        {
                            if (any == null) any = f;
                        }
                        pick = any;
                    }
                    if (pick == null) {
                        errText = "更新包里没有 exe";
                        Ui(() => { ls.Text = errText + "  —— 点「重试」"; bGo.Enabled = true; bGo.Text = "重试"; });
                        return;
                    }

                    Ui(() => { ls.Text = "正在替换文件…"; });
                    string rerr;
                    if (!ReplaceAndRun(ver, pick, work, tmp, out rerr))
                    {
                        errText = rerr;
                        Ui(() => { ls.Text = errText + "  —— 点「重试」"; bGo.Enabled = true; bGo.Text = "重试"; });
                        return;
                    }
                    Log("已交给替换脚本，退出");
                    Succeeded = true;
                    Ui(() => { ls.Text = "更新包已就绪，正在重启完成替换…"; });
                    Thread.Sleep(1200);
                    Ui(() => { Application.Exit(); });
                }
                catch (Exception ex)
                {
                    errText = "更新失败：" + ex.Message;
                    Log(errText);
                    Ui(() => { ls.Text = errText + "  —— 点「重试」"; bGo.Enabled = true; bGo.Text = "重试"; });
                }
            });
            th.IsBackground = true;
            th.Start();
        }
    }

    // 启动时：低于线上版本 → 强制更新（返回 true 表示本次不要继续启动）
    static bool StartupUpdate()
    {
        if (CliMode || DryUpdate || DryMode) return false;
        long size; string sha, url;
        string ver = PublicVersion(out size, out sha, out url);
        if (string.IsNullOrEmpty(ver) || string.IsNullOrEmpty(url)) return false;
        if (CmpVer(APP_VER, ver) >= 0) return false;

        Log("启动更新检查：本地 " + APP_VER + " → 线上 " + ver);
        using (var f = new UpdateForm(ver, size, sha, url))
        {
            f.ShowDialog();
            if (f.Succeeded) return true;
            if (!string.IsNullOrEmpty(f.errText)) return true;   // 失败也停在这，不让旧版继续用
            return true;   // 强制更新：用户关窗 → 直接退出
        }
    }


    // ================= 防护层 v1.1.2 =================
    // 关键字符串加密：运行时解出，静态扫描看不到明文 URL
    static byte[] _gkCache;   // 懒加载：避免和 KAMI_URL 的静态初始化顺序打架
    static byte[] _GK()
    {
        if (_gkCache == null)
            _gkCache = new byte[] { 0x56, 0xE6, 0x52, 0x6B, 0x16, 0xAA, 0xC5, 0x80, 0xF1, 0xB1, 0x3D, 0xD6, 0x82, 0xB9, 0xB1, 0xAD };
        return _gkCache;
    }


    static string _D(string b64)
    {
        try
        {
            byte[] raw = Convert.FromBase64String(b64);
            byte[] k = _GK();
            for (int i = 0; i < raw.Length; i++) raw[i] ^= k[i % k.Length];
            string r = Encoding.UTF8.GetString(raw);
            Array.Clear(raw, 0, raw.Length);
            return r;
        }
        catch { return ""; }
    }

    // 反调试 / 反分析：命中就静默退出（不弹窗，别给分析者线索）
    static void GuardStartup()
    {
#if !DEBUG
        try
        {
            if (Debugger.IsAttached) Environment.Exit(0);
            if (NativeDebugger()) Environment.Exit(0);
            if (DebugPortPresent()) Environment.Exit(0);   // ProcessDebugPort / DebugObjectHandle
            if (DebugFlagsPresent()) Environment.Exit(0);  // ProcessDebugFlags == 0 才正常
            if (SuspiciousTool()) Environment.Exit(0);
        }
        catch { }
#endif
    }

    // 8 字节缓冲（x64 下 ULONG_PTR 是 8 字节，用 int 会写越界）
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    static extern int NtQIP(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    static bool DebugPortPresent()
    {
        IntPtr b = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(b, 0);
            int rl;
            if (NtQIP(GetCurrentProcess(), 7, b, 8, out rl) == 0)
                return Marshal.ReadInt64(b) != 0;
        }
        catch { }
        finally { Marshal.FreeHGlobal(b); }
        return false;
    }

    static bool DebugFlagsPresent()
    {
        IntPtr b = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(b, 1);
            int rl;
            if (NtQIP(GetCurrentProcess(), 0x1F, b, 8, out rl) == 0)
                return Marshal.ReadInt64(b) == 0;      // 0 = 正在被调试
        }
        catch { }
        finally { Marshal.FreeHGlobal(b); }
        return false;
    }

    [DllImport("kernel32.dll")]
    static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll")]
    static extern bool CheckRemoteDebuggerPresent(IntPtr h, ref bool pb);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    static bool NativeDebugger()
    {
        try
        {
            if (IsDebuggerPresent()) return true;
            bool remote = false;
            if (CheckRemoteDebuggerPresent(GetCurrentProcess(), ref remote) && remote) return true;
        }
        catch { }
        return false;
    }

    // 已知分析工具：短名用精确匹配（避免误杀），长名才用包含匹配
    static bool SuspiciousTool()
    {
        try
        {
            // 精确匹配（进程名，不含 .exe，全小写）
            string[] exact = new string[] {
                "dnspy", "dnspy-x86", "ilspy", "ilspycmd", "de4dot", "de4dot-x64",
                "x64dbg", "x32dbg", "x96dbg", "ollydbg", "windbg", "windbgx",
                "ida", "ida64", "ida32", "idag", "idag64", "idat", "idat64",
                "processhacker", "procmon", "procmon64", "procexp", "procexp64",
                "scylla", "scylla_x64", "cheatengine-x86_64", "cheatengine",
                "wx64dbg", "dbgview", "httptoolkit"
            };
            // 包含匹配（名字独特，不会撞到正常软件）
            string[] contains = new string[] {
                "processhacker", "httpdebugger", "netreflector", "simpleassembly",
                "dnspy", "de4dot", "ollydbg", "x64dbg", "x32dbg", "cheatengine",
                "ilspy", "fiddler", "charles", "mitmproxy", "wireshark", "httptoolkit"
            };

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string n = (p.ProcessName ?? "").ToLowerInvariant();
                    for (int i = 0; i < exact.Length; i++)
                        if (n == exact[i]) return true;
                    for (int i = 0; i < contains.Length; i++)
                        if (n.Contains(contains[i])) return true;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return false;
    }

    // =================================================

    static void Main(string[] args)
    {
        // ★ v3.0.2：日志先初始化，否则篡改记录写不出来
        LogPath = Path.Combine(Path.GetTempPath(), "ACE工具箱.log");
        try { File.WriteAllText(LogPath, "", Encoding.UTF8); } catch { }

        GuardStartup();          // ★ 反调试 / 反分析（命中直接退出）
        if (!zQ4mR7("startup")) return;   // ★ 关键方法 IL 完整性（失败直接退出）
        try
        {
            string self = System.Windows.Forms.Application.ExecutablePath;
            if (!string.IsNullOrEmpty(self) && File.Exists(self + ".old")) File.Delete(self + ".old");
        }
        catch { }
        // ★ v1.1.4：开机第一件事把上次残留的功能脚本删掉，等登录后再现拉
        try
        {
            WipeFile(OpsPath());
            WipeFile(OpsCachePath());
        }
        catch { }
        AppDomain.CurrentDomain.UnhandledException += (s0, e0) =>
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ACE_crash.txt"), DateTime.Now.ToString("HH:mm:ss ") + e0.ExceptionObject + "\r\n"); } catch { }
        };
        // ★ 必须开 TLS 1.2：.NET Framework 4.x 默认只有 SSL3/TLS1.0，
        //   腾讯云拒绝 → "未能创建 SSL/TLS 安全通道"
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072
                                                  | (SecurityProtocolType)12288;
        }
        catch
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
        }
        try { ServicePointManager.Expect100Continue = false; } catch { }
        try { ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true; } catch { }

        KamiInitPaths();

        if (args != null && args.Length > 0)
        {
            CliMode = true;
#if DEBUG
            foreach (string a in args) if (a.Equals("/dry", StringComparison.OrdinalIgnoreCase)) DryMode = true;
            foreach (string a in args) if (a.Equals("/dryupdate", StringComparison.OrdinalIgnoreCase)) DryUpdate = true;
#endif
            foreach (string a in args) if (a.Equals("/v", StringComparison.OrdinalIgnoreCase)) CliVerbose = true;
            foreach (string a in args)
                if (a.StartsWith("/key=", StringComparison.OrdinalIgnoreCase)) CliKey = a.Substring(5).Trim();
#if DEBUG
            foreach (string a in args)
                if (a.StartsWith("/api=", StringComparison.OrdinalIgnoreCase)) CliApi = a.Substring(5).Trim();
#endif
            string act = "status";
            foreach (string a in args) if (!a.StartsWith("/")) { act = a.ToLowerInvariant(); break; }

            // 命令行同样要过卡密
#if NOLICENSE
            LicType = "forever"; LicCode = "LOCAL";
#else
#if ILTEST
            if (act != "query" && act != "ilcheck" && act != "ilprefix" && !VerifyLicenseCli()) { Environment.ExitCode = 2; return; }
#else
            if (act != "query" && !VerifyLicenseCli()) { Environment.ExitCode = 2; return; }
#endif
            if (act != "query")
            {
                string fe1;
                if (nA8wB5(out fe1)) Console.WriteLine("功能脚本已下发（内存）");
                else Console.WriteLine("功能脚本下发失败：" + fe1);
            }
#endif

            switch (act)
            {
                case "query":
                    {
                        string qs, qe;
                        if (KamiQuery(CliKey ?? SavedCode(), out qs, out qe)) Console.WriteLine(qs);
                        else { Console.WriteLine("查询失败：" + qe); Environment.ExitCode = 3; }
                        break;
                    }
#if DEBUG || ILTEST
                case "ilcheck":
                    {
                        Console.WriteLine("kW7bT3 = " + kW7bT3());
                        for (int i = 0; i < ILN.Length; i++)
                        {
                            System.Reflection.MethodInfo m = typeof(AceTool).GetMethod(ILN[i],
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Public);
                            byte[] il = m.GetMethodBody().GetILAsByteArray();
                            Console.WriteLine("  " + ILN[i] + " len=" + il.Length +
                                " norm=" + (int)nD3sF6(NormalizeIL(il)) + " want=" + ILH[i]);
                        }
                        break;
                    }
#endif
#if DEBUG || ILTEST
                case "ilprefix":
                    {
                        string[] want = new string[] { "Main", "tB9gL2", "rT9gG2", "sY5hH8" };
                        foreach (string w in want)
                        {
                            System.Reflection.MethodInfo m = typeof(AceTool).GetMethod(w,
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                                System.Reflection.BindingFlags.Public);
                            byte[] il = m.GetMethodBody().GetILAsByteArray();
                            StringBuilder sb = new StringBuilder();
                            int n = Math.Min(24, il.Length);
                            for (int i = 0; i < n; i++) sb.Append(il[i].ToString("X2"));
                            Console.WriteLine(w + " " + sb.ToString());
                        }
                        break;
                    }
#endif
#if DEBUG
                case "selftest":
                    {
                        Console.WriteLine("=== 自检（用假服务 ACETEST-*，不加载任何驱动文件）===");
                        Action<string, string, string> mk = (nm, ty, st) =>
                        {
                            RunCmd("sc", "delete \"" + nm + "\"");
                            RunCmd("sc", "create \"" + nm + "\" type= " + ty + " start= " + st +
                                   " binPath= \"C:\\Windows\\System32\\drivers\\nonexist.sys\"");
                        };
                        Action<string> rm = (nm) => { RunCmd("sc", "delete \"" + nm + "\""); };
                        int t;

                        // ---- 测试1：存档 + 精确还原 ----
                        Console.WriteLine("\n[1] 存档 → 禁用 → 按存档还原");
                        mk("ACETEST-BOOT", "kernel", "boot");
                        Console.WriteLine("   建服务 start=boot, 读回 Start=" + ReadSvcInfo("ACETEST-BOOT", out t));
                        SaveSvcStart("ACETEST-BOOT");
                        RunCmd("sc", "config \"ACETEST-BOOT\" start= disabled");
                        Console.WriteLine("   禁用后 Start=" + ReadSvcInfo("ACETEST-BOOT", out t) + "（期望 4）");
                        int r1 = RestoreSvcFromBackup();
                        Console.WriteLine("   还原后 Start=" + ReadSvcInfo("ACETEST-BOOT", out t) + "（期望 0）restored=" + r1);
                        rm("ACETEST-BOOT");

                        // ---- 测试2：FixDisabledOnly 只修 disabled，且 boot 驱动还原成 boot ----
                        Console.WriteLine("\n[2] FixDisabledOnly：BOOT 驱动→boot，普通驱动→demand，普通服务→auto");
                        mk("ACETEST-BOOT", "kernel", "disabled");
                        mk("ACETEST-CORE", "kernel", "disabled");
                        Console.WriteLine("   修复前 BOOT=" + ReadSvcInfo("ACETEST-BOOT", out t) +
                                          " CORE=" + ReadSvcInfo("ACETEST-CORE", out t));
                        FixDisabledOnly();
                        Console.WriteLine("   修复后 BOOT=" + ReadSvcInfo("ACETEST-BOOT", out t) + "（期望 0）" +
                                          " CORE=" + ReadSvcInfo("ACETEST-CORE", out t) + "（期望 3）");
                        rm("ACETEST-BOOT"); rm("ACETEST-CORE");

                        // ---- 测试3：FixBootDrivers 把 demand 的 BOOT 驱动修回 boot ----
                        Console.WriteLine("\n[3] FixBootDrivers：把被改成 demand 的 BOOT 驱动修回 boot");
                        mk("ACETEST-BOOT", "kernel", "demand");
                        mk("ACETEST-CORE", "kernel", "demand");
                        Console.WriteLine("   修复前 BOOT=" + ReadSvcInfo("ACETEST-BOOT", out t) +
                                          " CORE=" + ReadSvcInfo("ACETEST-CORE", out t));
                        FixBootDrivers();
                        Console.WriteLine("   修复后 BOOT=" + ReadSvcInfo("ACETEST-BOOT", out t) + "（期望 0）" +
                                          " CORE=" + ReadSvcInfo("ACETEST-CORE", out t) + "（期望 3 不变）");
                        rm("ACETEST-BOOT"); rm("ACETEST-CORE");

                        // ---- 测试4：NtUnloadDriver 对不存在的驱动返回错误（不崩） ----
                        Console.WriteLine("\n[4] NtUnloadDriver 假服务（应返回非 0，不崩）");
                        mk("ACETEST-UNL", "kernel", "demand");
                        Console.WriteLine("   NtUnload(ACETEST-UNL) = " + NtUnload("ACETEST-UNL"));
                        rm("ACETEST-UNL");

                        Console.WriteLine("\n=== 自检结束 ===");
                        break;
                    }
#endif
#if DEBUG
                case "svcbak":
                    {
                        Console.WriteLine("== 存档测试 ==");
                        foreach (string n in new string[] { "ACE-BOOT", "EasyAntiCheat_EOS" })
                        {
                            int ty; int st = ReadSvcInfo(n, out ty);
                            Console.WriteLine(n + " Start=" + st + " (" + StartToWord(st) + ") Type=" + ty);
                            SaveSvcStart(n);
                        }
                        Console.WriteLine("存档文件: " + SvcBakPath());
                        if (File.Exists(SvcBakPath()))
                            foreach (string l in File.ReadAllLines(SvcBakPath(), Encoding.UTF8)) Console.WriteLine("   " + l);
                        break;
                    }
                case "svcrestore":
                    {
                        Console.WriteLine("== 还原测试（只读判断，不改）==");
                        RestoreAceStartType();
                        break;
                    }
#endif
#if DEBUG
                case "cloudclose":
                    {
                        lC9rW1();
                        break;
                    }
#endif
#if DEBUG
                case "fdtest":
                    {
                        string t = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fd_test.sys");
                        File.WriteAllText(t, "x");
                        bool ok1 = ForceDeleteFile(t);            // 没有占用时
                        Console.WriteLine("forceDelete(no-holder)=" + ok1 + " exists=" + File.Exists(t));
                        File.WriteAllText(t, "x");
                        FileStream fh = File.Open(t, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        bool ok2 = ForceDeleteFile(t);            // 别的进程允许 share delete 时
                        fh.Close();
                        Console.WriteLine("forceDelete(share-delete)=" + ok2 + " exists=" + File.Exists(t));
                        break;
                    }
#endif
#if DEBUG
                case "deepscan":
                    {
                        Console.WriteLine("== 只读扫描（不删任何东西）==");
                        var sv = cR8sT5();
                        Console.WriteLine("服务 " + sv.Count + " 个：");
                        foreach (var x in sv) Console.WriteLine("   " + x[0] + "  |  " + (x.Length > 1 ? x[1] : ""));
                        foreach (string d in new string[] { ACE_DIR, ACE_DIR86, ACE_DATA })
                            Console.WriteLine("目录 " + (Directory.Exists(d) ? "存在" : "无") + " : " + d);
                        string drv = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32\\drivers");
                        int n = 0;
                        try
                        {
                            foreach (string f in Directory.GetFiles(drv, "*.sys"))
                            {
                                string fn = Path.GetFileName(f).ToUpperInvariant();
                                if (fn.StartsWith("ACE") || fn.Contains("SGUARD") || fn.Contains("ANTICHEAT"))
                                { n++; Console.WriteLine("驱动 " + f); }
                            }
                        }
                        catch (Exception ex) { Console.WriteLine("驱动扫描异常 " + ex.Message); }
                        Console.WriteLine("驱动命中 " + n + " 个");
                        break;
                    }
#endif
#if DEBUG
                case "schtest":
                    {
                        string t = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "acelimit_test.sys");
                        File.WriteAllText(t, "x");
                        bool ok = ScheduleDeleteOnReboot(t);
                        Console.WriteLine("schedule=" + ok + " pending=" + PendingReboot.Count + " file=" + t);
                        break;
                    }
#endif
#if DEBUG
                case "limitkeep":
                    {
                        aF4hT9();
                        Thread.Sleep(20000);
                        Console.WriteLine("keep done");
                        break;
                    }
                case "disclaim":
                    {
                        IsGui = true;
                        UI = new Form();
                        bool r0 = AskLimitDisclaimer();
                        Console.WriteLine("disclaimer result=" + r0);
                        break;
                    }
#endif
                case "limitace": aF4hT9(); break;
                case "restoreace": eG7jU2(); break;
                case "install": InstallAce(); break;
                case "game":
                    {
                        var gr = RunningGameProcs();
                        Console.WriteLine(gr.Count == 0 ? "游戏进程：未运行" : ("游戏进程：运行中 " + string.Join("、", gr.ToArray())));
                        break;
                    }
                case "fetch":
                    {
                        string fe;
                        if (nA8wB5(out fe)) Console.WriteLine("功能脚本已就绪：" + OpsPath());
                        else { Console.WriteLine("获取失败：" + fe); Environment.ExitCode = 5; }
                        break;
                    }
                case "payload":
                    { string pd = FindAceDir() ?? Path.Combine(Path.GetTempPath(), "ACEPayloadTest"); Directory.CreateDirectory(pd); ExtractPayload(pd); Console.WriteLine("已释放到 " + pd); break; }
                case "start": StartAce(); break;
                case "close": CloseAce(); break;
                case "uninstall": case "remove": UninstallAce(); break;
                default: RefreshStatus(); break;
            }
            return;
        }

        try
        {
            var wid = System.Security.Principal.WindowsIdentity.GetCurrent();
            IsAdmin = new System.Security.Principal.WindowsPrincipal(wid).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ★ 启动即检查更新（公开接口，不需要卡密）；低于线上版本就强制更新
#if !OFFLINE
        if (StartupUpdate()) return;
#endif

        // 卡密验证（上次用过的卡密会自动填入输入框）
#if NOLICENSE
        LicType = "forever"; LicCode = "LOCAL";   // 破解模拟版：免卡密
#else
        if (!rT9gG2()) return;
#endif

        // ★ v1.1.4：登录成功 → 立刻从云端下发全部功能脚本（只进内存）
        try
        {
            string fe0;
            if (nA8wB5(out fe0))
            {
                Log("登录后已下发功能脚本（内存）");
                try { if (oX3cC1()) Log("功能脚本指令已解析"); } catch { }
            }
            else
            {
                Log("登录后下发功能脚本失败：" + fe0);
                qW1eD9("功能脚本下发失败：" + fe0);
                MessageBox.Show("授权服务不可用：" + fe0 + "\r\n\r\n本软件需要联网下发功能指令，程序将退出。",
                                "ACE 工具箱", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // v3.0.7：直接杀进程，不走 Environment.Exit（后者可被异常处理器截住）
                TerminateSelf(4);
            }
        }
        catch (Exception fex) { Log("登录后下发异常：" + fex.Message); }

        UI = new Form();
        UI.Text = "ACE 工具箱";
        UI.Font = new Font("微软雅黑", 10f);
        UI.AutoScaleMode = AutoScaleMode.Dpi;
        UI.ClientSize = new Size(830, 728);
        UI.MinimumSize = new Size(620, 560);
        UI.StartPosition = FormStartPosition.CenterScreen;

        // ===== 系统状态 =====
        GroupBox grpState = new GroupBox();
        grpState.Text = "系统状态";
        grpState.SetBounds(12, 8, 676, 322);
        grpState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        UI.Controls.Add(grpState);

        LvStatus = new ListView();
        LvStatus.View = View.Details;
        LvStatus.FullRowSelect = true;
        LvStatus.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        LvStatus.MultiSelect = false;
        LvStatus.HideSelection = false;
        LvStatus.SetBounds(12, 24, 652, 288);
        LvStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        LvStatus.Columns.Add("项目", 300);
        LvStatus.Columns.Add("状态", 500);
        Action fitCols = () =>
        {
            int w = LvStatus.ClientSize.Width;
            if (w < 120) return;
            int c0 = (int)(w * 0.36);
            LvStatus.Columns[0].Width = c0;
            LvStatus.Columns[1].Width = Math.Max(120, w - c0 - 4);
        };
        LvStatus.Resize += (s2, e2) => fitCols();
        grpState.Controls.Add(LvStatus);

        // ===== 操作 =====
        GroupBox grpAct = new GroupBox();
        grpAct.Text = "操作";
        grpAct.SetBounds(12, 336, 676, 92);
        grpAct.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        UI.Controls.Add(grpAct);

        BtnInstall = MakeActButton(grpAct, "安装 ACE");
        BtnInstall.Click += (s, e) => RunAsync(InstallAce);

        BtnStart = MakeActButton(grpAct, "启动 / 激活");
        BtnStart.Click += (s, e) => RunAsync(StartAce);

        BtnClose = MakeActButton(grpAct, "静默关闭");
        BtnClose.Click += (s, e) => RunAsync(CloseAce);

        BtnUninst = MakeActButton(grpAct, "卸载");
        BtnUninst.Click += (s, e) =>
        {
            if (MessageBox.Show("将删除 ACE 的目录、服务与注册表项，卸载后需要重新安装才能用。\r\n\r\n确定继续？",
                "确认卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.OK)
                RunAsync(UninstallAce);
        };

        BtnRefresh = MakeActButton(grpAct, "刷新状态");
        BtnRefresh.Click += (s, e) => { Thread t = new Thread(RefreshStatus); t.IsBackground = true; t.Start(); };

        BtnForceDel = MakeActButton(grpAct, "关闭 ACE");
        BtnForceDel.ForeColor = Color.DarkRed;
        BtnForceDel.Click += (s, e) => RunAsync(lC9rW1);

        // ---- ACE 进程限制（v2.0.0）----
        BtnLimitAce = MakeActButton(grpAct, "限制 ACE");
        BtnLimitAce.ForeColor = Color.Firebrick;
        BtnLimitAce.Click += (s, e) =>
        {
            if (AceLimited)
            {
                MessageBox.Show("ACE 已处于限制状态。\r\n如需还原请点「恢复 ACE」。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!AskLimitDisclaimer()) { Result("已取消（未同意免责声明）"); return; }
            RunAsync(aF4hT9);
        };

        BtnRestoreAce = MakeActButton(grpAct, "恢复 ACE");
        BtnRestoreAce.Click += (s, e) => RunAsync(eG7jU2);

        // 用 FlowLayoutPanel 自动排；允许换行，DPI 变化时自动折到下一行
        FlowLayoutPanel rowAct = new FlowLayoutPanel();
        rowAct.AutoSize = true;
        rowAct.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        rowAct.WrapContents = true;
        rowAct.FlowDirection = FlowDirection.LeftToRight;
        rowAct.Location = new Point(14, 22);
        rowAct.Margin = Padding.Empty;
        grpAct.Controls.Add(rowAct);
        BtnClose.Visible = false;   // v2.0.6：静默关闭改为云端 disable 动作，按钮隐藏
        foreach (Button b in new Button[] { BtnInstall, BtnStart, BtnUninst, BtnForceDel, BtnRefresh })
        {
            b.Padding = new Padding(10, 4, 10, 4);
            b.Margin = new Padding(0, 0, 8, 6);
            rowAct.Controls.Add(b);
        }

        // ---- ACE 限制：单独一行（避免和上面几个按钮挤在一起）----
        FlowLayoutPanel rowAce = new FlowLayoutPanel();
        rowAce.AutoSize = true;
        rowAce.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        rowAce.WrapContents = false;
        rowAce.FlowDirection = FlowDirection.LeftToRight;
        rowAce.Location = new Point(14, 58);
        rowAce.Margin = Padding.Empty;
        grpAct.Controls.Add(rowAce);
        foreach (Button b in new Button[] { BtnLimitAce, BtnRestoreAce })
        {
            b.Padding = new Padding(10, 4, 10, 4);
            b.Margin = new Padding(0, 0, 8, 0);
            rowAce.Controls.Add(b);
        }

        LblAceTip = new Label();
        LblAceTip.AutoSize = false;
        LblAceTip.ForeColor = Color.DimGray;
        LblAceTip.Text = "限制 ACE：把 ACE 相关进程设为最低优先级并只跑最后一个 CPU 核；可随时点「恢复 ACE」还原。";
        rowAce.Controls.Add(LblAceTip);
        LblAceTip.Margin = new Padding(12, 10, 0, 0);
        LblAceTip.AutoSize = true;

        // 分组高度跟着按钮行数走，永不被截
        bool syncing = false;
        Action syncAct = () =>
        {
            if (syncing) return;
            syncing = true;
            try
            {
                int w = Math.Max(200, grpAct.ClientSize.Width - 30);
                if (rowAct.MaximumSize.Width != w) rowAct.MaximumSize = new Size(w, 0);
                rowAce.Top = rowAct.Bottom + 6;
                LblHint.Top = rowAce.Bottom + 4;
                int need = LblHint.Bottom + 8;
                int d = need - grpAct.Height;
                if (d != 0 && grpRstHTopAdjust != null)
                {
                    grpAct.Height = need;
                    grpRstHTopAdjust(d);
                }
            }
            finally { syncing = false; }
        };
        grpAct.Resize += (s2, e2) => syncAct();

        SyncAct = syncAct;

        LblHint = new Label();
        LblHint.AutoSize = false;
        LblHint.SetBounds(16, 66, 640, 26);
        LblHint.Text = IsAdmin
            ? "当前权限：管理员，所有操作可用。"
            : "当前权限：普通用户 —— 启动/关闭/卸载需要管理员，请右键选择“以管理员身份运行”。";
        LblHint.ForeColor = IsAdmin ? Color.DimGray : Color.Firebrick;
        grpAct.Controls.Add(LblHint);

        // ===== 结果 =====
        GroupBox grpRst = new GroupBox();
        grpRstHTopAdjust = (d) => { grpRst.Top += d; grpRst.Height -= d; };
        grpRst.Text = "结果";
        grpRst.SetBounds(12, 434, 676, 168);
        grpRst.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        UI.Controls.Add(grpRst);

        TxtResult = new TextBox();
        TxtResult.Multiline = true;
        TxtResult.ReadOnly = true;
        TxtResult.ScrollBars = ScrollBars.Vertical;
        TxtResult.BackColor = Color.White;
        TxtResult.BorderStyle = BorderStyle.FixedSingle;
        TxtResult.SetBounds(12, 22, 652, 134);
        TxtResult.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        grpRst.Controls.Add(TxtResult);

        if (!IsAdmin) BtnInstall.Enabled = BtnStart.Enabled = BtnClose.Enabled = BtnUninst.Enabled = false;

        LblLic = new Label();
        LblLic.AutoSize = false;
        LblLic.Text = "我的授权：" + LicSummary() + "　｜　客服 QQ：392543339";
        LblLic.ForeColor = Color.SteelBlue;
        LblLic.Font = new Font("微软雅黑", 9.5f);
        LblLic.SetBounds(14, 588, 800, 22);
        LblLic.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        UI.Controls.Add(LblLic);

        LblFoot = new Label();
        Label lblFoot = LblFoot;
        lblFoot.Text = "日志文件：" + LogPath;
        lblFoot.ForeColor = Color.DimGray;
        lblFoot.Font = new Font("微软雅黑", 8f);
        lblFoot.SetBounds(14, 612, 800, 18);
        lblFoot.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        UI.Controls.Add(lblFoot);

        try
        {
            System.Windows.Forms.Timer ik = new System.Windows.Forms.Timer();
            ik.Interval = 15000;
            ik.Tick += (s3, e3) => { try { pX2vN9("timer"); } catch { } };
            ik.Start();
        }
        catch { }

        UI.Shown += (s, e) =>
        {
            // 句柄已创建，这里写结果框才生效
            Result("卡密：" + LicSummary());
            if (!IsAdmin)
                Result("提示：当前不是管理员，只能查看状态。请右键本程序 → 以管理员身份运行。");
            else if (FindAceDir() == null)
                Result("提示：未检测到 ACE。点「安装 ACE」会调用游戏目录里的 ACE-Setup64.exe 装上（需要管理员）。");
            try { fitCols(); } catch { }

            try { if (SyncAct != null) SyncAct(); } catch { }

            // 底部两行按窗口高度贴底，结果框把位置让出来
            try
            {
                int bh = UI.ClientSize.Height;
                if (LblLic != null) LblLic.Top = bh - 46;
                if (LblFoot != null) LblFoot.Top = bh - 22;
                GroupBox g = null;
                foreach (Control cc in UI.Controls)
                    if (cc is GroupBox && ((GroupBox)cc).Text == "结果") g = (GroupBox)cc;
                if (g != null) g.Height = Math.Max(90, bh - 56 - g.Top);
            }
            catch { }

            Thread t = new Thread(() => { try { if (string.IsNullOrEmpty(GameDir)) DiscoverAceSourceDir(); } catch { } RefreshStatus(); }); t.IsBackground = true; t.Start();
        };

        // 试用倒计时 / 到期提醒（每分钟）
        System.Windows.Forms.Timer tmr = new System.Windows.Forms.Timer();
        tmr.Interval = 60000;
        tmr.Tick += (s2, e2) => TickLicense();
        tmr.Start();
        TickLicense();

        // 心跳复查（每 10 分钟问一次服务器，query 不扣次）：被禁用/到期就当场退出
        System.Windows.Forms.Timer hb = new System.Windows.Forms.Timer();
        hb.Interval = 10 * 60 * 1000;
        hb.Tick += (s2, e2) => Heartbeat();
        hb.Start();

        IsGui = true;
        Application.Run(UI);
    }

    // 心跳：每 10 分钟复查一次卡密（query，不扣次数）
    static bool HeartbeatBusy = false;
    static void Heartbeat()
    {
        if (HeartbeatBusy) return;
        HeartbeatBusy = true;
        try
        {
            if (string.IsNullOrEmpty(LicCode)) return;
            string st, ty, er;
            int rem; long exp;
            if (!KamiQueryStatus(LicCode, out st, out ty, out rem, out exp, out er))
            {
                if (er.StartsWith("NETWORK:"))
                {
                    NetFailCount++;
                    Log("心跳网络失败第 " + NetFailCount + " 次：" + er);
                    if (NetFailCount >= 3)       // v3.0.0：连续 3 次 → 降级，不再无限放行
                    {
                        qW1eD9("心跳连续失败 " + NetFailCount + " 次");
                        Result("✗ 授权服务连不上，功能已停用，请检查网络后重启软件");
                    }
                    return;
                }
                NetFailCount = 0;
                Log("心跳复查未通过：" + er);
                string m = MapKamiErr(er);
                try
                {
                    UI.BeginInvoke((Action)(() =>
                    {
                        MessageBox.Show("卡密已失效：" + m + "\r\n\r\n程序将退出。\r\n如需继续使用请联系客服 QQ 392543339",
                            "授权失效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Application.Exit();
                    }));
                }
                catch { }
                return;
            }
            LicType = string.IsNullOrEmpty(ty) ? LicType : ty;
            LicRemain = rem;
            LicExpireAt = exp;
            Log("心跳复查通过：" + st);
            try
            {
                UI.BeginInvoke((Action)(() =>
                {
                    if (LblLic != null) LblLic.Text = "我的授权：" + LicSummary() + "　｜　客服 QQ：392543339";
                }));
            }
            catch { }
        }
        finally { HeartbeatBusy = false; }
    }

    static Button MakeActButton(Control parent, string text)
    {
        Button b = new Button();
        b.Text = text;
        b.AutoSize = true;
        b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(16, 6, 16, 6);
        b.MinimumSize = new Size(90, 34);
        parent.Controls.Add(b);
        return b;
    }

    static bool trialWarned = false;

    static long LicNow()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long off = 0;
        var c = ReadKv(KamiCachePath);
        if (c.ContainsKey("offset")) long.TryParse(c["offset"], out off);
        return now + off;
    }

    static void TickLicense()
    {
        try
        {
            if (LicType == "trial" && LicExpireAt > 0)
            {
                long left = LicExpireAt - LicNow();
                if (left <= 0)
                {
                    if (LblLic != null) { LblLic.Text = "我的授权：试用已结束　｜　购买卡密请联系 QQ 392543339"; LblLic.ForeColor = Color.Firebrick; }
                    if (!trialWarned)
                    {
                        trialWarned = true;
                        MessageBox.Show("试用已经结束。\r\n\r\n购买卡密请联系 QQ 392543339",
                            "试用结束", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }
                long h = left / 3600000L, m = (left % 3600000L) / 60000L;
                if (LblLic != null)
                {
                    LblLic.Text = "我的授权：试用中，剩余 " + h + " 小时 " + m + " 分　｜　客服 QQ：392543339";
                    LblLic.ForeColor = left <= 30 * 60000 ? Color.OrangeRed : Color.SteelBlue;
                }
                if (!trialWarned && left <= 30 * 60000)
                {
                    trialWarned = true;
                    MessageBox.Show("试用还剩 " + m + " 分钟。\r\n\r\n购买卡密请联系 QQ 392543339",
                        "试用即将结束", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (LblLic != null && string.IsNullOrEmpty(LblLic.Text))
            {
                LblLic.Text = "我的授权：" + LicSummary() + "　｜　客服 QQ：392543339";
            }
        }
        catch { }
    }

    static void RunAsync(Action a)
    {
        BtnInstall.Enabled = BtnStart.Enabled = BtnClose.Enabled = BtnUninst.Enabled = BtnRefresh.Enabled = false;
        BtnLimitAce.Enabled = false; BtnRestoreAce.Enabled = false; BtnForceDel.Enabled = false;
        Thread t = new Thread(() =>
        {
            try { a(); }
            catch (Exception ex) { Log("异常：" + ex.Message); Result("✗ 出错：" + ex.Message); }
            finally
            {
                try
                {
                    UI.BeginInvoke((Action)(() =>
                    {
                        BtnRefresh.Enabled = true;
                        if (IsAdmin) BtnInstall.Enabled = true;   // 其余按钮由 RefreshStatus 按装没装来决定
                        BtnLimitAce.Enabled = true;               // ★ 两个 ACE 按钮始终可用
                        BtnRestoreAce.Enabled = true;
                        BtnForceDel.Enabled = true;
                        Thread t3 = new Thread(RefreshStatus); t3.IsBackground = true; t3.Start();
                    }));
                }
                catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
    }
}












































