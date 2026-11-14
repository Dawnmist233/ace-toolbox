// aceguard.dll —— 原生授权校验（x64）
// 作用：票据验签 + 设备/动作/时效比对，全部在原生层完成
// 导出：
//   int AG_VerifyTicket(const char* blob, const char* act, const char* deviceHash,
//                       long long nowMs, char* reason, int reasonCap);
//   int AG_SelfOk(void);
//   const char* AG_Version(void);
#include <windows.h>
#include <bcrypt.h>
#include <string.h>
#include <stdio.h>

#define AG_EXPORT __declspec(dllexport)

// ---- 服务端 RSA 公钥（构建时写入，n 为 512 hex，e 为 010001）----
static const char* N_HEX =
    "d26c1aef62616a94d5d1b7cf0f884b07f631e78869f73cbc0e56b44cab8cd47889ce2cbb91ae139f1a6e5ca1ff1a6911e9dcd33ff42e891ead1f31c12cd43a1af669363b7edb7c3da0ef64cda956adc940ec73b84c959e5d1084b5091845b556f6626820617a9bfd5a11e84527898fe1a15a64d92389d8eae13295f6c89f42ac07708d56c9daa8533f7528eec94622fde96055c991a24d9f4ad40acbec27296b885e542a69b171461453fe8404f398f2bb7070691208204f2e87524dde2bf7ef9587d37d10584caf46639802e759848f6a95134f2e2f88be8a627bc8bd335b543beb4f32a8d7134c3535fe0e41898b834c5c7c2b8b7bf0edd24dbc857ac9ad4f";
static const char* E_HEX = "010001";

static int hexval(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static int hex2bin(const char* hex, unsigned char* out, int cap)
{
    int n = 0;
    while (hex[0] && hex[1])
    {
        int a = hexval(hex[0]), b = hexval(hex[1]);
        if (a < 0 || b < 0) break;
        if (n >= cap) return -1;
        out[n++] = (unsigned char)((a << 4) | b);
        hex += 2;
    }
    return n;
}

static int b64u_val(char c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '-') return 62;
    if (c == '_') return 63;
    return -1;
}

static int b64u_decode(const char* s, int len, unsigned char* out, int cap)
{
    int acc = 0, bits = 0, n = 0;
    for (int i = 0; i < len; i++)
    {
        int v = b64u_val(s[i]);
        if (v < 0) return -1;
        acc = (acc << 6) | v;
        bits += 6;
        if (bits >= 8)
        {
            bits -= 8;
            if (n >= cap) return -1;
            out[n++] = (unsigned char)((acc >> bits) & 0xFF);
        }
    }
    return n;
}

// 在 JSON 里找 "key":"value"（value 不含引号）
static int json_str(const char* json, const char* key, char* out, int cap)
{
    char pat[64];
    _snprintf_s(pat, sizeof(pat), _TRUNCATE, "\"%s\":", key);   // 带冒号，避免撞到值里同名串
    const char* p = strstr(json, pat);
    if (!p) return 0;
    p += strlen(pat);
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '"') return 0;
    p++;
    int n = 0;
    while (*p && *p != '"' && n < cap - 1) out[n++] = *p++;
    out[n] = 0;
    return 1;
}

static long long json_num(const char* json, const char* key, int* found)
{
    char pat[64];
    _snprintf_s(pat, sizeof(pat), _TRUNCATE, "\"%s\":", key);
    *found = 0;
    const char* p = strstr(json, pat);
    if (!p) return 0;
    p += strlen(pat);
    while (*p == ' ' || *p == '\t') p++;
    long long v = 0;
    int any = 0;
    while (*p >= '0' && *p <= '9')
    {
        v = v * 10 + (*p - '0');
        p++;
        any = 1;
    }
    *found = any;
    return v;
}

