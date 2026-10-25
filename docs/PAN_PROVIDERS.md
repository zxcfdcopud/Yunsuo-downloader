# 网盘 / 链接解析 Provider 说明与维护指南

> 结论先讲：网盘**没有**公开的「直链解析」开放接口。本项目走的是**网页端私有接口**，
> 用**用户本人的登录态**访问，行为等同于用户自己在浏览器里操作。
> 这类接口官方随时会改，所以本文的重点是：**怎么快速定位、怎么修**。

---

## 0. 2026-10 实测状态（重要）

这一轮用真实请求把四家网盘的接口逐个探了一遍（方法见第 4 节），结论：

| 网盘 | 接口状态 | 本项目的处理 |
|------|----------|--------------|
| **123 网盘** | 老接口 `www.123pan.com/api/share/get`、`api.123pan.com/api/share/get` **已 404**；<br>现役主机是 **`https://www.123865.com/api/share/get`**（对错误的 shareKey 返回规范 JSON：`{"code":5103,"message":"此分享不存在"}`） | 已切到 `www.123865.com`，接口通了 |
| **百度网盘** | `/share/list` 不带验证凭证时返回 **`{"errno":9019,"errmsg":"need verify"}`** —— **必须先 `/share/verify` 并接住 `Set-Cookie: BDCLND`** | 已实现 Cookie 会话 + verify 前置；实测 verify 正常应答（假 surl 返回 `errno=105`） |
| **阿里云盘** | `/adrive/v{1,2,3,4}/share_link/get_share_token` **全部 404 已下线**；<br>`share_link/get_share_by_anonymous`(v2/v3) 与 `file/list`(v2/v3) **仍存在** | 改成「先匿名拿分享信息/令牌 → 再列目录」的多路兜底；拿不到令牌时给明确提示，不再误报"需要提取码" |
| **夸克网盘** | `sharepage/token`、`sharepage/detail` **路径仍有效**，token 接口接受 `passcode`（提取码） | 已补上提取码支持 |

★ 关于阿里云盘要说清楚：`get_share_token` 下线意味着**匿名访问的令牌只能从
`get_share_by_anonymous` 的响应里拿**（如果它给的话）。代码已按这个顺序尝试并多处兜底；
如果你遇到的阿里云盘分享仍然解析不了，用第 3.1 节的 `ysd probe --verbose` 把每一步的
真实返回发我，我照着返回改——这条路只能靠真实返回迭代。

---

## 1. 代码结构

```
Core/Providers/
  ILinkProvider.cs         ILinkProvider 接口 + ResolveContext + LinkResolver 注册中心
  ResolveResult.cs         解析结果（地址/类型/文件名/请求头/引擎建议/是否需要提取码/诊断）
  BuiltinProviders.cs      通用直链、磁力、迅雷、ed2k、视频站、用户自定义规则
  Pan/
    PanHttp.cs             带 Cookie 会话的请求层（PanSession）+ JSON 路径取值 + 提取码识别
    CloudPanProviders.cs   阿里云盘 / 夸克 / 123 / 百度 四个 Provider
```

`LinkResolver` 按 `Priority` 从高到低询问，谁先 `CanHandle` 谁处理：
阿里云盘(100) > 夸克(95) > 123(90) > 百度(85) > 迅雷(60) > ed2k(55) > 磁力(50)
> 视频站(40) > 用户规则(20) > 通用直链(0)。

---

## 2. 提取码（访问密码）是怎么处理的

这是通用能力，四家网盘都支持，用户有三种给法：

1. **写在链接里**（最省事）
   ```
   https://www.alipan.com/s/xxxx?pwd=1234
   https://pan.baidu.com/s/1AbCdEfGh#pwd=abcd
   提取码: abcd          ← 也可以直接粘在链接后面
   ```
   `PanHttp.ExtractPwd()` 识别 `?pwd=` / `#pwd=` / `&pwd=` / `passcode=` /
   `"提取码: xxxx"` / `"密码：xxxx"`。

