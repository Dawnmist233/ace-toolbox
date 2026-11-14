# -*- coding: utf-8 -*-
"""把关键方法名改成无意义标识符（确定性映射，可重复执行）
   同时会把 ILN 里的字符串一起改（它们必须匹配新名字）
"""
import re
import sys

P = r"C:\Users\111\Documents\deepseek-harness\default-workspace\ace_crack\ace_tool2.cs"

MAP = {
    # 校验 / 授权
    "MustBeIntact": "zQ4mR7",
    "ILGuard": "pX2vN9",
    "ILIntact": "kW7bT3",
    "ExpectAt": "hY5cJ8",
    "Fnv": "nD3sF6",
    "Require": "tB9gL2",
    "TryRequire": "vC6nP4",
    "RequireTicket": "mR2wZ5",
    "VerifyTicketBlob": "fT8kQ1",
    "VerifySignedBlob": "jH4yS7",
    "Degrade": "qW1eD9",
    "EnsureGate": "bN7uA3",
    "EnsureOpsLoaded": "cK5iE8",
    # 指纹
    "SealOps": "rY3tG6",
    "UnsealOps": "sP9oH2",
    "ExpectedFP": "xD6fV5",
    "LiveFP": "gL2mV4",
    "FpKeyStream": "yN8bC3",
    "FpXor": "uJ1kR7",
    # 动作
    "ThrottleAce": "aF4hT9",
    "RestoreAce": "eG7jU2",
    "ForceDeleteAceNoReboot": "iO3pV6",
    "CloseAceCloud": "lC9rW1",
    "CurrentAceTargets": "dB5sX8",
    "ResolveAceProcesses": "kE2tY4",
    "KillAllAce": "mU6vZ7",
    # ops
    "FetchOps": "nA8wB5",
    "LoadOps": "oX3cC1",
    "RunOps": "pD7dE9",
    "ExecOp": "qR4fF6",
    # 卡密 / 网络
    "ShowKamiDialog": "rT9gG2",
    "VerifyToken": "sY5hH8",
    "KamiStart": "tU1iI3",
    "KamiResource": "uV6jJ7",
    "DownloadToMemory": "vW2kK4",
    "CtrDecryptInline": "wX8lL9",
    "VerifyOpsSig": "xZ3mM5",
    "OpsSigFresh": "yA7nN1",
    # 原生
    "EnumServices": "bP6qR2",
    "AgInit": "zB4oO6",
    "AgCheckTicket": "aC9pP2",
}


def main():
    s = open(P, encoding="utf-8").read()
    changed = []
    for old, new in MAP.items():
        pat = r"(?<![A-Za-z0-9_])" + re.escape(old) + r"(?![A-Za-z0-9_])"
        cnt = len(re.findall(pat, s))
        if cnt == 0:
            print("  跳过（未出现）:", old)
            continue
        s = re.sub(pat, new, s)
        changed.append((old, new, cnt))

    open(P, "w", encoding="utf-8").write(s)
    print("\n改名统计：")
    for old, new, cnt in changed:
        print("  %-24s -> %-8s  %3d 处" % (old, new, cnt))
    print("\n共改名 %d 个符号" % len(changed))


if __name__ == "__main__":
    sys.exit(main())