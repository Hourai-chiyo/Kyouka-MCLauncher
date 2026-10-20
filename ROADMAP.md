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

---

## 下一步

### 1. Fabric / Quilt / Legacy Fabric

**为什么简单**：它们没有安装器、没有二进制补丁，加载器官方 meta 直接给一份现成的 version json（自带 `inheritsFrom`），拿到就能用我们现有的合并逻辑。

```
GET https://meta.fabricmc.net/v2/versions/loader/{mc}/{loader}/profile/json
```

Quilt 只需换 meta 地址（`https://meta.quiltmc.org/v3/...`），Legacy Fabric 只是换加载器版本号。

**建议命令形式**：

```bash
python mcroute.py loader fabric 1.20.1-0.16.9    # <MC版本>-<loader版本>
python mcroute.py loader quilt  1.20.1-0.27.0
```

实现要点：
- 拿到 profile json → 写入 `versions/fabric-loader-{loader}-{mc}/`
- 之后走现成的 `fetch` + `launch`，无需特殊处理
- 需要处理「同一 MC 版本装多个加载器」的目录命名冲突

---

### 2. Java 检测与管理（当前是隐性风险）

**现状**：
- `find_java()` 只在 `mcdata/java_versions` 和 Modrinth App 的 java 目录里找，找不到就退回 PATH
- 本机只有 Java **8** 和 **21**，**缺 17**
- Forge 1.20.1 声明需要 Java 17，实际是用 **21** 跑起来的——能跑，但不是它要求的版本，属于"侥幸可用"

**要做的**：

1. **读需求**：从 version json 的 `javaVersion.majorVersion` 取要求的大版本
   （1.12.2→8，1.17→16，1.20.1→17，1.21.1→21）
2. **本机检测**：注册表 `SOFTWARE\JavaSoft`、`Program Files`、`PATH`、已有 `java_versions`
3. **缺失时下载**：Adoptium API 实测可用（`api.adoptium.net`），清华 TUNA 镜像作备用，
   落到 `mcdata/java_versions/<name>/`，之后自动复用
4. **启动前校验**：大版本不匹配时**明确提示**，而不是静默用错的版本
5. 可选子命令：`java list` / `java install 17`

**为什么重要**：这是目前唯一一个"用户机器上缺东西时无法自助"的环节，而且错配 Java 版本报出的错往往和真正原因无关（和之前 `No value present` 那个坑同一类）。

---

### 3. OptiFine（评估后倾向不做）

**问题**：OptiFine 的官方 installer 采用「版本覆盖」模式——直接改写 jar，这和"多版本共存"天然冲突，一个版本被它改过之后就不能再正常启动了。

**建议**：
- 默认引导用户改用 Sodium（Fabric 生态）或 OptiFine 的 mod 版本
- 若确实要支持：**只在独立的实例目录里允许**，禁止改动共享的版本目录
- 1.12.2 / 1.7.10 确实有用户需要，但那是"改 jar"时代的历史包袱

---

### 4. 整合包安装（.mrpack / CurseForge zip）

需要解析整合包 manifest、处理依赖树、把 mod 落进实例的 `mods/`。
现在只支持"一个版本目录"，还没有"实例"概念（多个实例共享 libraries/assets）。

---

## 明确不做

| 平台 | 原因 |
|---|---|
| **Cleanroom** | 1.12.2 的 Forge 分支，用户基数太小 |
| **LiteLoader** | 已停止维护（止于 1.12） |
| **LabyMod** | 商业客户端，需要官方启动器环境 |
