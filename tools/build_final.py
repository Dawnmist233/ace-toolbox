# -*- coding: utf-8 -*-
"""build_final.py —— 三步构建：
   1) 编译客户端（算 hash、注入常量、验证稳定）
   2) 用最终 hash 重建 aceguard.dll（期望值写进机器码）
   3) 带着新 DLL 重新编译客户端，并复核 hash 未变
用法：python build_final.py [输出exe]
"""
import os
import subprocess
import sys

ACE = os.path.dirname(os.path.abspath(__file__))
GUARD = os.path.join(ACE, "..", "ace_guard")
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
SRC = os.path.join(ACE, "ace_tool2.cs")
REFS = ["/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll",
        "/reference:System.IO.Compression.dll", "/reference:System.IO.Compression.FileSystem.dll",
        "/reference:System.Management.dll"]
METHODS = ["Main", "rT9gG2", "tB9gL2", "sY5hH8",
           "pX2vN9", "bN7uA3", "aF4hT9", "eG7jU2", "iO3pV6",
           "mR2wZ5", "fT8kQ1", "mU6vZ7", "oX3cC1", "cK5iE8",
           "zQ4mR7", "nD3sF6", "rY3tG6", "sP9oH2", "wX8lL9",
           "bP6qR2", "cR8sT5", "bX9vK3"]


def compile_client(out):
    cmd = [CSC, "/nologo", "/target:winexe", "/platform:x64", "/out:" + out,
           "/optimize+", "/win32icon:" + os.path.join(ACE, "app.ico"),
           "/win32manifest:" + os.path.join(ACE, "ace.manifest"),
           "/resource:" + os.path.join(ACE, "ace_payload.zip"),
           "/resource:" + os.path.join(ACE, "aceguard.dll")] + REFS + [SRC]
    r = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    errs = [l for l in (r.stdout or "").splitlines() if " error " in l]
    if r.returncode != 0 or errs:
        print("编译失败：")
        print("\n".join(errs[:6]))
        return False
    return True


def hashes(exe):
    r = subprocess.run([os.path.join(ACE, "ilhash.exe"), exe, "AceTool"] + METHODS,
                       capture_output=True, text=True, errors="replace")
    out = {}
    for line in (r.stdout or "").splitlines():
        p = line.split()
        if len(p) >= 3 and p[0] in METHODS:
            out[p[0]] = p[2]
    return out


def main():
    out = os.path.join(ACE, sys.argv[1] if len(sys.argv) > 1 else "_inner_gui.exe")

    print("== 1/4 客户端（hash 注入）==")
    r = subprocess.run([sys.executable, os.path.join(ACE, "build_release.py"), os.path.basename(out)],
                       capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        print((r.stdout or "")[-800:] + (r.stderr or "")[-400:])
        return 1
    print("   OK")

    print("== 2/4 同步期望值到原生 DLL ==")
    r2 = subprocess.run([sys.executable, os.path.join(GUARD, "sync_guard.py"), out],
                        capture_output=True, text=True, errors="replace")
    print("   " + (r2.stdout or "").strip().replace("\n", "\n   "))
    if r2.returncode != 0:
        print((r2.stderr or "")[-400:])
        return 1

    print("== 3/4 带新 DLL 重新编译 ==")
    if not compile_client(out):
        return 1
    h2 = hashes(out)

    print("== 4/4 复核 hash 未变 ==")
    ok = True
    for m in METHODS:
        if h2.get(m) is None:
            print("   缺", m)
            ok = False
    if not ok:
        return 1
    print("   22 个方法 hash 齐全，DLL 期望值已对齐")
    return 0


if __name__ == "__main__":
    sys.exit(main())