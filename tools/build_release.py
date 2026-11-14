# -*- coding: utf-8 -*-
"""build_release.py —— 一键构建：编译 → 算 IL hash → 注入 → 再编译 → 校验
用法： python build_release.py [输出exe名]
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CSC = r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
SRC = os.path.join(HERE, "ace_tool2.cs")
REFS = ["/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll",
        "/reference:System.IO.Compression.dll", "/reference:System.IO.Compression.FileSystem.dll",
        "/reference:System.Management.dll"]
METHODS = ["Main", "rT9gG2", "tB9gL2", "sY5hH8",
           "pX2vN9", "bN7uA3", "aF4hT9", "eG7jU2", "iO3pV6",
           "mR2wZ5", "fT8kQ1", "mU6vZ7", "oX3cC1", "cK5iE8",
           "zQ4mR7", "nD3sF6", "rY3tG6", "sP9oH2", "wX8lL9", "bP6qR2", "cR8sT5", "bX9vK3"]


def compile_to(out_name):
    cmd = [CSC, "/nologo", "/target:winexe", "/platform:x64", "/out:" + out_name,
           "/optimize+", "/win32icon:" + os.path.join(HERE, "app.ico"),
           "/win32manifest:" + os.path.join(HERE, "ace.manifest"),
           "/resource:" + os.path.join(HERE, "ace_payload.zip"),
           "/resource:" + os.path.join(HERE, "aceguard.dll")] + REFS + [SRC]
    r = subprocess.run(cmd, capture_output=True, text=True, errors="replace")
    errs = [l for l in (r.stdout or "").splitlines() if " error " in l]
    if r.returncode != 0 or errs:
        print("编译失败：")
        print("\n".join(errs[:8]) or (r.stdout or "")[:500])
        return False
    return True


def hashes(exe):
    args = [os.path.join(HERE, "ilhash.exe"), exe, "AceTool"] + METHODS
    r = subprocess.run(args, capture_output=True, text=True, errors="replace")
    out = {}
    for line in (r.stdout or "").splitlines():
        p = line.split()
        if len(p) >= 3 and p[0] in METHODS:
            out[p[0]] = p[2]
    return out


def inject(vals):
    s = open(SRC, encoding="utf-8").read()
    pairs = [("H0", "Main"), ("H1", "rT9gG2"), ("H2", "tB9gL2"),
             ("H3", "sY5hH8"), ("H4", "pX2vN9"), ("H5", "bN7uA3"),
             ("H6", "aF4hT9"), ("H7", "eG7jU2"), ("H8", "iO3pV6"),
             ("H9", "mR2wZ5"), ("H10", "fT8kQ1"),
             ("H11", "mU6vZ7"), ("H12", "oX3cC1"), ("H13", "cK5iE8"),
             ("H14", "zQ4mR7"), ("H15", "nD3sF6"),
             ("H16", "rY3tG6"), ("H17", "sP9oH2"), ("H18", "wX8lL9"), ("H19", "bP6qR2"), ("H20", "cR8sT5"), ("H21", "bX9vK3")]
    orig = s
    for name, m in pairs:
        s = re.sub(r"const int %s = -?\d+;" % name,
                   "const int %s = %s;" % (name, vals[m]), s, count=1)
    if s == orig:
        # 值本来就一致（未改动关键方法）—— 视为成功
        print("   （hash 常量已是目标值，无需改动）")
        return True
    open(SRC, "w", encoding="utf-8").write(s)
    return True


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "_inner_gui.exe"
    print("== 1/4 编译（占位 hash）==")
    if not compile_to(out):
        return 1
    print("== 2/4 计算 IL hash ==")
    h1 = hashes(out)
    for m in METHODS:
        print("   %-16s %s" % (m, h1.get(m, "缺失")))
    if len(h1) != len(METHODS):
        print("!! hash 计算不全")
        return 1
    print("== 3/4 注入并重编 ==")
    if not inject(h1):
        return 1
    if not compile_to(out):
        return 1
    print("== 4/4 校验稳定性 ==")
    h2 = hashes(out)
    ok = all(h1[m] == h2.get(m) for m in METHODS)
    for m in METHODS:
        print("   %-16s 注入 %-12s 实算 %-12s %s" %
              (m, h1[m], h2.get(m), "OK" if h1[m] == h2.get(m) else "**不一致**"))
    if not ok:
        print("\n!! hash 不稳定，请检查（若有 #if 分支改变了关键方法 IL，需要重新收敛）")
        return 1
    print("\n完成：%s" % out)
    return 0


if __name__ == "__main__":
    sys.exit(main())