static int sha256(const unsigned char* data, int len, unsigned char* out32)
{
    BCRYPT_ALG_HANDLE h = NULL;
    if (BCryptOpenAlgorithmProvider(&h, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0) return 0;
    int ok = 0;
    BCRYPT_HASH_HANDLE hh = NULL;
    if (BCryptCreateHash(h, &hh, NULL, 0, NULL, 0, 0) == 0)
    {
        if (BCryptHashData(hh, (PUCHAR)data, (ULONG)len, 0) == 0)
            if (BCryptFinishHash(hh, out32, 32, 0) == 0) ok = 1;
        BCryptDestroyHash(hh);
    }
    BCryptCloseAlgorithmProvider(h, 0);
    return ok;
}

static int rsa_verify(const unsigned char* msg, int msgLen,
                      const unsigned char* sig, int sigLen)
{
    unsigned char n[512], e[16], hash[32];
    int nlen = hex2bin(N_HEX, n, sizeof(n));
    int elen = hex2bin(E_HEX, e, sizeof(e));
    if (nlen <= 0 || elen <= 0) return 0;
    if (!sha256(msg, msgLen, hash)) return 0;

    int ok = 0;
    BCRYPT_ALG_HANDLE hAlg = NULL;
    BCRYPT_KEY_HANDLE hKey = NULL;
    if (BCryptOpenAlgorithmProvider(&hAlg, BCRYPT_RSA_ALGORITHM, NULL, 0) != 0) return 0;

    // BCRYPT_RSAPUBLIC_BLOB: 头 + exponent + modulus
    int blobLen = (int)sizeof(BCRYPT_RSAKEY_BLOB) + elen + nlen;
    unsigned char* blob = (unsigned char*)HeapAlloc(GetProcessHeap(), 0, blobLen);
    if (blob)
    {
        BCRYPT_RSAKEY_BLOB* hdr = (BCRYPT_RSAKEY_BLOB*)blob;
        hdr->Magic = BCRYPT_RSAPUBLIC_MAGIC;
        hdr->BitLength = nlen * 8;
        hdr->cbPublicExp = elen;
        hdr->cbModulus = nlen;
        hdr->cbPrime1 = 0;
        hdr->cbPrime2 = 0;
        memcpy(blob + sizeof(BCRYPT_RSAKEY_BLOB), e, elen);
        memcpy(blob + sizeof(BCRYPT_RSAKEY_BLOB) + elen, n, nlen);

        if (BCryptImportKeyPair(hAlg, NULL, BCRYPT_RSAPUBLIC_BLOB, &hKey, blob, blobLen, 0) == 0)
        {
            BCRYPT_PKCS1_PADDING_INFO pi;
            pi.pszAlgId = BCRYPT_SHA256_ALGORITHM;
            if (BCryptVerifySignature(hKey, &pi, hash, 32,
                                      (PUCHAR)sig, (ULONG)sigLen, BCRYPT_PAD_PKCS1) == 0)
                ok = 1;
            BCryptDestroyKey(hKey);
        }
        HeapFree(GetProcessHeap(), 0, blob);
    }
    BCryptCloseAlgorithmProvider(hAlg, 0);
    return ok;
}


/* ---------- IL 完整性判断（期望值编译在机器码里，改 IL 无效）---------- */
#define AG_IL_COUNT 22
static const int AG_EXPECTED[AG_IL_COUNT] = { 303800821, 571635761, 989903223, -699616532, 2075538902, 1560914675, 4058603, -542926237, 341745411, 729222028, -721562609, -2108860175, -1166345355, -1910741651, 527641526, 1124521789, 1997165696, -1661716904, -384794635, -1411403284, -623767805, -598220926 };


/* 返回指纹（全对才非 0）；给托管层当解密密钥用 —— 改 IL 就解不开 */
AG_EXPORT int AG_Fingerprint(const int* live, int n)
{
    if (!live || n != AG_IL_COUNT) return 0;
    unsigned int acc = 2166136261u;
    for (int i = 0; i < AG_IL_COUNT; i++)
    {
        if (AG_EXPECTED[i] == 0) return 0;
        if (live[i] != AG_EXPECTED[i]) return 0;
        unsigned int x = (unsigned int)live[i];
        for (int b = 0; b < 4; b++) { acc ^= (unsigned char)(x >> (b * 8)); acc *= 16777619u; }
    }
    return (int)(acc ? acc : 1);
}

AG_EXPORT int AG_CheckIL(const int* live, int n)
{
    if (!live || n != AG_IL_COUNT) return 0;
    unsigned int acc = 2166136261u;
    for (int i = 0; i < AG_IL_COUNT; i++)
    {
        if (AG_EXPECTED[i] == 0) return 0;          // 未注入 = 失败
        if (live[i] != AG_EXPECTED[i]) return 0;    // 任何一个不符 = 失败
        unsigned int x = (unsigned int)live[i];
        for (int b = 0; b < 4; b++) { acc ^= (unsigned char)(x >> (b * 8)); acc *= 16777619u; }
    }
    return (int)(acc ? acc : 1);
}

AG_EXPORT const char* AG_Version(void)
{
    return "aceguard/1.0";
}

// 简单自检：公钥常量是否被改坏
AG_EXPORT int AG_SelfOk(void)
{
    unsigned char n[512];
    int nlen = hex2bin(N_HEX, n, sizeof(n));
    return (nlen == 256) ? 1 : 0;
}

AG_EXPORT int AG_VerifyTicket(const char* blob, const char* act, const char* deviceHash,
                              long long nowMs, char* reason, int reasonCap)
{
    if (reason && reasonCap > 0) reason[0] = 0;
    if (!blob || !act || !deviceHash) { if (reason) strcpy_s(reason, reasonCap, "bad-args"); return 0; }

    const char* dot = strchr(blob, '.');
    if (!dot || dot == blob || !dot[1]) { if (reason) strcpy_s(reason, reasonCap, "bad-format"); return 0; }

    int p64len = (int)(dot - blob);
    unsigned char payload[4096];
    int plen = b64u_decode(blob, p64len, payload, sizeof(payload) - 1);
    if (plen <= 0) { if (reason) strcpy_s(reason, reasonCap, "payload-decode"); return 0; }
    payload[plen] = 0;

    unsigned char sig[1024];
    int sigLen = b64u_decode(dot + 1, (int)strlen(dot + 1), sig, sizeof(sig));
    if (sigLen <= 0) { if (reason) strcpy_s(reason, reasonCap, "sig-decode"); return 0; }

    // 服务端对 base64url 字符串本身签名（不是解码后的字节）
    if (!rsa_verify((const unsigned char*)blob, p64len, sig, sigLen))
    {
        if (reason) strcpy_s(reason, reasonCap, "bad-signature");
        SecureZeroMemory(payload, sizeof(payload));
        return 0;
    }

    const char* json = (const char*)payload;

    char kind[32] = { 0 };
    if (!json_str(json, "k", kind, sizeof(kind)) || strcmp(kind, "act") != 0)
    { if (reason) strcpy_s(reason, reasonCap, "bad-kind"); return 0; }

    char jact[64] = { 0 };
    if (!json_str(json, "act", jact, sizeof(jact))) { if (reason) strcpy_s(reason, reasonCap, "no-act"); return 0; }
    if (_stricmp(jact, act) != 0) { if (reason) strcpy_s(reason, reasonCap, "act-mismatch"); return 0; }

    char jdh[128] = { 0 };
    if (!json_str(json, "dh", jdh, sizeof(jdh))) { if (reason) strcpy_s(reason, reasonCap, "no-dh"); return 0; }
    if (_stricmp(jdh, deviceHash) != 0) { if (reason) strcpy_s(reason, reasonCap, "device-mismatch"); return 0; }

    int found = 0;
    long long exp = json_num(json, "exp", &found);
    if (!found) { if (reason) strcpy_s(reason, reasonCap, "no-exp"); return 0; }
    if (nowMs > exp + 120000) { if (reason) strcpy_s(reason, reasonCap, "expired"); return 0; }

    SecureZeroMemory(payload, sizeof(payload));
    return 1;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    (void)h; (void)reason; (void)r;
    return TRUE;
}