// ilhash.cs —— 计算关键方法 IL 的 FNV-1a 哈希（构建期用）
using System;
using System.Reflection;
using System.Text;

class ILHash
{
    static uint Fnv(byte[] b)
    {
        uint h = 2166136261u;
        for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619u; }
        return h;
    }

    static byte[] Norm(byte[] il)
    {
        byte[] o = new byte[il.Length];
        int i = 0;
        while (i < il.Length)
        {
            if (i + 3 < il.Length)
            {
                byte hi = il[i + 3];
                if (hi == 0x02 || hi == 0x04 || hi == 0x06 || hi == 0x0A ||
                    hi == 0x11 || hi == 0x1B || hi == 0x2B || hi == 0x70 ||
                    hi == 0x71 || hi == 0x1C || hi == 0x20)
                { o[i] = 0; o[i+1] = 0; o[i+2] = 0; o[i+3] = hi; i += 4; continue; }
            }
            o[i] = il[i]; i++;
        }
        return o;
    }

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法: ilhash.exe <assembly> <typeName> <method1> [method2 ...]");
            return 2;
        }
        Assembly asm;
        try { asm = Assembly.Load(System.IO.File.ReadAllBytes(args[0])); }
        catch (Exception e) { Console.WriteLine("LOAD_FAIL " + e.Message); return 3; }

        Type t = asm.GetType(args[1]);
        if (t == null) { Console.WriteLine("TYPE_NOT_FOUND " + args[1]); return 4; }

        for (int i = 2; i < args.Length; i++)
        {
            MethodInfo m = t.GetMethod(args[i],
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { Console.WriteLine("MISSING " + args[i]); continue; }
            MethodBody mb = m.GetMethodBody();
            if (mb == null) { Console.WriteLine("NOBODY " + args[i]); continue; }
            byte[] il = mb.GetILAsByteArray();
            StringBuilder hx = new StringBuilder();
            int hn = Math.Min(24, il.Length);
            for (int k = 0; k < hn; k++) hx.Append(il[k].ToString("X2"));
            Console.WriteLine(args[i] + " " + il.Length + " " + (int)Fnv(il) + " " + hx.ToString());
        }
        return 0;
    }
}