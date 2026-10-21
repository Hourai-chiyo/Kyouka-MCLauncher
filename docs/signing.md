# 代码签名（SignPath，开源项目免费）

> 目的：让 Windows 不再提示「无法验证发布者」，并且 SmartScreen 不再拦。
> 这份文档写完时签名流程**还没生效**——需要你在 SignPath 网站上申请一次（约 10 分钟 + 审核 1~2 天）。

---

## 一、工作流已经配好了

仓库里的 [`.github/workflows/build-and-sign.yml`](../.github/workflows/build-and-sign.yml)：

```
打 tag（v0.1.x）  →  构建 exe  →  冒烟测试(--selftest)  →  上传给 SignPath
                  →  等待签名完成  →  验证签名状态  →  上传到 GitHub Release
手动触发          →  同上，但签名结果作为 Actions artifact 供下载
```

**你不需要改工作流**，只要把下面两个 secret 填进去。

---

## 二、SignPath 配置清单（你回来照着点）

### 1. 注册并创建组织

1. https://signpath.io → **Sign in with GitHub**（用 `Hourai-chiyo` 账号）
2. 右上角 **Create organization**
   - Organization name：`Hourai-chiyo`（随意）
   - 勾选 **Open source project**（这样才免费）
3. 记下 **Organization ID**（Organization settings 页面里，形如 `12345678-...`）

### 2. 连接 GitHub 仓库

1. Organization → **Repositories** → **Connect repository**
2. 选 `Hourai-chiyo/mcroute`
3. SignPath 会要求装 GitHub App，授权到这个仓库即可

### 3. 创建 Project

Project 页面 → **Create project**

| 字段 | 填 |
|---|---|
| Name | `mcroute` |
| Slug | **`mcroute`** ← 工作流里用的就是这个 |
| Repository | `Hourai-chiyo/mcroute` |

### 4. 创建 Artifact Configuration

Project → **Artifact configurations** → **Create**

- **Slug**：`mcroute-exe`
- 内容用这份（GitHub Actions 上传的 artifact 是 zip，所以根节点是 `zip-file`）：

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="mcroute.exe">
      <authenticode-sign/>
    </pe-file>
  </zip-file>
</artifact-configuration>
```

### 5. 创建 Signing Policy

Project → **Signing policies** → **Create**

- **Slug**：`release-signing`
- 类型：**Automatic approval for open source**（开源项目可以自动批，否则每次要人工点）
- Artifact configuration：选上一步的 `mcroute-exe`

### 6. 生成 API Token

右上角头像 → **User settings** → **API tokens** → **Create token**

- 权限：能提交签名请求即可（默认即可）

### 7. 填到 GitHub Secrets

仓库 → Settings → Secrets and variables → Actions → **New repository secret**：

| 名字 | 值 |
|---|---|
| `SIGNPATH_API_TOKEN` | 上一步生成的 token |
| `SIGNPATH_ORGANIZATION_ID` | 第 1 步的 Organization ID |

填完就行——**下次打 tag 会自动走签名**。

---

## 三、验证签名是否生效

```powershell
# 下载签名后的 exe 后
Get-AuthenticodeSignature .\mcroute.exe | Format-List Status, StatusMessage, SignerCertificate

# 期望：
#   Status : Valid
#   SignerCertificate.Subject : CN=...
```

签名正常后：
- 属性 → 数字签名 里能看到签名者
- 双击不再弹「未知发布者」
- SmartScreen 会逐步建立信誉（新证书前期仍可能提示一次）

---

## 四、本地先试签名（不花一分钱，仅本机有效）

自签名证书可以让**你自己的机器**不再提示，用来验证签名流程是否正常：

```powershell
# 只签名（其他机器仍然会提示未知发布者）
powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1

# 签名 + 把证书加入「当前用户受信任根」（本机不再提示）
powershell -ExecutionPolicy Bypass -File tools\sign-local.ps1 -Trust
```

脚本做的事：
1. 复用/创建一张自签名代码签名证书（`CN=mcroute dev (self-signed)`）
2. `Set-AuthenticodeSignature` 签名 exe
3. `-Trust` 时把证书导出并导入 `Cert:\CurrentUser\Root`

> 注意：自签名证书**只对装过它的机器有效**，别人下载照样提示——所以正式分发还是要 SignPath。

---

## 五、备选方案（如果 SignPath 审核不通过）

| 方案 | 成本 | 备注 |
|---|---|---|
| **Azure Trusted Signing** | 约 ¥70/月 | 微软云签名；工作流把 `Submit signing request` 那步换成 Azure 的 action 即可 |
| **Certum Open Source** | 免费（需 U 盾/身份审核） | 审核慢，但证书是公共 CA 签发 |
| OV / EV 商业证书 | ¥1000+/年 | EV 可立即通过 SmartScreen；纯个人项目不建议 |

---

## 六、没有签名时的兜底（现在正在用）

- Release 附 **`SHA256SUMS.txt`**，用户可自行核对
- README 里有「关于未知发布者安全警告」章节，解释为什么提示、怎么点
- exe 的版本信息（产品名/公司/版本/版权）已补全，属性对话框不再是空白
