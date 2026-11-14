# ACE 工具箱 · 源码导出包（v3.1.6）

> 导出时间：2026-11-14
> 内容：完整 C# 源码 / 原生校验 DLL 源码 / 构建脚本 / 资源 / 文档
> 正式构建与「免卡密构建」共用同一份源码，靠编译开关区分

---

## 一、目录结构

```
ACE源码导出/
├─ src/
│   ├─ ace_tool2.cs          # 主程序（约 6k 行，全部逻辑）
│   ├─ aceguard.c            # 原生校验 DLL（RSA 验签 + IL 完整性判断）
│   ├─ ilhash.cs             # 工具：算方法 IL 的 FNV
│   └─ readilh.cs            # 工具：反射读运行时状态
├─ assets/
│   ├─ app.ico               # 图标
│   ├─ ace.manifest          # UAC 清单（requireAdministrator）
│   ├─ ace_payload.zip       # 内嵌的 ACE 组件包（4 个文件）
│   └─ ace-ops.txt           # 当前云端功能脚本明文（离线版内嵌用）
├─ tools/
│   ├─ build_final.py        # ★ 正式构建（三步：hash 注入 → 同步 DLL → 复验）
│   ├─ build_release.py      # 编译 + 算 hash + 注入 + 稳定复验
│   ├─ sync_guard.py         # 把最终 hash 写进 aceguard.c 并重编 DLL
│   ├─ obfuscate.py          # 方法名混淆（确定性映射，可重复执行）
│   └─ collect_payload.py    # 采集本机最新 ACE 组件，重建 ace_payload.zip
├─ build_nolicense.bat       # 一键构建免卡密版
├─ build_release.bat         # 一键构建正式版
└─ README.md                 # 本文件
```

---

## 二、编译开关

| 开关 | 作用 |
|---|---|
`NOLICENSE` | 不弹卡密窗、不校验授权态、不要动作票据、跳过完整性守卫、恢复本地目标清单 |
`OFFLINE` | 不检查更新；功能脚本从内嵌 `ace-ops.txt` 读（只进内存，不落盘） |
`DEBUG` | 命令行调试模式、`ACE_KAMI_URL` 可覆盖服务端地址、可指定数据目录 |
`ILTEST` | 额外输出 IL 自校验诊断 |

**正式版：不加任何开关。**
**免卡密版：`NOLICENSE` + `OFFLINE`。**

---

## 三、构建步骤

### 3.1 环境

```
· .NET Framework 4.0（csc.exe: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe）
· Python 3.10+
· MinGW-w64 gcc（编 aceguard.dll）：C:\msys64\mingw64\bin\gcc.exe
```

### 3.2 正式版

```bash
cd tools
python build_final.py
```

它会依次：
1. 编译 → 算 22 个方法的 IL hash → 注入常量 → 复验稳定
2. 用最终 hash 重编 `aceguard.dll`（期望值写进机器码）
3. 带新 DLL 重新编译客户端 → 再复验一次

输出 `_inner_gui.exe`，再用壳脚本打包：

```bash
cd ../../ace_pack
python pack8.py ../ace_crack/_inner_gui.exe "../ace_crack/ACE工具箱vX.Y.Z.exe" 5 --admin --sign --clean
```

### 3.3 免卡密版

```bash
cd tools
python build_nolicense.py      # 或直接跑根目录 build_nolicense.bat
```

产出 `ACE工具箱vX.Y.Z_免卡密版.exe`。

> 注意：免卡密版**不参与** IL 完整性校验与票据链，专用于自己人使用，不要对外分发。

---

## 四、这版防护是怎么堆起来的

```
① 参数只在云端（ops）
   limit|targets  限制/恢复的目标清单
   disable|kill   要杀的进程
   disable|scfilter  服务/驱动筛选用正则
   → 没有下发 = 没有参数 = 动作全部拒绝

② ops 按设备加密
   服务端用 SHA256('aceops|masterKey|deviceId') 派生密钥现场加密
   → 抓包换机解不开

③ 指纹当密钥
   ops 明文用「IL 指纹」派生的密钥流封存，运行时用实时指纹解封
   → 改了 IL 就解不出参数

④ 指纹判定在原生 DLL
   AG_CheckIL / AG_Fingerprint：22 个方法的期望 hash 编译在机器码里
   → 改托管层的 bool 没意义

⑤ 名称混淆（40+ 符号）+ 5 层壳 + 签名
```

---

## 五、版本号规则

```
3.1.6 → 3.1.7 → … → 3.1.9 → 3.2.0 → …
第三位到 9 就进位
· 修 bug / 调参数 → 第三位 +1
· 新功能         → 第二位 +1，第三位归零
· 大改           → 第一位 +1
```

版本号写在 `ace_tool2.cs` 的 `APP_VER`，云端资源 `ace-update` 的 version 字段要和它一致。

---

## 六、云端

```
环境：CloudBase mc-d9gnl09z9b3c78775
接口：kamiVerify（客户端）/ kamiAdmin（管理）
资源：ace-update（安装包）/ ace-ops（功能脚本）/ ace-ilhash（可选）
```

`ace-ops` 的关键字段：

```
limit|targets|名称1,名称2,^正则
disable|kill|进程1,进程2,^正则
disable|scfilter|片段1,片段2
disable|schdel|关键词
disable|rundel|关键词
```

`scfilter` 支持逗号分隔多片段（按「或」组合），`^` 开头表示正则。

---

## 七、ACE 升级后怎么跟

```bash
cd tools
python collect_payload.py --online     # 与官方公开包比版本，取较新
# 或者从一台刚更新过 ACE 的机器上采集：
python collect_payload.py --src "D:\ACE备份"
```

然后重跑 `build_final.py` + 打包即可。

---

## 八、已知取舍

```
· 客户端必须联网才能用（参数在云端）—— 断网 = 动作不可用
· aceguard.dll 会释放到随机临时目录并尝试退出清理；运行期仍可被取证
· 内层是 Assembly.Load(byte[])，明文 PE 会短暂存在于托管堆（dump 可拿到，
  但拿到也改不动：改 IL → 指纹变 → ops 解不开）
```