# Cleanroom 实例（1.12.2）在 Kyouka MCLauncher 上跑起来的方法

> 适用：整合包带 **Cleanroom Relauncher**（`mods/++relauncher-*.jar`、`relauncher/mmcpack/`），
> 想在 Kyouka MCLauncher 里启动的 1.12.2 实例。
> 记录于 2026-10-22，实测通过（149 个 mod，Java 25 也能跑）。

## 原理

Cleanroom Relauncher 是两段式启动：

```
第一段：Java 8 + Forge 引导
  └─ Relauncher 检查 mmcpack → 用 Java 21+ 重新启动
第二段：Java 21/25 + Foundation + Cleanroom（真正的游戏）
```

所以启动器必须：先让第一段跑起来，再保证第二段的 **classpath / JVM 参数 / natives** 正确。

## 手工配置清单（5 步）

实例目录记为 `<版本目录>`（例如 `.minecraft/versions/1.12.2-Forge_14.23.5.2864`）。

### 1. 工作目录 = 版本目录

Kyouka MCLauncher v1.0.2 起已默认（版本隔离），无需手动。
旧版本请升级，否则 `mods` 不会被读到。

### 2. 修复 `config/cleanroom_relauncher.cfg` 的跨机器路径

别人的整合包里 Class Path 常写着 `D:\新建文件夹\...`。
Kyouka MCLauncher v1.0.3 起会在启动前自动修复（备份 `.bak-*`）。

### 3. Class Path = client jar → Cleanroom jar → 库

顺序很关键：

```
[0]  1.12.2-Forge_14.23.5.2864.jar                     ← MC 客户端（混淆类）
[1]  com/cleanroommc/cleanroom/0.6.12-alpha/cleanroom-0.6.12-alpha.jar
     ↑ Cleanroom 是完整 Forge 替代：自带改过的 FMLTweaker + binpatches.pack.lzma
[2..] lwjglxx / cleanmix / mixinextras / asm / lwjgl3 / …（mmcpack patches 里的 libraries）
```

**绝对不能**把原版 `forge-1.12.2-14.23.5.2864.jar` 放进 classpath：
它的 `FMLTweaker` 会设 SecurityManager，Java 17+ 直接崩：

```
UnsupportedOperationException: Setting a Security Manager is not supported
```

### 4. 解压 LWJGL3 natives 并告诉 JVM

Cleanroom 用 LWJGL3，需要原生 DLL：

```powershell
# 从 versions/libraries/org/lwjgl/*/*-natives-windows.jar 解压 .dll 到 <版本目录>\natives-lwjgl3
```

然后在 cfg 的 `JVM Arguments` 里加：

```
-Xmx6g -Xms2g
-Dorg.lwjgl.librarypath=<版本目录>\natives-lwjgl3
-Djava.library.path=<版本目录>\natives-lwjgl3
```

缺这步会报：

```
java.lang.UnsatisfiedLinkError: Failed to locate library: lwjgl.dll
```

### 5. Java Path 指向 Java 21 或 25

不能用 Java 8（Cleanroom 需要 17+，官方测试 21）：

```
Java Path = <JDK21>\bin\javaw.exe
或          <JDK25>\bin\javaw.exe
```

> 实测 JDK 25.0.1 也能进游戏；若遇到 mixin/ASM 相关崩溃，换回 21。

## 常见报错对照表

| 报错 | 原因 | 处理 |
|---|---|---|
| `ClassNotFoundException: top.outlands.foundation.boot.Foundation` | Class Path 是别的机器的绝对路径 | 步骤 2 |
| `Patcher expecting non-empty class data ... vanilla jar may be corrupt` | classpath 缺 client jar / Cleanroom jar（缺 binpatches） | 步骤 3 |
| `Setting a Security Manager is not supported` | classpath 里原版 forge 排在 Cleanroom 前面 | 步骤 3（移除原版 forge） |
| `UnsatisfiedLinkError: lwjgl.dll` | 缺 LWJGL3 natives 或没设 librarypath | 步骤 4 |
| `Failed to get MCP data! UnknownHostException: export.mcpbot.bspk.rs` | mcpbot 服务已停 | **可以忽略**，Forge 会回退 |

## 已知限制

- 这套配置目前需要手动做（自动修复在做，见 `进度与待办.md` 的「Cleanroom 适配」）
- 内存建议 ≥6G（149 个 mod）
- 首次启动会下载较多运行库（约 100 MB）