2. **界面弹框输入**
   解析时发现需要提取码会弹输入框，填完自动重试，不用重新添加任务。
   「新建任务 → 解析预览」和任务列表里的 **🔑 按钮**（只有缺提取码的任务才出现）都支持。

3. **命令行**
   ```powershell
   ysd probe "https://pan.baidu.com/s/1AbCdEfGh" --pwd abcd --verbose
   ysd add   "https://pan.baidu.com/s/1AbCdEfGh" --pwd abcd
   ```

实现上：Provider 需要密码时返回 `ResolveResult.NeedPassword = true`，界面据此弹框；
用户填的密码通过 `ResolveContext.SharePassword` 传回来重试。各家判定信号不同，
代码里逐个映射：
* 阿里云盘：响应出现 `WrongPassword` / `ShareLinkPwdError` / `密码` 才判为密码问题
  （**不能只看 401** —— 401 是令牌缺失，误判会让用户去填一个根本不存在的密码）
* 夸克：`code` 41009/41010 或消息含 `密码`/`passcode`
* 百度：`/share/verify` 返回 `errno != 0`（105 除外，那是分享不存在）
* 123：`code` 403/5113 或消息含 `密码`

---

## 3. 出了问题的排查顺序

### 3.1 一条命令拿到全部真相
```powershell
ysd probe "分享链接" --verbose
```
打印内容：
* 命中的解析器、是否缺 Cookie
* **每一步 HTTP 请求与响应**（`→ POST /adrive/v3/share_link/get_share_by_anonymous`
  / `← 404 {"code":"NotFound",...}`）
* 汇成"解析过程诊断"；桌面端在任务「详情」里能看到同一份内容

看到 `"not exist"` / 404 → 接口路径变了；看到 `errno`/`code` → 业务层报错
（多为 Cookie 失效或需要提取码）。

### 3.2 按错误信息对症下药
| 现象 | 原因 | 怎么办 |
|------|------|--------|
| 提示需要提取码 | 分享设了密码 | 弹框输入，或写进链接 `?pwd=` |
| 提示提取码不对 | 密码错 | 重新输入 |
| `分享不存在/已失效` | 链接失效被取消 | 浏览器里打开确认 |
| 百度 `errno=9019 need verify` | 验证凭证没生效 | 确认 Cookie 含 `BDUSS` 后重开一次 |
| 百度 `errno=-9` | Cookie 无效/过期 | 重新复制 Cookie |
| 123 `code=5103` | 分享不存在 | 确认链接 |
| 123 `code=401/403` | 未登录或 Cookie 过期 | 重新复制 Cookie |
| 接口 `uri(...) not exist` | **网盘改版**了 | 按 3.4 更新 Provider |

### 3.3 用 rules.json 兜底（不用改代码）
「设置 → 数据与维护 → 打开 rules.json」：
```json
[
  {
    "name": "某站直链重写",
    "enabled": true,
    "pattern": "^https://pan\\.example\\.com/s/([A-Za-z0-9]+)$",
    "directUrl": "https://cdn.example.com/real/$1",
    "referer": "https://pan.example.com/",
    "kind": "video",
    "preferYtDlp": false
  }
]
```

### 3.4 改 Provider
每个 Provider 都是独立类，互不影响。改完用 `ysd probe --verbose` 验证，不必真的下载。
参考实现（同见 docs/第三方开源项目清单.txt）：
* 百度 <https://github.com/qjfoidnh/BaiduPCS-Go>
* 阿里云盘 <https://github.com/tickstep/aliyunpan>
* 夸克 <https://github.com/Cp0204/quark-auto-save>

