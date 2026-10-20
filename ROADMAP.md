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
- **整合包安装（MultiMC / Prism / HMCL 导出包）**：解析 `mmc-pack.json` 的组件 → 直接铺开包内的 `.minecraft/`（mod 已在包里，无需下载）
- **整合包安装（.mrpack）**：解析 `modrinth.index.json` → 自动装对应加载器 → 并发下载并 sha1 校验 → 铺开 `overrides/`
- **URL 改写补充**：`media.forgecdn.net` 会 403，同路径的 `edge.forgecdn.net` 正常（文件字节一致）
- **版本 jar 从父版本复制**：Forge/NeoForge 的 `-DignoreList` 期望版本目录里有 `${version_name}.jar`，缺了会把同一个游戏加载成两个模块（`ResolutionException`）
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

### 2. 整合包：CurseForge zip

CurseForge 的 manifest 只给 `projectID`/`fileID`，换下载直链必须调 `api.curseforge.com`，实测无 key 一律 403。

需要用户提供免费 API key（`console.curseforge.com` → API Keys），然后加一个 `--cf-key` 参数或存进配置。

顺带已知：CF 的下载域名 `media.forgecdn.net` 会 403，要改写成 `edge.forgecdn.net`（已实现）。

---

## 明确不做

| 平台 | 原因 |
|---|---|
| **Cleanroom** | 1.12.2 的 Forge 分支，用户基数太小 |
| **LiteLoader** | 已停止维护（止于 1.12） |
| **LabyMod** | 商业客户端，需要官方启动器环境 |
