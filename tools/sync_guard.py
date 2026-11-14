# -*- coding: utf-8 -*-
"""sync_guard.py —— 从构建好的内层 exe 算出 21 个方法的 IL hash，
   写进 aceguard.c 的期望表，重新编译 DLL。
用法：python sync_guard.py <inner_exe>
"""
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ACE = os.path.join(HERE, "..", "ace_crack")
GCC = r"C:\msys64\mingw64\bin\gcc.exe"
ILHASH = os.path.join(ACE, "ilhash.exe")

# 必须与 build_release.py 的 METHODS 顺序完全一致
METHODS = ["Main", "rT9gG2", "tB9gL2", "sY5hH8",
           "pX2vN9", "bN7uA3", "aF4hT9", "eG7jU2", "iO3pV6",
           "mR2wZ5", "fT8kQ1", "mU6vZ7", "oX3cC1", "cK5iE8",
           "zQ4mR7", "nD3sF6", "rY3tG6", "sP9oH2", "wX8lL9",
           "bP6qR2", "cR8sT5", "bX9vK3"]


def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ACE, "_inner_gui.exe")
    r = subprocess.run([ILHASH, exe, "AceTool"] + METHODS,
                       capture_output=True, text=True, errors="replace")
    vals = {}
    for line in (r.stdout or "").splitlines():
        p = line.split()
        if len(p) >= 3 and p[0] in METHODS:
            vals[p[0]] = int(p[2])
    missing = [m for m in METHODS if m not in vals]
    if missing:
        print("!! 缺方法:", missing)
        return 1
    arr = ", ".join(str(vals[m]) for m in METHODS)
    print("期望表（%d 项）: %s" % (len(METHODS), arr[:80] + "…"))

    c = os.path.join(HERE, "aceguard.c")
    s = open(c, encoding="utf-8").read()
    s2 = re.sub(r"static const int AG_EXPECTED\[AG_IL_COUNT\] = \{[^}]*\};",
                "static const int AG_EXPECTED[AG_IL_COUNT] = { %s };" % arr, s, count=1)
    if s2 == s:
        print("   （期望表已是目标值，无需改动）")
    else:
        open(c, "w", encoding="utf-8").write(s2)

    dll = os.path.join(HERE, "aceguard.dll")
    env = dict(os.environ)
    env["PATH"] = r"C:\msys64\mingw64\bin;" + env.get("PATH", "")
    r2 = subprocess.run([GCC, "-shared", "-O2", "-o", dll, "aceguard.c", "-lbcrypt"],
                        capture_output=True, text=True, errors="replace", cwd=HERE, env=env)
    if r2.returncode != 0:
        print("!! DLL 编译失败")
        print((r2.stdout or "") + (r2.stderr or ""))
        return 1
    print("DLL 已重编：%s (%d 字节)" % (dll, os.path.getsize(dll)))

    # 同步到客户端资源目录
    import shutil
    shutil.copy(dll, os.path.join(ACE, "aceguard.dll"))
    print("已同步到 ace_crack/aceguard.dll")
    return 0


if __name__ == "__main__":
    sys.exit(main())