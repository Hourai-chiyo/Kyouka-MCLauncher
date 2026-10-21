# mcroute 路线图

按优先级排列。已完成的不再重复讨论，未完成的写清「为什么」和「怎么做」，避免下次接手时重新推理一遍。

---

## 已完成

- 原版下载与启动（元数据三级兜底：Mojang → Modrinth → Wiki）
- NeoForge（官方 installer + `inheritsFrom` 合并 + maven URL 改写）
- Forge（同上，复用同一套 `loader` 逻辑）
- sha1 校验 / 断点续传 / 内容寻址资源去重
- 微软登录（设备码流程 + refresh token 静默续期）—— 等 Mojang AppID 白名单
- 图形界面 + 下载进度条
- **Java 检测**：扫描 PATH + 常见安装目录（Adoptium / .minecraft runtime / Modrinth / CurseForge），逐个跑 `-version` 校验主版本，按 version json 的需求匹配；`java` 子命令可列出全部
- **Fabric / Quilt / Legacy Fabric**：不用安装器，直接从各自官方 meta 拿 profile json；自动挑最新 stable loader
  （Quilt 用 `meta.quiltmc.org/v3`，Legacy Fabric 用 `meta.legacyfabric.net/v2`）
- **老版本 natives 解压**：1.19 之前必须把 .dll 解出来放进 `java.library.path`，否则 `UnsatisfiedLinkError`
- **实例目录兼容主流启动器**：整合包改为装在 `versions/<实例名>/`（版本隔离布局），并写出**合并后的完整 json + 客户端 jar**，PCL2 / HMCL / Prism 直接把 mcdata 当 .minecraft 就能识别
- **MSI 安装包（WiX v5）**：per-user、静默安装/卸载实测通过；WiX v7 因 OSMF 付费条款不可用，锁在 v5
- **MSIX 安装包（不做了）**：makeappx + 自签名实测能注册运行，但**每个用户都要手动信任证书**，体验比 exe 安装器差；上架商店才值得做，暂缓
- **整合包安装（CurseForge zip）**：`manifest.json` 只有 projectID/fileID，靠官方 API 换地址；key 放环境变量 `CF_API_KEY` 或 `mcdata/cf_api_key.txt`
- **整合包安装（MultiMC / Prism / HMCL 导出包）**：解析 `mmc-pack.json` 的组件 → 直接铺开包内的 `.minecraft/`（mod 已在包里，无需下载）
- **整合包安装（.mrpack）**：解析 `modrinth.index.json` → 自动装对应加载器 → 并发下载并 sha1 校验 → 铺开 `overrides/`
- **URL 改写补充**：`media.forgecdn.net` 会 403，同路径的 `edge.forgecdn.net` 正常（文件字节一致）
- **版本 jar 从父版本复制**：Forge/NeoForge 的 `-DignoreList` 期望版本目录里有 `${version_name}.jar`，缺了会把同一个游戏加载成两个模块（`ResolutionException`）
- **大学镜像加速 maven 库**：`https://mirrors.hit.edu.cn/bmclapi/`（哈工大，BMCLAPI 的镜像，标准 maven 布局）。实测 **7~8 MB/s**，官方 maven 只有 0.65~1.7 MB/s；只覆盖 maven 库（Forge/NeoForge universal、fabric-loader、工具库），assets/manifest/client jar 会 404 自动回落官方
- **`--memory` 参数**：之前堆内存写死 2G，202 mod 的整合包直接不够用
- **库的 `url` 为空字符串**：老版本 Forge 的条目 url 是空串（文件由安装器放好），之前会被整个跳过 → `ClassNotFoundException: FMLTweaker`。现在有 `path` 就进 classpath
- **老版本 Forge 预下载**：1.12.2 及更早的运行时库藏在 `install_profile.versionInfo` 里，不预下载安装器会卡在慢速 maven 上
- **修复同名库去重**：同一个库可能在 json 里出现两次（一条普通 jar、一条只提供 natives），按 name 去重会把前者顶掉，导致 `NoClassDefFoundError: Narrator` 这类问题
  （顺带支持了 Fabric/Quilt 风格的库声明：`name` + maven 仓库根地址，没有 `downloads` 块）

