# -*- coding: utf-8 -*-
# -*- coding: utf-8 -*-
"""collect_payload.py —— 采集本机最新的 ACE 组件，重建 ace_payload.zip
用法：
    python collect_payload.py                 # 只用本机已安装的
    python collect_payload.py --online        # 顺便拉官方公开包一起比版本
    python collect_payload.py --src 目录
规则：按 FileVersion 取最新，同版本取文件更大的那个。
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "ace_payload.zip")
DEF_SRC = r"C:\Program Files\AntiCheatExpert"
WANT = ["ACE-BOOT.sys", "ACE-Tray.exe", "ACE-TrayV.exe", "ace-boot.dat"]

OFFICIAL = ("https://wx-gamesafe-pub-1258344700.cos.ap-nanjing.myqcloud.com/uploads/images/"
            "%E9%A2%84%E5%90%AF%E5%8A%A8%E8%87%AA%E5%8A%A9%E5%AE%89%E8%A3%85%E5%8C%85-2601.17164.zip")


def filever(path):
    """用 PowerShell 读 FileVersion，返回可比较的元组"""
    try:
        r = subprocess.run(
            ["powershell", "-NoProfile", "-Command",
             "(Get-Item -LiteralPath '%s').VersionInfo.FileVersion" % path.replace("'", "''")],
            capture_output=True, text=True, errors="replace", timeout=30)
        s = (r.stdout or "").strip()
        if not s:
            return ()
        return tuple(int(x) for x in re.findall(r"\d+", s))
    except Exception:
        return ()


def scan(src):
    found = {}
    if not os.path.isdir(src):
        return found
    for name in WANT:
        p = os.path.join(src, name)
        if os.path.isfile(p):
            found[name] = (p, filever(p), os.path.getsize(p))
    return found


def fetch_official():
    d = tempfile.mkdtemp(prefix="aceoff_")
    try:
        req = urllib.request.Request(OFFICIAL, headers={
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120",
            "Referer": "https://gamesafe.qq.com/",
        })
        with urllib.request.urlopen(req, timeout=180) as r:
            data = r.read()
        zp = os.path.join(d, "pkg.zip")
        open(zp, "wb").write(data)
        with zipfile.ZipFile(zp) as z:
            z.extractall(d)
        print("  官方包已下载（%.1f MB）" % (len(data) / 1048576))
        return d
    except Exception as e:
        print("  官方包下载失败：", e)
        return None


def main():
    src = DEF_SRC
    online = "--online" in sys.argv
    for i, a in enumerate(sys.argv):
        if a == "--src" and i + 1 < len(sys.argv):
            src = sys.argv[i + 1]

    print("== 扫描本机：%s" % src)
    local = scan(src)
    for n, (p, v, sz) in local.items():
        print("   %-16s ver=%-18s %10d" % (n, ".".join(map(str, v)) or "-", sz))

    cands = dict(local)
    if online:
        print("== 拉官方公开包")
        od = fetch_official()
        if od:
            off = scan(od)
            for n, (p, v, sz) in off.items():
                print("   %-16s ver=%-18s %10d" % (n, ".".join(map(str, v)) or "-", sz))
                cur = cands.get(n)
                if cur is None or v > cur[1] or (v == cur[1] and sz > cur[2]):
                    cands[n] = (p, v, sz)
                    print("      ↑ 采用官方这份")

    missing = [n for n in WANT if n not in cands]
    if missing:
        print("!! 缺文件：", missing)
        return 1

    print("\n== 写入 %s" % OUT)
    with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
        for n in WANT:
            p, v, sz = cands[n]
            z.write(p, n)
            print("   + %-16s ver=%-18s %10d" % (n, ".".join(map(str, v)) or "-", sz))
    print("   包大小 %.2f MB" % (os.path.getsize(OUT) / 1048576))
    return 0


if __name__ == "__main__":
    sys.exit(main())