### 3.5 加一个新的 Provider
```csharp
public sealed class MyPanProvider : ILinkProvider
{
    public string Name => "MyPan";
    public string DisplayName => "我的网盘";
    public int Priority => 80;
    public bool RequiresCredential => true;
    public bool CanHandle(string url) => url.Contains("mypan.com/s/");

    public async Task<ResolveResult> ResolveAsync(string url, ResolveContext ctx, CancellationToken ct = default)
    {
        var cookie = ctx.SettingsService.GetCookie(Name);
        var session = new PanSession(ctx.Http, cookie, m => ctx.Log(m), ctx.Verbose);
        var pwd = ctx.SharePassword ?? PanHttp.ExtractPwd(url);

        var resp = await session.GetJsonAsync("https://mypan.com/api/share/get?key=xxx",
            new[] { new KeyValuePair<string, string>("Referer", "https://mypan.com/") }, ct);
        ctx.Diag($"GET /api/share/get → HTTP {resp.Status} {resp.Snippet}");   // 关键：留诊断
        // ... 解析 resp，返回 ResolveResult（需要密码就 NeedPassword = true）
    }
}
```
然后注册进 `DownloadManager` 的 `LinkResolver` 链；「设置 → 网盘账号」页面按
`KnownPans` 数组列出新网盘。

---

## 4. 怎么探测接口（下次官方改版照这个做）

用**不存在的参数**去"撞"接口，靠返回区分三种情况：

```powershell
curl.exe -s -m 25 "https://候选主机/api/share/get?shareKey=zzzz" `
  -H "Referer: https://候选主机/" -H "platform: web" -H "app-version: 3"
```

| 返回 | 含义 |
|------|------|
| `{"code":5103,"message":"此分享不存在"}` | **接口存在**，只是参数不对 |
| `{"message":"no Route matched with those values"}` | 路径已废弃（网关层 404） |
| `{"code":"NotFound","message":"uri(...) not exist"}` | **路径没了**，要换接口 |
| 一大段 HTML | 命中了前端路由，不是 API |

关键是**区分"接口不存在"和"业务报错"**：前者换路径，后者换参数/更新 Cookie。

项目里留着当次的探测脚本：`artifacts\probe-pan.ps1`、`artifacts\probe-pan2.ps1`。

---

## 5. 凭据是怎么存的

* 只存 `Cookie` 请求头字符串，位置 `%AppData%\YunsuoDownloader\settings.json`
  的 `panCredentials[].cookieProtected`。
* 加密：Windows DPAPI（`CryptProtectData`），密钥绑定当前 Windows 用户，
  换用户或换机器都无法解密。
* 实现见 `Core/Utils/SecretProtector.cs`（P/Invoke `crypt32.dll`，零第三方依赖）。
* 程序不含任何遥测；Cookie 不会离开本机。
* 百度/夸克/123 解析过程中产生的**新运行时 Cookie**（例如百度的 `BDCLND`）
  只存在于本次解析的 `PanSession` 里，不会写回配置。

---

## 6. 已知的坑

1. **百度的 sign 算法**与网页端版本绑定。本项目实现的是社区通用形式
   （`app_id + timestamp + rand + uid + sign_type` 的 MD5）。若持续 `errno ≠ 0`，
   按 3.4 更换算法。
2. **夸克必须转存**才能取直链，会真的往你网盘里放一份文件，注意空间。
3. **百度/夸克/123 的直链有时效**（通常几小时），过期要重新解析。
4. **文件夹分享**：阿里云盘会列根目录并优先挑视频文件；其余三家只取第一个文件，
   暂不支持递归。
5. **代理环境**：本机若跑着 Clash 之类的工具（DNS 常见 fake-IP 198.18.x.x），
   部分网盘域名可能连不上。实测 `api.alipan.com`、`pan.baidu.com`、
   `www.123865.com` 通；旧的 `api.aliyundrive.com` 会超时。
6. **百度分享页的压缩流**：实测遇到过与 `Content-Encoding` 不匹配的响应，
   导致自动解压抛 `unsupported compression method`。现在 provider 客户端只启用
   gzip/deflate，并且失败时会用 `identity` 裸请求兜底一次。