---

## 下一步

### 1. OptiFine（评估后倾向不做）

**问题**：OptiFine 的官方 installer 采用「版本覆盖」模式——直接改写 jar，这和"多版本共存"天然冲突，一个版本被它改过之后就不能再正常启动了。

**建议**：
- 默认引导用户改用 Sodium（Fabric 生态）或 OptiFine 的 mod 版本
- 若确实要支持：**只在独立的实例目录里允许**，禁止改动共享的版本目录
- 1.12.2 / 1.7.10 确实有用户需要，但那是"改 jar"时代的历史包袱

---

## v0.3.0（2026-10-21）安全与健壮性

外部代码评审指出的三个风险点，逐个处理：

| 评审意见 | 处理 |
|---|---|
| 解压存在 Zip Slip 风险 | ✅ 新增 `SafeZip`，整合包 overrides / MultiMC `.minecraft/` / Java 包全部走安全解压；`--selftest-security` 用恶意 zip 回归（3/3 通过） |
| 下载整包进内存 | ✅ 新增 `Http.StreamToFile`：80KB 缓冲流式写盘 + 边下边算 SHA-1，校验通过才原子替换；`--selftest-download` 验证 |
| README 与实际状态不一致 | ✅ 新增「功能状态（实现 / 测试 / 可用）」三态表 |
| 同步等待异步 API | ⚠️ 保留现状但确认：所有网络调用都在后台线程（`RunTask`），UI 线程不参与；后续若引入 UI 直接调用再改 |

### 下载引擎后续计划（尚未实现）

1. **单文件分段并行**（HTTP Range + 206 + Content-Range 校验）
2. 断点续传（分段状态 + ETag/Last-Modified 一致性检查）
3. 智能调度（按文件大小决定是否分段：小文件继续并发，大文件才分段）
4. 每阶段都要求：失败清理临时文件、可取消、并发写不撕裂、哈希不符即失败

> 原则：**保留现有下载器作为回退**，新引擎只处理「服务器支持 Range 且文件够大」的情况。

## 待办：更多 Mod 来源（长期）

| 来源 | 形态 | 状态 |
|---|---|---|
| Modrinth | 公开 API，免 key，可直链下载 | ✅ v0.5.0 已接入 |
| CurseForge | 需用户自有 API Key；部分文件禁止第三方分发 | 待做（二期） |
| **MC百科 mcmod.cn** | **中文资料站**，下载链接指向外部站点 | 待评估（三期） |

关于 mcmod.cn 的说明：它是**资料库不是文件托管**，所以合理用法是
「中文搜索 / 中文简介 / 跳转到原下载页」，而不是把它当下载源。
站点有反爬策略，接入时要遵守 robots.txt，且只做跳转、不做镜像。

## 待办：Cleanroom（1.12.2 现代生态）

1.12.2 的 Cleanroom（Java 21 + LWJGL3）通过 **Cleanroom Relauncher** 把 Forge 实例升级运行。
启动器需要：识别 Cleanroom 实例、自动选 Java 21、修复跨机器的 `cleanroom_relauncher.cfg` 路径。

- Cleanroom：<https://download.cleanroommc.com/> · [GitHub](https://github.com/CleanroomMC/Cleanroom)
- Scalar：<https://github.com/CleanroomMC/Scalar>
- Fugue：<https://github.com/CleanroomMC/Fugue>
- Relauncher：<https://github.com/CleanroomMC/CleanroomRelauncher>
- Relauncher Unofficial：<https://github.com/kappa-maintainer/Cleanroom-Relauncher>

> 注意：Cleanroom 的 maven 不在 BMCLAPI 镜像范围内，只能直连。
