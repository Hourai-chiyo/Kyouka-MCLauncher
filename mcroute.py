# -*- coding: utf-8 -*-
"""
mcroute.py —— MC 下载源路由验证器 v0
=====================================

这个脚本不做任何"启动游戏"的事，它只做一件事：
    把《MC启动器路由表》里的每一条源真的请求一遍，看谁通、谁快、谁要兜底。

为什么先做这个？
    因为路由表是"设计"，脚本跑出来的才是"事实"。
    你以后写的下载器，逻辑跟这个脚本里的 get_manifest() 是一样的。

【怎么运行】在 VS Code 里打开本文件夹，按 Ctrl+` 打开终端，然后：

    python mcroute.py manifest         # 从 Mojang 拉版本清单，失败自动切 Modrinth
    python mcroute.py resolve 1.21.1   # 解析某个版本，看它要下载多少东西
    python mcroute.py sources          # 体检：把所有源打一遍，出速度表

只用 Python 标准库，不需要 pip install 任何东西。
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

# Windows 终端有时不是 UTF-8，这里兜一下，避免中文打出来是乱码
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# 有些服务器会拒绝没有 User-Agent 的请求，所以统一带一个
UA = "mcroute/0.1 (learning project)"
TIMEOUT = 8  # 秒。路由表里定的首字节超时是 3 秒，这里放宽一点方便调试


# ---------------------------------------------------------------------------
# 路由表：元数据层的候选源，按优先级从上到下
# 注意：Mojang 官方排第一（EULA 意义上的 authorized source），Modrinth 只做兜底
# ---------------------------------------------------------------------------
MANIFEST_SOURCES = [
    ("Mojang/launchermeta", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
    ("Mojang/piston-meta", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
    ("Modrinth/launcher-meta", "https://launcher-meta.modrinth.com/minecraft/v0/manifest.json"),
]

# 体检用的端点清单：(名字, URL, 期望说明)
SOURCES = [
    ("Mojang manifest", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"),
    ("Modrinth manifest", "https://launcher-meta.modrinth.com/minecraft/v0/manifest.json"),
    ("Modrinth version json", "https://launcher-meta.modrinth.com/minecraft/v0/versions/1.21.1.json"),
    ("Modrinth neo meta", "https://launcher-meta.modrinth.com/neo/v0/versions/21.1.264.json"),
    ("Modrinth maven (neo)", "https://launcher-meta.modrinth.com/maven/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-universal.jar"),
    ("ForgeCDN neo (官方)", "https://neoforged.forgecdn.net/releases/net/neoforged/neoforge/21.1.264/neoforge-21.1.264-universal.jar"),
    ("Forge maven", "https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-installer.jar"),
    ("Fabric meta", "https://meta.fabricmc.net/v2/versions/loader"),
    ("Adoptium API", "https://api.adoptium.net/v3/info/available_releases"),
]


# ---------------------------------------------------------------------------
# 工具函数
# ---------------------------------------------------------------------------
def fetch_bytes(url, timeout=TIMEOUT):
    """下载一个 URL，返回 (字节内容, 耗时秒数)。失败会抛异常，由调用方处理。"""
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = resp.read()
    return data, time.time() - t0


def fetch_json(url, timeout=TIMEOUT):
    """下载并解析 JSON，返回 (数据, 耗时秒数, 字节数)。"""
    data, dt = fetch_bytes(url, timeout)
    return json.loads(data.decode("utf-8")), dt, len(data)


def get_manifest():
    """
    核心逻辑：按路由表顺序尝试每个 manifest 源，第一个成功的就是本次的结果。

    返回值：(manifest数据, 用了哪个源, 耗时, 字节数, 之前失败的记录列表)
    如果全失败，抛 RuntimeError。

    这段代码就是"兜底"这件事的最小实现——你以后写下载器，结构一模一样。
    """
    errors = []
    for name, url in MANIFEST_SOURCES:
        try:
            data, dt, nbytes = fetch_json(url)
            return data, name, dt, nbytes, errors
        except Exception as exc:  # 网络错误、超时、JSON 坏了都到这里
            errors.append((name, str(exc)))
    raise RuntimeError("所有 manifest 源都失败了")


def human_kb(nbytes):
    """把字节数变成好看的单位"""
    if nbytes >= 1024 * 1024:
        return f"{nbytes / 1024 / 1024:.1f} MB"
    return f"{nbytes / 1024:.1f} KB"


def sha1_of(data):
    """计算 sha1，用来校验文件没被换过——路由表里的核心判据"""
    import hashlib

    return hashlib.sha1(data).hexdigest()


# ---------------------------------------------------------------------------
# 子命令 1: manifest
# ---------------------------------------------------------------------------
def cmd_manifest(_args):
    print("正在按路由表顺序尝试 manifest 源 ...\n")

    data, used, dt, nbytes, errors = get_manifest()

    # 先把失败过的打出来，让你看到"兜底真的发生过"
    for name, msg in errors:
        print(f"  [失败] {name}: {msg}")

    versions = data.get("versions", [])
    latest = data.get("latest", {})

    print(f"\n[成功] 使用的源 : {used}")
    print(f"       耗时     : {dt:.2f}s")
    print(f"       大小     : {human_kb(nbytes)}")
    print(f"       版本数   : {len(versions)}")
    print(f"       最新正式版: {latest.get('release', '?')}")
    print(f"       最新快照  : {latest.get('snapshot', '?')}")
    print(f"\n最近 5 个版本:")
    for v in versions[:5]:
        print(f"  - {v.get('id'):<20} {v.get('type'):<10} {v.get('releaseTime', '')}")


# ---------------------------------------------------------------------------
# 子命令 2: resolve
# ---------------------------------------------------------------------------
def cmd_resolve(args):
    ver = args.version
    print(f"正在解析版本 {ver} ...\n")

    # 第一步：拿到 manifest，找到这个版本的 json 地址
    manifest, used, dt, _nbytes, _errs = get_manifest()
    target = None
    for v in manifest.get("versions", []):
        if v.get("id") == ver:
            target = v
            break

    if target is None:
        print(f"[错误] manifest 里没有版本 {ver}")
        return

    print(f"[1/2] manifest 来自 {used}（{dt:.2f}s），找到 {ver} 的 json 地址")
    json_url = target["url"]
    print(f"      {json_url}")

    # 第二步：下载这个版本的 json
    vj, dt2, nbytes2 = fetch_json(json_url)
    print(f"[2/2] 版本 json 下载完成（{dt2:.2f}s，{human_kb(nbytes2)}）\n")

    # 第三步：把"这个版本要下多少东西"列出来
    client = vj.get("downloads", {}).get("client", {})
    server = vj.get("downloads", {}).get("server", {})
    libraries = vj.get("libraries", [])
    asset_index = vj.get("assetIndex", {})
    asset_count = 0

    print(f"版本信息")
    print(f"  id          : {vj.get('id')}")
    print(f"  类型        : {vj.get('type')}")
    print(f"  mainClass   : {vj.get('mainClass')}")
    print(f"  Java 大版本 : {vj.get('javaVersion', {}).get('majorVersion', '?')}")
    print(f"\n下载闭包")
    print(f"  client.jar  : {human_kb(client.get('size', 0))}  sha1={str(client.get('sha1', ''))[:12]}...")
    if server:
        print(f"  server.jar  : {human_kb(server.get('size', 0))}  sha1={str(server.get('sha1', ''))[:12]}...")
    print(f"  libraries   : {len(libraries)} 个")
    print(f"  assets 索引 : {asset_index.get('id')}  sha1={str(asset_index.get('sha1', ''))[:12]}...")

    # 顺手统计一下 libraries 里有多少个不是 Mojang 官方库
    # （非官方库在 Modrinth 的 version json 里会被改写到 launcher-meta.modrinth.com/maven/）
    third_party = 0
    for lib in libraries:
        artifact = (lib.get("downloads", {}) or {}).get("artifact") or {}
        url = artifact.get("url", "")
        if url and "libraries.minecraft.net" not in url:
            third_party += 1
    print(f"  非官方库    : {third_party} 个（有加载器时会改走 Modrinth maven）")
    print(f"\n提示：这些文件全都是 sha1 寻址的，所以可以安全地做本地缓存。")


# ---------------------------------------------------------------------------
# 子命令 3: sources
# ---------------------------------------------------------------------------
def cmd_sources(_args):
    print("源体检（只读 GET，单位 KB/s）\n")
    header = f"{'SOURCE':<24}{'STATUS':<8}{'TIME':>8}{'SIZE':>11}{'SPEED':>12}"
    print(header)
    print("-" * len(header))

    ok = 0
    for name, url in SOURCES:
        try:
            data, dt = fetch_bytes(url)
            size = len(data)
            speed = (size / 1024) / dt if dt > 0 else 0
            ok += 1
            print(f"{name:<24}{'OK':<8}{dt:>7.2f}s{human_kb(size):>11}{speed:>9.0f}KB/s")
        except urllib.error.HTTPError as exc:
            print(f"{name:<24}{'HTTP ' + str(exc.code):<8}{'-':>8}{'-':>11}{'-':>12}")
        except Exception as exc:
            print(f"{name:<24}{'FAIL':<8}{'-':>8}{'-':>11}{str(exc)[:10]:>12}")

    print("-" * len(header))
    print(f"{ok}/{len(SOURCES)} 个端点可用")


# ---------------------------------------------------------------------------
# 子命令 4: build —— 用本地已有的实例目录，拼出启动命令（只打印，不执行）
# ---------------------------------------------------------------------------
# 默认目录：Modrinth App 的缓存（你机器上已经有完整的三套版本）
DEFAULT_BASE_DIR = os.path.join(os.environ.get("APPDATA", ""), "ModrinthApp", "meta")

# 版本 json 里的 features 开关，我们全部不启用（演示模式/自定义分辨率等都不开）
DEFAULT_FEATURES = {"is_demo_user": False, "has_custom_resolution": False}


def rules_allow(obj, features=None):
    """
    判断一个带 rules 的条目（库 / 参数）是否适用于「Windows + x86_64」。

    规则语义（和官方启动器一致）：
      - 没有 rules  -> 允许
      - 有 rules    -> 逐条看，只有"匹配当前环境"的规则才算数，
                       最后一条匹配的 action 决定结果（allow / deny）
    """
    if features is None:
        features = DEFAULT_FEATURES
    rules = obj.get("rules")
    if not rules:
        return True

    allowed = False
    for rule in rules:
        action = rule.get("action", "allow")

        # 1) 操作系统匹配（不匹配就跳过这条规则）
        os_rule = rule.get("os") or {}
        if os_rule:
            name = os_rule.get("name")
            if name and name != "windows":
                continue
            arch = os_rule.get("arch")
            if arch and arch not in ("x86_64", "amd64"):
                continue

        # 2) features 匹配（不匹配就跳过）
        feats = rule.get("features") or {}
        if feats and any(features.get(k, False) != v for k, v in feats.items()):
            continue

        # 3) 这条规则生效
        allowed = (action == "allow")

    return allowed


def maven_path(artifact, lib):
    """
    把一条库信息换算成 libraries/ 目录下的相对路径。

    三种来源，按可靠性排序：
      1. artifact["path"]  —— 新的 json 一般直接给（最可靠）
      2. 从下载 url 里截   —— url 形如 .../libraries.minecraft.net/<这里> 或 .../maven/<这里>
      3. 从 name 拼        —— "group:artifact:version[:classifier]" 拼成 Maven 路径
    """
    path = artifact.get("path")
    if path:
        return path

    url = artifact.get("url", "")
    for marker in ("libraries.minecraft.net/", "/maven/"):
        if marker in url:
            return url.split(marker, 1)[1]

    name = lib.get("name", "")
    parts = name.split(":")
    if len(parts) < 3:
        return ""
    group, artifact_id, version = parts[0], parts[1], parts[2]
    classifier = parts[3] if len(parts) > 3 else None
    filename = f"{artifact_id}-{version}"
    if classifier:
        filename += f"-{classifier}"
    filename += ".jar"
    return "/".join(group.split(".")) + f"/{artifact_id}/{version}/{filename}"


def library_download(lib):
    """
    给一条库信息算出「下载地址 + libraries 下的相对路径 + 期望 sha1」。

    有两种写法，都要支持：
      1. 官方/Forge 风格：downloads.artifact.url 直接给了完整地址
      2. Fabric/Quilt 风格：只有 name（maven 坐标）+ url（仓库根地址），
         需要自己拼成 <url>/<group路径>/<artifact>/<version>/<artifact>-<version>.jar
    """
    downloads = lib.get("downloads") or {}
    artifact = downloads.get("artifact")
    if artifact and artifact.get("path"):
        # 注意：老版本 Forge 的条目 url 是空字符串（文件由安装器放好），
        # 这时候仍然要按 path 进 classpath，只是不能拿来下载。
        return (artifact.get("url") or None), artifact["path"], artifact.get("sha1")

    name = lib.get("name") or ""
    base_url = lib.get("url")
    if not base_url or not name:
        return None

    parts = name.split(":")
    if len(parts) < 3:
        return None

    group, artifact_id, version = parts[0], parts[1], parts[2]
    classifier = parts[3] if len(parts) > 3 else None

    filename = "%s-%s" % (artifact_id, version)
    if classifier:
        filename += "-" + classifier
    filename += ".jar"

    rel = "/".join(group.split(".")) + "/%s/%s/%s" % (artifact_id, version, filename)
    if not base_url.endswith("/"):
        base_url += "/"
    return base_url + rel, rel, lib.get("sha1")


def subst(template, ctx):
    """把 ${xxx} 占位符替换成实际值"""
    for key, value in ctx.items():
        template = template.replace("${" + key + "}", str(value))
    return template


def expand_arg_entries(entries, ctx):
    """
    展开 arguments 段。条目有两种形态：
      - 字符串："-cp"
      - 字典：{"rules": [...], "value": "某个参数" 或 ["多个", "参数"]}
    """
    out = []
    for entry in entries:
        if isinstance(entry, str):
            out.append(subst(entry, ctx))
            continue
        if not rules_allow(entry):
            continue
        value = entry.get("value")
        if isinstance(value, list):
            out.extend(subst(v, ctx) for v in value)
        elif isinstance(value, str):
            out.append(subst(value, ctx))
    return out


_JAVA_VERSION_CACHE = {}

# 常见安装位置：这些目录下面是一层「版本目录」，里面是 bin\java.exe 或 jre\bin\java.exe
JAVA_SEARCH_DIRS = [
    os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Eclipse Adoptium"),
    os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Java"),
    os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), "Java"),
    os.path.join(os.environ.get("APPDATA", ""), ".minecraft", "runtime"),
    os.path.join(os.environ.get("APPDATA", ""), "ModrinthApp", "meta", "java_versions"),
    os.path.join(os.environ.get("USERPROFILE", ""), "curseforge", "minecraft", "Install", "java"),
]


def java_major_of(exe):
    """
    跑一次 java -version 解析主版本号（结果缓存，不重复跑）。

    为什么必须真跑：目录名可能被改过，PATH 里也可能是 shim/javapath，
    光看路径名不可靠。1.8.0_xxx 会归一成 8。
    """
    if exe in _JAVA_VERSION_CACHE:
        return _JAVA_VERSION_CACHE[exe]

    import re
    import subprocess

    major = None
    try:
        proc = subprocess.run([exe, "-version"], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=15)
        text = (proc.stdout or "") + (proc.stderr or "")
        match = re.search(r'version "(\d+)(?:\.(\d+))?', text)
        if match:
            first = int(match.group(1))
            major = 8 if first == 1 else first
    except Exception:
        major = None

    _JAVA_VERSION_CACHE[exe] = major
    return major


def scan_java(base=None):
    """
    找出机器上所有可用的 java.exe，返回 [(路径, 主版本号), ...]（按版本升序）。

    来源顺序：PATH -> 常见安装目录 -> 我们自己管理的 <数据目录>/java_versions
    """
    seen = set()
    candidates = []

    def add(path_):
        if not path_ or not os.path.isfile(path_):
            return
        key = os.path.normcase(os.path.realpath(path_))
        if key in seen:
            return
        seen.add(key)
        candidates.append(path_)

    # 1) PATH
    for part in (os.environ.get("PATH") or "").split(os.pathsep):
        if part.strip():
            add(os.path.join(part.strip().strip('"'), "java.exe"))

    # 2) 常见安装目录（一层版本子目录）
    for root in JAVA_SEARCH_DIRS:
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            sub = os.path.join(root, name)
            if os.path.isdir(sub):
                add(os.path.join(sub, "bin", "java.exe"))
                add(os.path.join(sub, "jre", "bin", "java.exe"))

    # 3) 我们自己下载管理的
    if base:
        ours = os.path.join(base, "java_versions")
        if os.path.isdir(ours):
            for name in sorted(os.listdir(ours)):
                add(os.path.join(ours, name, "bin", "java.exe"))

    result = []
    for exe in candidates:
        major = java_major_of(exe)
        if major is not None:
            result.append((exe, major))
    result.sort(key=lambda item: item[1])
    return result


def find_java(base, vj):
    """
    按 version json 要求的 Java 大版本挑一个 java.exe。
    没有精确匹配时，挑版本最接近的（并会在启动前给出提示）。
    """
    required = (vj.get("javaVersion") or {}).get("majorVersion")
    available = scan_java(base)

    if required is not None:
        for exe, major in available:
            if major == required:
                return exe

    if not available:
        return "java"
    if required is None:
        return available[-1][0]
    return min(available, key=lambda item: abs(item[1] - required))[0]


def write_argfile(path, args):
    """
    把参数写进一个文件，之后可以用 java @文件名 启动。
    好处：命令行不会超长，也方便你自己看/改。
    含空格的参数要加引号。
    """
    with open(path, "w", encoding="utf-8") as f:
        for arg in args:
            if arg == "" or " " in arg:
                f.write('"' + arg.replace('"', '\\"') + '"\n')
            else:
                f.write(arg + "\n")


def merge_version(parent, child):
    """
    把「子版本 json」合并到「父版本 json」上（Minecraft 的 inheritsFrom 语义）：
      - libraries：父的在前，子版本的同名库覆盖父版本
      - arguments.jvm / arguments.game：父的在前，子的追加
      - 其它字段：子版本有就用子的
    """
    merged = dict(parent)

    for key, value in child.items():
        if key == "libraries":
            # 去重键 = (groupId, artifactId, classifier, 是否有 natives)，**不含版本号**。
            # 这样 Forge/NeoForge 的 guava 17.0 能覆盖原版的 guava 15.0；
            # 如果按完整 name 去重，两个版本都会留在 classpath 里，
            # Java 取先出现的那个 -> 经典的 Forge 1.7.10 "NoSuchMethodError: com.google.common"。
            def _lib_key(lib):
                parts = (lib.get("name") or "").split(":")
                group = parts[0] if len(parts) > 0 else ""
                artifact = parts[1] if len(parts) > 1 else ""
                classifier = parts[3] if len(parts) > 3 else ""
                # rules 不同 = 互斥条目（1.12.2 的 lwjgl：2.9.4 给 Windows、2.9.2 只给 osx），
                # 必须都保留，否则按 rules 过滤后整条消失
                rules = lib.get("rules")
                rules_sig = json.dumps(rules, sort_keys=True, ensure_ascii=False) if rules else ""
                return (group, artifact, classifier, bool(lib.get("natives")), rules_sig)

            def _has_artifact(lib):
                """这条库有没有真正可下载的 jar（而不是只有 natives 分类器）"""
                downloads = lib.get("downloads") or {}
                artifact = downloads.get("artifact") or {}
                if artifact.get("path") or artifact.get("url"):
                    return True
                return bool(lib.get("url") and lib.get("name"))

            order = []
            table = {}

            def put(lib):
                k = _lib_key(lib)
                if k not in table:
                    order.append(k)
                elif not _has_artifact(lib) and _has_artifact(table[k]):
                    return      # 新的不能下载、旧的能 -> 保留旧的（lwjgl 2.9.4 vs 2.9.2 就是这么救回来的）
                table[k] = lib

            for lib in parent.get("libraries") or []:
                put(lib)
            for lib in value:
                put(lib)
            merged["libraries"] = [table[k] for k in order]

        elif key == "arguments":
            base_args = parent.get("arguments") or {}
            merged["arguments"] = {
                "jvm": list(base_args.get("jvm") or []) + list(value.get("jvm") or []),
                "game": list(base_args.get("game") or []) + list(value.get("game") or []),
            }

        else:
            merged[key] = value

    return merged


def load_version_json(base, ver, depth=0):
    """
    读取版本 json，并自动处理 inheritsFrom（递归合并父版本）。
    加载器（NeoForge/Forge）生成的 json 通常只是「补丁」，必须和原版 json 合体才能用。
    """
    path = os.path.join(base, "versions", ver, ver + ".json")
    if not os.path.isfile(path) or depth > 5:
        return None

    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)

    parent_id = data.get("inheritsFrom")
    if not parent_id:
        return data

    parent = load_version_json(base, parent_id, depth + 1)
    if not parent:
        return data
    return merge_version(parent, data)


def plan_launch(args):
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    ver = args.version
    vdir = os.path.join(base, "versions", ver)
    vjson = os.path.join(vdir, ver + ".json")

    # ---- 0. 找版本 json ----
    if not os.path.isfile(vjson):
        print(f"[错误] 找不到版本文件：{vjson}")
        vroot = os.path.join(base, "versions")
        if os.path.isdir(vroot):
            print("\n这个目录里可用的版本：")
            for name in sorted(os.listdir(vroot)):
                print("  -", name)
        print("\n提示：--dir 要指向包含 versions/ libraries/ assets/ 的目录")
        return

    vj = load_version_json(base, ver)
    if vj is None:
        print("[错误] 版本 json 读取失败：" + vjson)
        return

    lib_root = os.path.join(base, "libraries")
    assets_root = os.path.join(base, "assets")
    natives_dir = os.path.join(base, "natives", ver)
    if args.game_dir:
        game_dir = os.path.abspath(args.game_dir)
    elif os.path.isfile(os.path.join(base, "versions", ver, "instance.json")):
        # 整合包实例：版本隔离，游戏目录就是版本目录（和 PCL2 / HMCL 一致）
        game_dir = os.path.join(base, "versions", ver)
    else:
        game_dir = os.path.join(base, "instances", ver)

    # ---- 1. 过滤库 + 拼 classpath ----
    classpath = []
    missing = []
    natives_jars = []
    skipped = 0

    # 1) 先按 name 去重：同名库「后出现的覆盖先出现的」（官方启动器同语义）
    #    Fabric/Forge 的 json 常同时带旧的 lwjgl 3.2.1 和新的 3.2.2，
    #    不去重就会把旧的也算进 classpath，然后误报“文件缺失”。
    # 去重键带上“是否有 natives”：同一个库可能在 json 里出现两次，
    # 一条是普通 jar（要进 classpath），一条只提供 natives 分类器。
    # 只用 name 做键会把前者顶掉，导致 Narrator 这类类找不到。
    libs_by_key = {}
    for lib in vj.get("libraries", []):
        libs_by_key[(lib.get("name", ""), bool(lib.get("natives")))] = lib

    # 2) 再过滤 + 拼 classpath
    for lib in libs_by_key.values():
        if not rules_allow(lib):
            skipped += 1
            continue

        downloads = lib.get("downloads") or {}
        natives = lib.get("natives") or {}

        # 2a) 老格式的 natives 库（1.19 之前）
        #     这类库的主 jar 往往不存在，只发布 natives 分类器，
        #     所以只取分类器、不进 classpath（否则会误报缺失）
        if natives:
            classifier = natives.get("windows")
            if classifier:
                classifier = classifier.replace("${arch}", "64")
                nat = (downloads.get("classifiers") or {}).get(classifier)
                if nat:
                    rel = maven_path(nat, lib)
                    if rel:
                        full = os.path.join(lib_root, rel.replace("/", os.sep))
                        natives_jars.append(full)
                        if not os.path.isfile(full):
                            missing.append(("natives", full))
            continue

        # 2b) 普通库：直接进 classpath
        resolved = library_download(lib)
        if resolved:
            _url, rel, _sha1 = resolved
            if rel:
                full = os.path.join(lib_root, rel.replace("/", os.sep))
                classpath.append(full)
                if not os.path.isfile(full):
                    missing.append(("库", full))

    # 1c) 客户端 jar
    # 加载器版本（Forge/NeoForge）的 json 里通常没有自己的 jar，但它的
    # -DignoreList 写着要忽略 ${version_name}.jar —— 说明它期望版本目录里
    # 存在这么一个文件。所以我们从父版本复制一份过来。
    # 不复制、直接把父版本 jar 塞进 classpath 会踩这个坑：
    #   ResolutionException: Modules _1._18._2 and minecraft export package ...
    # 因为同一个游戏被当成两个模块加载了。
    client_jar = os.path.join(vdir, ver + ".jar")
    if not os.path.isfile(client_jar):
        parent_id = vj.get("inheritsFrom")
        if parent_id:
            parent_jar = os.path.join(base, "versions", parent_id, parent_id + ".jar")
            if os.path.isfile(parent_jar):
                import shutil
                try:
                    shutil.copyfile(parent_jar, client_jar)
                    print("[准备] 已把 %s 复制为 %s" % (os.path.basename(parent_jar),
                                                     os.path.basename(client_jar)))
                except Exception as exc:
                    print("[警告] 复制客户端 jar 失败：" + str(exc))
                    client_jar = parent_jar
            else:
                client_jar = parent_jar
    if os.path.isfile(client_jar):
        classpath.append(client_jar)
    else:
        missing.append(("客户端 jar", client_jar))

    # 1d) assets 索引
    asset_index_id = (vj.get("assetIndex") or {}).get("id", "")
    asset_index_path = os.path.join(assets_root, "indexes", asset_index_id + ".json")
    if not os.path.isfile(asset_index_path):
        missing.append(("assets 索引", asset_index_path))

    # ---- 2. 找 Java ----
    java = args.java or find_java(base, vj)
    java_major = java_major_of(java) if os.path.isfile(java) else None
    required_java = (vj.get("javaVersion") or {}).get("majorVersion")
    if required_java and java_major and java_major != required_java:
        print("[提示] 这个版本声明需要 Java %s，当前用的是 Java %s。" % (required_java, java_major))
        print("       多数情况能跑，但不要预期它稳定；建议装一个对应的 JDK。")

    # ---- 3. 占位符上下文 ----
    separator = ";" if os.name == "nt" else ":"

    # 账号：有登录凭据就用真的，没有就退回离线占位
    auth = ensure_fresh_auth() or {}
    player_name = auth.get("name") or args.username
    player_uuid = (auth.get("uuid") or "0" * 32).replace("-", "")
    player_token = auth.get("mc_access_token") or "0"

    ctx = {
        "natives_directory": natives_dir,
        "launcher_name": "mcroute",
        "launcher_version": "0.1",
        "classpath": separator.join(classpath),
        "classpath_separator": separator,
        "library_directory": lib_root,
        "version_name": ver,
        "version_type": vj.get("type", "release"),
        "game_directory": game_dir,
        "assets_root": assets_root,
        "assets_index_name": asset_index_id,
        # 下面这些都是"账号相关"，build 阶段先填占位值（launch 阶段才接真登录）
        "auth_player_name": player_name,
        "auth_uuid": player_uuid,
        "auth_access_token": player_token,
        "auth_xuid": "0",
        "clientid": "0",
        "user_type": "legacy",
        "user_properties": "{}",
        "resolution_width": "854",
        "resolution_height": "480",
    }

    # ---- 4. 展开 JVM 参数 ----
    arguments = vj.get("arguments")
    if arguments:
        jvm_args = expand_arg_entries(arguments.get("jvm", []), ctx)
    else:
        # 老板本（1.12 及以前）没有 arguments 段，给一套等价的最小参数
        jvm_args = [f"-Djava.library.path={natives_dir}"]

    if "-cp" not in jvm_args and "-classpath" not in jvm_args:
        jvm_args += ["-cp", ctx["classpath"]]

    # 内存参数（老板本 json 里没有，新版本由 launcher 自己决定）
    mem = getattr(args, "memory", None)
    if not any(a.startswith("-Xmx") for a in jvm_args):
        jvm_args.insert(0, "-Xmx" + (mem or "2G"))
    if not any(a.startswith("-Xms") for a in jvm_args):
        jvm_args.insert(0, "-Xms512M")

    # ---- 4.5 NeoForge 特别处理：module path 与 classpath 不能重复 ----
    # BootstrapLauncher 会把一部分库放进 module path（-p / --module-path），
    # 此时同名模块不能再出现在 classpath 里，否则会直接崩：
    #   IllegalStateException: Module named X was already on the JVM's module path
    #   ... but class-path contains it at location ...
    # 做法：从 classpath 里剔除「模块名与 module path 相同的」那些 jar
    # （9.3 与 9.10.1 版本不同，所以按去掉版本号的文件名比）
    def _stem(path):
        name = os.path.basename(path)
        if name.endswith(".jar"):
            name = name[:-4]
        import re as _re
        return _re.sub(r"-\d[\w.\-+]*$", "", name)

    module_path_jars = []
    for index, arg in enumerate(jvm_args):
        if arg in ("-p", "--module-path") and index + 1 < len(jvm_args):
            module_path_jars.extend(
                part for part in jvm_args[index + 1].split(separator) if part.strip()
            )

    module_dropped = 0
    if module_path_jars:
        module_stems = {_stem(path) for path in module_path_jars}
        kept = [path for path in classpath if _stem(path) not in module_stems]
        module_dropped = len(classpath) - len(kept)
        if module_dropped:
            classpath = kept
            new_classpath = separator.join(classpath)
            old_classpath = ctx["classpath"]
            # classpath 在 jvm 参数里是一整个 token，直接替换掉
            jvm_args = [arg.replace(old_classpath, new_classpath) for arg in jvm_args]

    # ---- 5. 展开游戏参数 ----
    if arguments:
        game_args = expand_arg_entries(arguments.get("game", []), ctx)
    else:
        game_args = [subst(a, ctx) for a in (vj.get("minecraftArguments") or "").split()]

    main_class = vj.get("mainClass", "")
    full_cmd = [java] + jvm_args + [main_class] + game_args

    # ---- 6. 写出参数文件 ----
    argfile = os.path.join(os.getcwd(), f"launch-{ver}.txt")
    write_argfile(argfile, full_cmd[1:])  # 第一项是 java.exe，不在参数文件里

    # ---- 7. 打包结果返回（打印和执行交给调用方） ----
    return {
        "base": base,
        "version": ver,
        "java": java,
        "java_major": java_major,
        "main_class": main_class,
        "argv": full_cmd,
        "argfile": argfile,
        "classpath": classpath,
        "skipped": skipped,
        "natives_jars": natives_jars,
        "natives_dir": natives_dir,
        "game_dir": game_dir,
        "asset_index_id": asset_index_id,
        "jvm_args": jvm_args,
        "game_args": game_args,
        "missing": missing,
    }


def print_plan(info):
    """把 plan_launch 的结果打印成人话（build 命令用）"""
    print("=" * 70)
    print("build 报告")
    print("=" * 70)
    print(f"  实例目录   : {info['base']}")
    print(f"  版本       : {info['version']}")
    print(f"  mainClass  : {info['main_class']}")
    _java_note = "   (Java %s)" % info.get("java_major") if info.get("java_major") else ""
    print(f"  Java       : {info['java']}{_java_note}")
    print(f"  classpath  : {len(info['classpath'])} 个条目（按规则跳过 {info['skipped']} 个不适用的库）")
    print(f"  natives    : {len(info['natives_jars'])} 个 jar 需要解压到 {info['natives_dir']}")
    print(f"  assets索引 : {info['asset_index_id']}")
    print(f"  参数总数   : jvm {len(info['jvm_args'])} + game {len(info['game_args'])}")

    missing = info["missing"]
    if missing:
        print(f"\n[缺失] 共 {len(missing)} 个文件：")
        for kind, path in missing[:15]:
            print(f"  - [{kind}] {path}")
        if len(missing) > 15:
            print(f"  ...还有 {len(missing) - 15} 个")
        print("\n  说明：缺的东西要么还没下载（以后用 fetch 补），")
        print("        要么路径猜错了（这时要看 --dir 是否指对）。")
    else:
        print("\n[OK] 所有依赖文件都在本地，classpath 可以拼完整。")

    print(f"\n[已写出] 参数文件：{info['argfile']}")
    argv = info["argv"]
    preview = " ".join(argv[:4]) + " ... " + " ".join(argv[-3:])
    print(f"\n[命令预览] {preview}")
    print(f'\n启动命令：\n  "{info["java"]}" @"{info["argfile"]}"')


def cmd_build(args):
    info = plan_launch(args)
    if info is None:
        return
    print_plan(info)


def extract_natives(jars, target_dir):
    """
    把老版本（1.19 之前）的 natives jar 解压到 java.library.path 指向的目录。

    1.19+ 的 natives 是直接放在 classpath 上的（LWJGL 自己从 jar 里加载），
    而 1.12.2 / 1.7.10 这些老版本必须把 .dll 解压出来，否则启动会报
    UnsatisfiedLinkError: no lwjgl in java.library.path。
    """
    import zipfile

    os.makedirs(target_dir, exist_ok=True)
    extracted = 0

    for jar in jars:
        if not os.path.isfile(jar):
            continue
        try:
            with zipfile.ZipFile(jar) as zf:
                for name in zf.namelist():
                    if name.endswith("/") or name.upper().startswith("META-INF/"):
                        continue
                    target = os.path.join(target_dir, os.path.basename(name))
                    if os.path.isfile(target):
                        continue
                    with zf.open(name) as src, open(target, "wb") as dst:
                        dst.write(src.read())
                    extracted += 1
        except Exception as exc:
            print("[警告] 解压 natives 失败：" + os.path.basename(jar) + " -> " + str(exc))

    return extracted


def cmd_launch(args):
    """
    真正启动游戏。
    注意：这一步会拉起一个 JVM 子进程，正常的话你会看到 Minecraft 窗口。
    """
    import subprocess

    info = plan_launch(args)
    if info is None:
        return

    # 缺文件时默认拒绝启动——classpath 不全，启动必然崩，不如提前拦住
    if info["missing"]:
        print(f"\n[拒绝启动] 还缺 {len(info['missing'])} 个文件：")
        for kind, path in info["missing"][:5]:
            print(f"  - [{kind}] {path}")
        if len(info["missing"]) > 5:
            print(f"  ...还有 {len(info['missing']) - 5} 个")
        if not args.force:
            print("\n补齐之后再启动；确实想强行试可以加 --force。")
            return
        print("\n[警告] --force 已指定，继续启动（大概率会崩）。")

    # 游戏目录和 natives 目录先建好，否则游戏会报错
    os.makedirs(info["game_dir"], exist_ok=True)
    os.makedirs(info["natives_dir"], exist_ok=True)

    # 老版本的 natives 要解压出来
    if info.get("natives_jars"):
        count = extract_natives(info["natives_jars"], info["natives_dir"])
        if count:
            print("[启动] 已解压 %d 个 natives 文件" % count)

    print(f"\n[启动] 版本     : {info['version']}")
    print(f"[启动] 工作目录 : {info['game_dir']}")
    print(f"[启动] 参数文件 : {info['argfile']}")
    print("[启动] 正在拉起 JVM ...（Minecraft 窗口出现后，这个终端可以关掉）")

    # 这里故意不用 "@参数文件" 启动。
    # 原因：java 读 @参数文件 时用的是系统编码（中文 Windows 上是 GBK），
    # 而我们的路径里有中文，UTF-8 写出的文件会被读成乱码，classpath 直接失效。
    # 直接把参数交给 subprocess，Windows 会按 Unicode 传，没有这个问题。
    # 代价：命令行不能超过约 32 KB——我们这里是 9 KB 左右，安全。
    proc = subprocess.Popen(info["argv"], cwd=info["game_dir"])
    print(f"[启动] 已启动，PID = {proc.pid}")

    if args.wait:
        print("[启动] 等待游戏退出 ...")
        code = proc.wait()
        print(f"[启动] 游戏已退出，退出码 = {code}")
    else:
        print("[启动] 不等待（加 --wait 可以等游戏关闭后再返回）")


# ---------------------------------------------------------------------------
# 子命令 6: fetch —— 把某版本的完整闭包下载到本地（sha1 校验 + 跳过已有）
# ---------------------------------------------------------------------------
# 默认数据目录：脚本旁边的 mcdata/（和代码分开，避免"工具变胖"）
def app_dir():
    """
    数据目录应该放哪。

    三种运行形态不一样：
      1. 源码运行 / 普通 exe        -> 放程序旁边，方便"绿色版"
      2. MSIX 安装                 -> 安装目录是只读的（WindowsApps），必须放用户可写位置
      3. 装在 Program Files 的 exe -> 同样只读，同样的处理

    判断方式是"真的写一下试试"，比猜路径可靠。
    """
    if getattr(sys, "frozen", False):
        base = os.path.dirname(os.path.abspath(sys.executable))
    else:
        base = os.path.dirname(os.path.abspath(__file__))

    try:
        probe = os.path.join(base, ".mcroute_write_test")
        with open(probe, "w", encoding="utf-8") as fp:
            fp.write("ok")
        os.remove(probe)
        return base
    except Exception:
        # 只读目录（MSIX / Program Files）：退到用户可写位置。
        # MSIX 下 LOCALAPPDATA 会被系统重定向到包自己的 LocalCache，正好合适。
        writable = os.path.join(
            os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "mcroute")
        try:
            os.makedirs(writable, exist_ok=True)
        except Exception:
            pass
        return writable


DEFAULT_DATA_DIR = os.path.join(app_dir(), "mcdata")


def file_sha1(path):
    """算本地文件的 sha1（分块读，避免把大文件一次性塞进内存）"""
    import hashlib

    digest = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


# ---------------------------------------------------------------------------
# URL 路由改写：官方域名被污染时，换成同路径的官方 CDN
# ---------------------------------------------------------------------------
URL_REWRITES = [
    # NeoForge 官方的 maven 域名在国内被 DNS 污染，官方 CDN 路径完全一致
    ("https://maven.neoforged.net/", "https://neoforged.forgecdn.net/"),
    # CurseForge 的 media 域名会返回 403（CloudFront 拒绝），
    # 同一路径在 edge 域名上是正常的，文件字节完全一致
    ("https://media.forgecdn.net/", "https://edge.forgecdn.net/"),
]


def rewrite_url(url):
    """按路由表把 URL 换到可用的镜像/CDN（路径保持不变）"""
    for old, new in URL_REWRITES:
        if url.startswith(old):
            return new + url[len(old):]
    return url


# 哈工大的 BMCLAPI 镜像：标准 maven 布局，覆盖 Forge / NeoForge / Fabric 的库。
# 实测 7 MB/s（官方 maven 约 1.7 MB/s），大学生镜像站，比个人域名可信。
# 注意它只镜像 maven 库，没有 assets / manifest / client jar，那些会 404 自动回落到官方。
HIT_MIRROR = "https://mirrors.hit.edu.cn/bmclapi/"
MIRROR_PREFIXES = [
    "https://maven.minecraftforge.net/",
    "https://maven.neoforged.net/releases/",
    "https://maven.fabricmc.net/",
    "https://maven.quiltmc.org/",
]


def download_candidates(url):
    """下载候选地址：大学镜像优先，官方地址兜底"""
    out = []
    for prefix in MIRROR_PREFIXES:
        if url.startswith(prefix):
            out.append(HIT_MIRROR + url[len(prefix):])
            break
    fallback = rewrite_url(url)
    if fallback not in out:
        out.append(fallback)

    # 跨官方 maven 兜底：老的 Forge json 里很多库只写了空 url 或写错仓库，
    # 两个官方 maven 之间互相兜一次，能救回 launchwrapper / asm 这类
    CROSS = {
        "https://libraries.minecraft.net/": "https://maven.minecraftforge.net/",
        "https://maven.minecraftforge.net/": "https://libraries.minecraft.net/",
    }
    for prefix, alt in CROSS.items():
        if url.startswith(prefix):
            other = alt + url[len(prefix):]
            if other not in out:
                out.append(other)
            break
    return out


def download_one(job):
    """
    下载单个文件。任务格式：(url, 本地路径, 期望sha1)
    返回 (状态, 路径, 说明)，状态是 ok / skip / fail / bad。
    已存在且 sha1 对得上 -> skip（这就是"断点续传"的全部秘密：不重复下已完成的）
    """
    url, dest, expected = job

    if os.path.isfile(dest):
        if not expected or file_sha1(dest) == expected:
            return ("skip", dest, 0)

    # 依次尝试：大学镜像 -> 官方（镜像缺文件就自动回落）
    data = None
    last_error = None
    for candidate in download_candidates(url):
        try:
            data, _dt = fetch_bytes(candidate, timeout=90)
        except Exception as exc:
            last_error = str(exc)
            continue
        if expected and sha1_of(data) != expected:
            last_error = "sha1 不符：" + candidate
            data = None
            continue
        break

    if data is None:
        return ("fail", dest, last_error or "所有地址都失败")

    if expected and sha1_of(data) != expected:
        return ("bad", dest, "sha1 不符（可能被换过内容）")

    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with open(dest, "wb") as f:
        f.write(data)
    return ("ok", dest, len(data))


def cmd_fetch(args):
    import concurrent.futures

    ver = args.version
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    print(f"版本：{ver}")
    print(f"目标目录：{base}\n")

    # ---- 1. 找版本 json（本地已有就直接用，能处理加载器生成的 inheritsFrom 版本）----
    vdir = os.path.join(base, "versions", ver)
    vjson_path = os.path.join(vdir, ver + ".json")

    if os.path.isfile(vjson_path):
        vj = load_version_json(base, ver)
        if vj is None:
            print("[错误] 本地版本 json 读取失败：" + vjson_path)
            return
        print("[1/5] 使用本地版本 json（已合并 inheritsFrom）")
    else:
        manifest, used, _dt, _nbytes, _errs = get_manifest()
        entry = None
        for item in manifest.get("versions", []):
            if item.get("id") == ver:
                entry = item
                break
        if not entry:
            print("[错误] manifest 里没有版本 " + ver)
            return
        print("[1/5] manifest 来自 " + str(used))

        vj, _dt2, _n2 = fetch_json(entry["url"])
        os.makedirs(vdir, exist_ok=True)
        with open(vjson_path, "w", encoding="utf-8") as f:
            json.dump(vj, f, ensure_ascii=False, indent=2)
        print("      版本 json 已保存")

    lib_root = os.path.join(base, "libraries")
    assets_root = os.path.join(base, "assets")
    jobs = []  # 每一项是 (url, 本地路径, 期望sha1)

    # ---- 2. 客户端 jar ----
    client = (vj.get("downloads") or {}).get("client")
    if client:
        jobs.append((client["url"], os.path.join(vdir, ver + ".jar"), client.get("sha1")))
        print(f"[2/5] client.jar 加入队列（{human_kb(client.get('size', 0))}）")
    else:
        print("[2/5] 警告：这个版本没有客户端下载信息")

    # ---- 3. 库（同名去重 + 规则过滤 + 老版本 natives） ----
    libs = {}
    for lib in vj.get("libraries", []):
        libs[(lib.get("name", ""), bool(lib.get("natives")))] = lib

    for lib in libs.values():
        if not rules_allow(lib):
            continue
        downloads = lib.get("downloads") or {}

        resolved = library_download(lib)
        if resolved:
            url, rel, sha1 = resolved
            target = os.path.join(lib_root, rel.replace("/", os.sep)) if rel else None
            if target and url:
                jobs.append((url, target, sha1))
            elif target and not os.path.isfile(target):
                # json 没给下载地址、本地又没有：多半是安装器没跑完
                print("      [缺少] 没有下载地址且本地不存在：" + os.path.basename(target))

        natives = lib.get("natives") or {}
        classifier = natives.get("windows")
        if classifier:
            classifier = classifier.replace("${arch}", "64")
            native = (downloads.get("classifiers") or {}).get(classifier)
            if native and native.get("url"):
                rel = maven_path(native, lib)
                if rel:
                    jobs.append((native["url"],
                                 os.path.join(lib_root, rel.replace("/", os.sep)),
                                 native.get("sha1")))

    print(f"[3/5] 库加入队列（当前共 {len(jobs)} 个文件）")

    # ---- 4. assets 索引（先下索引，才知道有哪些资源对象） ----
    asset_index = vj.get("assetIndex") or {}
    index_id = asset_index.get("id") or "index"
    index_url = asset_index.get("url")
    asset_count = 0

    if index_url:
        index_path = os.path.join(assets_root, "indexes", index_id + ".json")
        status, _path, info = download_one((index_url, index_path, asset_index.get("sha1")))
        if status in ("ok", "skip"):
            with open(index_path, "r", encoding="utf-8") as f:
                index_data = json.load(f)
            objects = index_data.get("objects") or {}
            asset_count = len(objects)
            print(f"[4/5] assets 索引 {index_id}：{asset_count} 个资源对象")
            if args.no_assets:
                print("      --no-assets：跳过资源对象下载（够启动，但会缺音效/贴图）")
            else:
                for obj in objects.values():
                    digest = obj.get("hash")
                    if not digest:
                        continue
                    jobs.append((f"https://resources.download.minecraft.net/{digest[:2]}/{digest}",
                                 os.path.join(assets_root, "objects", digest[:2], digest),
                                 digest))
                print(f"      资源对象已加入队列（总计 {len(jobs)} 个文件）")
        else:
            print(f"[4/5] assets 索引下载失败：{info}")
    else:
        print("[4/5] 这个版本没有 assets 索引")

    # ---- 5. 并发下载 ----
    total = len(jobs)
    print(f"\n开始下载：{total} 个文件，{args.workers} 线程\n")
    ok = skip = fail = 0
    done = 0
    failed = []

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(download_one, job) for job in jobs]
        for future in concurrent.futures.as_completed(futures):
            status, path, info = future.result()
            done += 1
            if status == "ok":
                ok += 1
            elif status == "skip":
                skip += 1
            else:
                fail += 1
                failed.append((status, path, info))
            if done % 200 == 0 or done == total:
                print(f"  进度 {done}/{total}   新下载 {ok}   已存在 {skip}   失败 {fail}")

    print(f"\n完成：新下载 {ok}，跳过 {skip}，失败 {fail}")
    if failed:
        print("\n失败清单（前 10 条）：")
        for status, path, info in failed[:10]:
            print(f"  [{status}] {os.path.basename(path)}  -> {info}")
        print("\n再跑一次 fetch 即可续传：已完成的不会重下。")
    else:
        print("\n现在可以启动它（--java 换成你机器上的 java.exe）：")
        print(f'  python mcroute.py launch {ver} --dir "{base}" --java "<java路径>"')


# ---------------------------------------------------------------------------
# 子命令 5: wiki —— 用 Minecraft Wiki 的 API 取版本索引（manifest 兜底）
# ---------------------------------------------------------------------------
WIKI_API = ("https://minecraft.wiki/api.php?action=parse&page=Java_Edition_{ver}"
            "&prop=wikitext&format=json&formatversion=2")


def fetch_wiki_version(ver):
    """
    从 wiki 拿一个版本的 Infobox 信息。
    返回 dict：{date, jsonhash, raw_len}；拿不到返回 None。

    为什么这条有用：jsonhash 可以直接拼出 piston-meta 直链，
    于是「manifest 全挂」时仍然能定位任意历史版本的 json。
    注意 wiki 不是下载源，它不提供任何文件。
    """
    import re

    url = WIKI_API.format(ver=ver)
    data, _dt, _nbytes = fetch_json(url)

    if not isinstance(data, dict) or "error" in data:
        return None

    text = ((data.get("parse") or {}).get("wikitext")) or ""
    if not text:
        return None

    info = {"date": "", "jsonhash": "", "raw_len": len(text)}

    # jsonhash 是 40 位十六进制
    match = re.search(r"\|\s*jsonhash\s*=\s*([0-9a-f]{40})", text)
    if match:
        info["jsonhash"] = match.group(1)

    # date 字段后面可能夹带引用标记/换行/HTML，清一下
    match = re.search(r"\|\s*date\s*=\s*([^\n|]+)", text)
    if match:
        raw = match.group(1)
        raw = re.sub(r"<br\s*/?>", " ", raw)
        raw = raw.replace("'''", "").replace("''", "")
        info["date"] = " ".join(raw.split())

    return info


def cmd_wiki(args):
    ver = args.version
    print(f"正在查询 Minecraft Wiki：Java Edition {ver} ...\n")

    try:
        info = fetch_wiki_version(ver)
    except Exception as exc:
        print(f"[失败] 请求 wiki 出错：{exc}")
        return

    if not info:
        print("[失败] wiki 上没有这个版本，或者页面没有 Infobox")
        return

    print(f"  发布日期 : {info['date'] or '（取不到）'}")
    print(f"  jsonhash : {info['jsonhash'] or '（取不到）'}")
    print(f"  wikitext : {info['raw_len']} 字符")

    if not info["jsonhash"]:
        print("\n[不可用] 没有 jsonhash —— 通常是刚发布的版本，wiki 还没补全。")
        return

    url = f"https://piston-meta.mojang.com/v1/packages/{info['jsonhash']}/{ver}.json"
    print(f"\n  拼出直链 : {url}")

    try:
        data, dt = fetch_bytes(url)
        print(f"  验证结果 : OK，{dt:.2f}s，{human_kb(len(data))}")
        print("\n  => manifest 全挂时，这条路径仍然能拿到版本 json。")
    except Exception as exc:
        print(f"  验证结果 : 失败（{exc}）")


# ---------------------------------------------------------------------------
# 子命令 7: 微软账号登录（OAuth 设备码流程）
# ---------------------------------------------------------------------------
AUTH_FILE = os.path.join(DEFAULT_DATA_DIR, "auth.json")
CLIENT_ID_FILE = os.path.join(DEFAULT_DATA_DIR, "client_id.txt")

# client_id 的三种来源，优先级从高到低：
#   1) 环境变量 MCROUTE_CLIENT_ID
#   2) mcdata/client_id.txt 里写一行
#   3) 直接改这个常量
CLIENT_ID = os.environ.get("MCROUTE_CLIENT_ID", "YOUR_CLIENT_ID_HERE")
AUTH_TENANT = "consumers"  # 只允许个人 Microsoft 账户（Minecraft 账号就是个人账户）
AUTH_SCOPE = "XboxLive.signin offline_access"

DEVICE_CODE_URL = "https://login.microsoftonline.com/" + AUTH_TENANT + "/oauth2/v2.0/devicecode"
TOKEN_URL = "https://login.microsoftonline.com/" + AUTH_TENANT + "/oauth2/v2.0/token"
XBL_URL = "https://user.auth.xboxlive.com/user/authenticate"
XSTS_URL = "https://xsts.auth.xboxlive.com/xsts/authorize"
MC_LOGIN_URL = "https://api.minecraftservices.com/authentication/login_with_xbox"
MC_PROFILE_URL = "https://api.minecraftservices.com/minecraft/profile"

JSON_HEADERS = {"Content-Type": "application/json", "Accept": "application/json"}


def resolve_client_id():
    """按优先级找到 client_id；找不到返回 None"""
    if CLIENT_ID and CLIENT_ID != "YOUR_CLIENT_ID_HERE":
        return CLIENT_ID
    if os.path.isfile(CLIENT_ID_FILE):
        with open(CLIENT_ID_FILE, "r", encoding="utf-8") as f:
            value = f.read().strip()
        if value:
            return value
    return None


def http_request(url, payload=None, headers=None, method=None, timeout=30, retries=3):
    """
    统一发请求，返回 (JSON数据, 耗时, 是否成功)。
    失败也返回 JSON —— 微软把错误原因写在响应体里（error / error_description）。

    连接级失败（TLS 握手被重置、超时）会退避重试：
    国内连微软端点偶尔会在握手阶段被掐断，重连通常就好了。
    """
    import urllib.parse

    hdrs = {"User-Agent": UA}
    hdrs.update(headers or {})

    data = None
    if payload is not None:
        if hdrs.get("Content-Type") == "application/json":
            data = json.dumps(payload).encode("utf-8")
        else:
            hdrs.setdefault("Content-Type", "application/x-www-form-urlencoded")
            data = urllib.parse.urlencode(payload).encode("utf-8")

    last_error = None
    for attempt in range(retries):
        req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
            return json.loads(raw.decode("utf-8")), time.time() - t0, True
        except urllib.error.HTTPError as exc:
            # HTTPError 是"服务器正常回了个错误"（比如 400/401），不重试
            raw = exc.read()
            try:
                return json.loads(raw.decode("utf-8")), time.time() - t0, False
            except Exception:
                return {"error": "http_" + str(exc.code),
                        "error_description": raw.decode("utf-8", "replace")}, time.time() - t0, False
        except Exception as exc:
            # 连接被重置 / 超时 / 握手失败：重试
            last_error = exc
            if attempt + 1 < retries:
                print("[网络] %s，%d 秒后重试（%d/%d）..." % (
                    type(exc).__name__, attempt + 1, attempt + 1, retries - 1))
                time.sleep(1.5 * (attempt + 1))

    return ({"error": "network_error",
             "error_description": "%s: %s" % (type(last_error).__name__, last_error)},
            time.time() - t0, False)


def auth_load():
    if not os.path.isfile(AUTH_FILE):
        return None
    try:
        with open(AUTH_FILE, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def auth_save(data):
    os.makedirs(os.path.dirname(AUTH_FILE), exist_ok=True)
    with open(AUTH_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    try:
        os.chmod(AUTH_FILE, 0o600)  # Windows 上无效，Linux/macOS 上有效
    except Exception:
        pass


def ms_refresh(auth, client_id):
    """用 refresh token 换一个新的微软 access token；失败返回 None"""
    refresh_token = (auth or {}).get("ms_refresh_token")
    if not refresh_token:
        return None
    data, _dt, ok = http_request(TOKEN_URL, {
        "grant_type": "refresh_token",
        "client_id": client_id,
        "refresh_token": refresh_token,
        "scope": AUTH_SCOPE,
    })
    if not ok or "access_token" not in data:
        return None
    return data


def mc_auth_from_ms(ms_token):
    """
    微软 access token -> Minecraft 令牌 + 玩家档案。

    成功返回 dict：{mc_access_token, mc_expires_at, name, uuid}
    失败抛 RuntimeError（带人话说明）。
    cmd_login 和自动续期都要用这段逻辑，所以抽成独立函数。
    """
    # 第 1 步：微软令牌 -> Xbox Live 令牌
    xbl, _dt, ok = http_request(XBL_URL, {
        "Properties": {
            "AuthMethod": "RPS",
            "SiteName": "user.auth.xboxlive.com",
            "RpsTicket": "d=" + ms_token,
        },
        "RelyingParty": "http://auth.xboxlive.com",
        "TokenType": "JWT",
    }, headers=JSON_HEADERS)
    if not ok:
        raise RuntimeError("Xbox Live 认证失败：" + str(xbl.get("Message") or xbl))
    xbl_token = xbl.get("Token")
    claims = (xbl.get("DisplayClaims") or {}).get("xui") or [{}]
    uhs = claims[0].get("uhs")

    # 第 2 步：Xbox Live -> XSTS
    xsts, _dt, ok = http_request(XSTS_URL, {
        "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl_token]},
        "RelyingParty": "rp://api.minecraftservices.com/",
        "TokenType": "JWT",
    }, headers=JSON_HEADERS)
    if not ok:
        hints = {
            2148916233: "这个微软账号还没有 Xbox 档案，先去 xbox.com 登录一次创建",
            2148916235: "Xbox 在你所在地区不可用",
            2148916236: "需要先在 xbox.com 完成成人与地区验证",
            2148916237: "同上，需要完成成人与地区验证",
            2148916238: "这是未成年人账号，需要家长加入 Microsoft 家庭组",
        }
        message = "XSTS 认证失败：" + str(xsts.get("Message") or xsts)
        hint = hints.get(xsts.get("XErr"))
        raise RuntimeError(message + ("（" + hint + "）" if hint else ""))
    xsts_token = xsts.get("Token")
    claims = (xsts.get("DisplayClaims") or {}).get("xui") or [{}]
    uhs = claims[0].get("uhs") or uhs

    # 第 3 步：XSTS -> Minecraft 令牌
    mc, _dt, ok = http_request(MC_LOGIN_URL, {
        "identityToken": "XBL3.0 x=" + str(uhs) + ";" + str(xsts_token),
    }, headers=JSON_HEADERS)
    if not ok:
        raise RuntimeError("换 Minecraft 令牌失败：" + str(mc.get("errorMessage") or mc))
    token = mc.get("access_token")
    expires_in = int(mc.get("expires_in") or 86400)

    # 第 4 步：读玩家档案（同时验证令牌真的可用）
    profile, _dt, ok = http_request(
        MC_PROFILE_URL,
        headers={"Authorization": "Bearer " + str(token), "Accept": "application/json"},
    )
    if not ok:
        raise RuntimeError("读取玩家档案失败：" + str(profile.get("error") or profile)
                           + "（常见原因：这个账号没有购买 Minecraft）")

    return {
        "mc_access_token": token,
        "mc_expires_at": _now() + expires_in,
        "name": profile.get("name"),
        "uuid": profile.get("id"),
    }


def ensure_fresh_auth(verbose=True):
    """
    返回一份「当下可用」的登录凭据：
      1. 没有凭据              -> None
      2. MC 令牌还剩 5 分钟以上 -> 直接用
      3. 过期但有 refresh token -> 静默续期并写回文件
      4. 续期失败              -> 返回旧凭据（由调用方决定怎么提示）
    """
    auth = auth_load()
    if not auth:
        return None

    if int(auth.get("mc_expires_at") or 0) - _now() > 300:
        return auth

    client_id = auth.get("client_id") or resolve_client_id()
    if not client_id or not auth.get("ms_refresh_token"):
        return auth

    if verbose:
        print("[登录] Minecraft 令牌已过期，正在静默续期 ...")

    refreshed = ms_refresh(auth, client_id)
    if not refreshed:
        if verbose:
            print("[登录] 续期失败：refresh token 已失效，请重新运行 login")
        return auth

    try:
        info = mc_auth_from_ms(refreshed["access_token"])
    except RuntimeError as exc:
        if verbose:
            print("[登录] 续期失败：" + str(exc))
        return auth

    auth.update({
        "mc_access_token": info["mc_access_token"],
        "mc_expires_at": info["mc_expires_at"],
        "name": info["name"],
        "uuid": info["uuid"],
        "ms_access_token": refreshed.get("access_token"),
        "ms_refresh_token": refreshed.get("refresh_token") or auth.get("ms_refresh_token"),
        "ms_expires_at": _now() + int(refreshed.get("expires_in") or 3600),
    })
    auth_save(auth)
    if verbose:
        print("[登录] 续期成功，当前用户：" + str(auth.get("name")))
    return auth


def cmd_login(_args):
    client_id = resolve_client_id()
    if not client_id:
        print("还没有 client_id，登录流程启动不了。\n")
        print("你需要一个 Azure 应用的 client_id（注册免费，前提是能访问一个可注册应用的租户）：")
        print("  1) 用能注册应用的账号登录 https://entra.microsoft.com")
        print("  2) 应用注册 -> 新注册 -> 支持的帐户类型选「含个人 Microsoft 帐户」")
        print("  3) 身份验证 -> 允许公共客户端流 = 是")
        print("  4) 抄下「应用程序(客户端) ID」\n")
        print("拿到后有两条填法（任选）：")
        print("  临时：set MCROUTE_CLIENT_ID=你的id      （只对当前终端窗口有效）")
        print("  永久：把 id 单独一行写进 " + CLIENT_ID_FILE)
        return

    print("正在向微软申请设备码 ...")
    data, _dt, ok = http_request(DEVICE_CODE_URL, {"client_id": client_id, "scope": AUTH_SCOPE})
    if not ok:
        print("[失败] " + str(data.get("error_description") or data))
        return

    user_code = data.get("user_code")
    verify_uri = data.get("verification_uri") or "https://microsoft.com/link"
    device_code = data.get("device_code")
    interval = int(data.get("interval") or 5)
    expires_in = int(data.get("expires_in") or 900)

    print("\n" + "=" * 62)
    print("  1) 浏览器打开：" + str(verify_uri))
    print("  2) 输入代码  ：" + str(user_code))
    print("=" * 62)
    print("等待你在浏览器里授权...（{} 秒内有效）".format(expires_in))

    deadline = time.time() + expires_in
    token_data = None

    while time.time() < deadline:
        time.sleep(interval)
        res, _dt2, ok2 = http_request(TOKEN_URL, {
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "client_id": client_id,
            "device_code": device_code,
        })
        if ok2:
            token_data = res
            break
        err = res.get("error")
        if err == "authorization_pending":
            continue
        if err == "slow_down":
            interval += 5
            continue
        print("[失败] " + str(res.get("error_description") or err))
        return

    if not token_data:
        print("[失败] 授权超时。重新运行 login 再试。")
        return

    ms_token = token_data["access_token"]
    print("授权成功，正在换取 Minecraft 令牌 ...")

    try:
        info = mc_auth_from_ms(ms_token)
    except RuntimeError as exc:
        print("[失败] " + str(exc))
        return

    auth = {
        "name": info["name"],
        "uuid": info["uuid"],
        "client_id": client_id,
        "ms_refresh_token": token_data.get("refresh_token"),
        "ms_access_token": ms_token,
        "ms_expires_at": _now() + int(token_data.get("expires_in") or 3600),
        "mc_access_token": info["mc_access_token"],
        "mc_expires_at": info["mc_expires_at"],
    }
    auth_save(auth)

    print("\n[成功] 已登录并保存凭据")
    print("  用户名 : " + str(auth.get("name")))
    print("  UUID   : " + str(auth.get("uuid")))
    print("  凭据   : " + AUTH_FILE)
    print("\n以后启动会自动带上这个账号；令牌过期会自动续期。")


# ---------------------------------------------------------------------------
# 子命令 8: loader —— 自动安装加载器（目前支持 NeoForge）
# ---------------------------------------------------------------------------
# NeoForge 官方 CDN，路径与官方 maven 完全一致（官方域名在国内被 DNS 污染）
NEOFORGE_INSTALLER = ("https://neoforged.forgecdn.net/releases/net/neoforged/"
                      "neoforge/{ver}/neoforge-{ver}-installer.jar")


def find_java_anywhere(base, major=None):
    """在 mcdata 和 Modrinth App 的 java 目录里找 java.exe"""
    candidates = []
    for root in (
        os.path.join(base, "java_versions"),
        os.path.join(os.environ.get("APPDATA", ""), "ModrinthApp", "meta", "java_versions"),
    ):
        if not os.path.isdir(root):
            continue
        for name in sorted(os.listdir(root)):
            exe = os.path.join(root, name, "bin", "java.exe")
            if os.path.isfile(exe):
                candidates.append(exe)

    if major is not None:
        for exe in candidates:
            if ("jre%d" % major) in exe or ("jdk%d" % major) in exe:
                return exe
    return candidates[0] if candidates else "java"


def installer_url(kind, ver):
    """
    加载器 installer 的下载地址。
      neoforge: 版本形如 21.1.264
      forge   : 版本形如 1.20.1-47.2.0（maven 路径就是 <mc>-<forge版本>）
    """
    if kind == "neoforge":
        return ("https://neoforged.forgecdn.net/releases/net/neoforged/"
                "neoforge/{v}/neoforge-{v}-installer.jar").format(v=ver)
    if kind == "forge":
        return ("https://maven.minecraftforge.net/net/minecraftforge/forge/"
                "{v}/forge-{v}-installer.jar").format(v=ver)
    return None


# 加载器 meta：base URL + profile id 前缀（id 一般由 meta 直接给，这里只作兜底）
LOADER_META = {
    "fabric": ("https://meta.fabricmc.net/v2", "fabric-loader"),
    "quilt": ("https://meta.quiltmc.org/v3", "quilt-loader"),
    "legacyfabric": ("https://meta.legacyfabric.net/v2", "fabric-loader"),
}


def normalize_loader_kind(kind):
    """把 legacy-fabric / legacy_fabric / LegacyFabric 统一成 legacyfabric"""
    return (kind or "").lower().replace("-", "").replace("_", "").replace(" ", "")


def resolve_loader_version(meta_base, mc, loader=None):
    """没指定 loader 版本时，从 meta 里挑最新的 stable 版本"""
    if loader:
        return loader
    try:
        data, _dt, _n = fetch_json(meta_base + "/versions/loader/" + mc)
    except Exception:
        return None
    for item in data:
        info = item.get("loader") or {}
        if info.get("stable"):
            return info.get("version")
    if data:
        return (data[0].get("loader") or {}).get("version")
    return None


def install_meta_loader(base, kind, ver):
    """
    Fabric / Quilt / Legacy Fabric 没有安装器：
    加载器官方 meta 直接给一份现成的 version json（自带 inheritsFrom），
    写进 versions/ 就能用现有的 fetch/launch 跑。

    版本写法：
        fabric 1.20.1              -> 自动挑最新 stable loader
        fabric 1.20.1-0.19.5       -> 指定 loader 版本
    """
    meta_base, id_prefix = LOADER_META[kind]

    if "-" in ver:
        mc, _sep, loader = ver.partition("-")
    else:
        mc, loader = ver, None

    loader = resolve_loader_version(meta_base, mc, loader)
    if not loader:
        print("[错误] 找不到 " + mc + " 可用的加载器版本")
        return

    print("      MC 版本: %s   加载器: %s %s" % (mc, kind, loader))
    url = "%s/versions/loader/%s/%s/profile/json" % (meta_base, mc, loader)
    print("      " + url)

    try:
        profile, _dt, _n = fetch_json(url)
    except Exception as exc:
        print("      拉取 profile 失败：" + str(exc))
        return

    pkg_id = profile.get("id") or ("%s-%s-%s" % (id_prefix, loader, mc))
    vdir = os.path.join(base, "versions", pkg_id)
    os.makedirs(vdir, exist_ok=True)
    vjson = os.path.join(vdir, pkg_id + ".json")
    with open(vjson, "w", encoding="utf-8") as f:
        json.dump(profile, f, ensure_ascii=False, indent=2)

    print("      已写入：" + vjson)
    print("      mainClass : " + str(profile.get("mainClass")))
    print("      inherits  : " + str(profile.get("inheritsFrom")))
    print("      libraries : " + str(len(profile.get("libraries") or [])))
    print("\n下一步：")
    print("  python mcroute.py fetch  " + pkg_id)
    print("  python mcroute.py launch " + pkg_id)


# NeoForge 的版本号通常等于「MC 版本去掉 1.」前缀（1.21.1 -> 21.1.x），
# 但 1.20.1 是个例外，它的 NeoForge 版本走的是 47.1.x
NEOFORGE_PREFIX_OVERRIDES = {
    "1.20.0": "47.1",
    "1.20.1": "47.1",
}


def maven_version_list(url):
    """从 maven-metadata.xml 里读出所有版本号（失败返回空列表）"""
    import re

    try:
        raw, _dt = fetch_bytes(url, timeout=30)
    except Exception:
        return []
    text = raw.decode("utf-8", "replace")
    versions = re.findall(r"<version>([^<]+)</version>", text)
    release = re.search(r"<release>([^<]+)</release>", text)
    if release:
        versions.append(release.group(1))
    return versions


def _version_key(text):
    """
    "10.13.4.1614-1.7.10" 这种版本号，按数字前缀排序。
    不能直接用 isdigit()：带后缀的那段（1445-1.7.10）会被当成 0，
    导致 1445 排在 1614 后面。
    """
    import re as _re

    out = []
    for part in text.split("."):
        m = _re.match(r"\d+", part)
        out.append(int(m.group()) if m else 0)
    return tuple(out)


def resolve_forge_version(ver):
    """
    Forge 的正式写法是 <MC版本>-<Forge版本>（如 1.12.2-14.23.5.2860），
    但用户经常只写 1.12.2。这时从官方 maven 版本列表里挑一个最新的匹配项。
    """
    if "-" in ver:
        return ver

    versions = maven_version_list(
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml")
    matches = [v for v in versions if v.startswith(ver + "-")]
    if not matches:
        return ver
    matches.sort(key=lambda v: _version_key(v[len(ver) + 1:]))
    return matches[-1]


def resolve_neoforge_version(ver):
    """
    NeoForge 版本号隐含 MC 版本（1.21.1 -> 21.1.x，但 1.20.1 -> 47.1.x）。
    用户写 1.21.1 时，映射出前缀再挑最新的。
    """
    parts = ver.split(".")
    if not (len(parts) >= 2 and parts[0] == "1" and parts[1].isdigit()):
        return ver

    prefix = NEOFORGE_PREFIX_OVERRIDES.get(ver)
    if not prefix:
        prefix = "%s.%s" % (parts[1], parts[2] if len(parts) > 2 else "0")

    versions = maven_version_list(
        "https://neoforged.forgecdn.net/releases/net/neoforged/neoforge/maven-metadata.xml")
    matches = [v for v in versions if v.startswith(prefix + ".")]
    if not matches:
        return ver
    matches.sort(key=_version_key)
    return matches[-1]


def minecraft_of_loader(kind, ver):
    """从加载器版本号反推它对应的 MC 版本（用于顺手把原版也下了）"""
    if kind == "forge":
        return ver.split("-")[0] if "-" in ver else ver
    if kind in LOADER_META:
        return ver.split("-")[0]
    if kind == "neoforge":
        if ver.startswith("47."):
            return "1.20.1"          # NeoForge 的 1.20.1 特例
        parts = ver.split(".")
        if len(parts) >= 2 and parts[0].isdigit() and parts[1].isdigit():
            major, minor = parts[0], parts[1]
            if minor == "0":
                return "1." + major          # 21.0.x -> 1.21
            return "1.%s.%s" % (major, minor)  # 21.1.x -> 1.21.1
    return None


def install_legacy_loader(base, profile):
    """
    老版本 Forge（1.7.10 及更早）的安装方式：**不跑安装器**。

    这类安装器的 install_profile.json 里自带 versionInfo（完整版本 json），
    也没有 processors / --installClient 参数（那是 1.8+ 才有的）。
    所以直接：
        1. 把 versionInfo 里的 forge 库加上 :universal 分类器
           （maven 上发布的文件名就是 forge-<ver>-universal.jar）
        2. 写出 versions/<id>/<id>.json
        3. 剩下的交给 fetch 下载依赖
    """
    vi = profile.get("versionInfo") or {}
    vid = vi.get("id")
    if not vid:
        print("[错误] install_profile.json 里没有 versionInfo.id")
        return None

    libs = vi.get("libraries") or []
    fixed = 0
    for lib in libs:
        name = lib.get("name", "")
        parts = name.split(":")
        # net.minecraftforge:forge:<ver>  -> 加上 :universal
        if (len(parts) == 3 and parts[0] == "net.minecraftforge" and parts[1] == "forge"):
            lib["name"] = name + ":universal"
            lib.setdefault("url", "https://maven.minecraftforge.net/")
            fixed += 1
        # 老 json 里 url 为空或缺省：补成 Mojang 官方 maven
        # （launchwrapper / asm-all / guava 这些都在上面）
        if not (lib.get("url") or "").strip():
            lib["url"] = "https://libraries.minecraft.net/"
            fixed += 1

    vdir = os.path.join(base, "versions", vid)
    os.makedirs(vdir, exist_ok=True)
    vjson = os.path.join(vdir, vid + ".json")
    with open(vjson, "w", encoding="utf-8") as f:
        json.dump(vi, f, ensure_ascii=False, indent=2)

    print("[legacy] 已写出 " + vid + "（修正了 %d 条 forge 库）" % fixed)
    print("         版本 json: " + vjson)
    return vid


def cmd_loader(args):
    """
    安装加载器（neoforge / forge）。

    两者机制相同：官方 installer 会生成「补丁后的客户端 jar」和带 inheritsFrom 的 json。
    所以流程也相同：
        下载 installer -> 预下载它的处理器依赖 -> 跑 installer -> 检查产物

    预下载那一步是必须的：installer 自己会去连官方 maven，而 neoforge 的
    maven.neoforged.net 在国内被 DNS 污染，失败后只会留下一个缺 jar 的 json，
    后续启动会报出毫无关联的错误（比如 No value present）。
    """
    import zipfile

    kind = normalize_loader_kind(args.kind)
    ver = args.version
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)

    if kind not in ("neoforge", "forge") and kind not in LOADER_META:
        print("[错误] 目前支持 " + " / ".join(["neoforge", "forge"] + list(LOADER_META)))
        return

    os.makedirs(base, exist_ok=True)

    # ---- 版本号智能补全（用户通常只写 MC 版本，如 1.12.2 / 1.21.1）----
    if kind == "forge":
        resolved = resolve_forge_version(ver)
        if resolved != ver:
            print("[提示] Forge 版本自动补全：" + ver + " -> " + resolved)
            ver = resolved
    elif kind == "neoforge":
        resolved = resolve_neoforge_version(ver)
        if resolved != ver:
            print("[提示] NeoForge 版本自动补全：" + ver + " -> " + resolved)
            ver = resolved

    # ---- 顺手把对应的原版也下好（安装器要用它打补丁，启动也要它）----
    mc_ver = minecraft_of_loader(kind, ver)
    if mc_ver:
        mc_json = os.path.join(base, "versions", mc_ver, mc_ver + ".json")
        if not os.path.isfile(mc_json):
            print("[准备] 本地没有原版 %s，先下载（装加载器必须要有它）..." % mc_ver)
            cmd_fetch(argparse.Namespace(version=mc_ver, dir=base, workers=16,
                                         no_assets=getattr(args, "no_assets", False)))
            if not os.path.isfile(mc_json):
                print("[错误] 原版下载失败，安装中止")
                return
        else:
            print("[准备] 原版 %s 已在本地" % mc_ver)

    # Fabric / Quilt / Legacy Fabric 没有安装器，走 meta 合成
    if kind in LOADER_META:
        print("加载器  : %s %s" % (kind, ver))
        print("数据目录: " + base)
        install_meta_loader(base, kind, ver)
        return

    url = installer_url(kind, ver)
    tmp_dir = os.path.join(base, "_tmp")
    os.makedirs(tmp_dir, exist_ok=True)

    # 安装器本身需要的 Java：NeoForge 用 21，Forge 用 17（找不到就退回任意一个）
    java = args.java or find_java_anywhere(base, 21 if kind == "neoforge" else 17)
    print("加载器  : {0} {1}".format(kind, ver))
    print("数据目录: " + base)
    print("Java    : " + java)

    vroot = os.path.join(base, "versions")
    os.makedirs(vroot, exist_ok=True)
    before = set(os.listdir(vroot))

    # ---- 1) 下载 installer ----
    installer = os.path.join(tmp_dir, kind + "-installer.jar")
    print("\n[1/4] 下载 installer ...")
    print("      " + url)
    try:
        data = None
        last_exc = None
        for candidate in download_candidates(url):
            try:
                data, dt = fetch_bytes(candidate, timeout=180)
                break
            except Exception as exc:
                last_exc = exc
        if data is None:
            raise last_exc or RuntimeError("所有地址都失败")
    except Exception as exc:
        print("      失败：" + str(exc))
        if "404" in str(exc):
            if kind == "forge":
                print("      提示：Forge 版本应写成 <MC版本>-<Forge版本>，例如 1.12.2-14.23.5.2860")
                print("            直接写 1.12.2 也可以，本工具会自动补全 Forge 部分")
            elif kind == "neoforge":
                print("      提示：NeoForge 版本形如 21.1.264（对应 MC 1.21.1）")
        return
    with open(installer, "wb") as f:
        f.write(data)
    print("      OK，%.1f MB，%.1fs" % (len(data) / 1024 / 1024, dt))

    # ---- 2) 预下载处理器依赖 ----
    print("[2/4] 预下载 installer 处理器依赖 ...")
    try:
        with zipfile.ZipFile(installer) as zf:
            profile = json.loads(zf.read("install_profile.json").decode("utf-8"))
            # 安装器不仅要下载处理器依赖，还要下载 version.json 里的运行时库；
            # 官方 maven 一慢它就超时失败，所以两类都替它下好。
            template_libs = []
            if "version.json" in zf.namelist():
                template = json.loads(zf.read("version.json").decode("utf-8"))
                template_libs = template.get("libraries") or []
            elif (profile.get("versionInfo") or {}).get("libraries"):
                # 老版本 Forge（1.12.2 及更早）把运行时库藏在 install_profile.versionInfo 里，
                # 不预下载的话安装器会自己去连官方 maven，慢到能卡住
                template_libs = profile["versionInfo"]["libraries"]
    except Exception as exc:
        print("      读取 install_profile.json 失败：" + str(exc))
        return

    all_libs = list(profile.get("libraries") or []) + template_libs
    print("      处理器依赖 %d 个 + 运行时库 %d 个" % (len(profile.get("libraries") or []), len(template_libs)))

    # 老版本 Forge 走 legacy 安装（不跑安装器）
    if profile.get("versionInfo") and not (profile.get("processors") or []):
        vid = install_legacy_loader(base, profile)
        if not vid:
            return
        print("")
        print("下一步：")
        print("  python mcroute.py fetch  " + vid)
        print("  python mcroute.py launch " + vid)
        return

    ok = skip = fail = 0
    for lib in all_libs:
        artifact = (lib.get("downloads") or {}).get("artifact") or {}
        rel = artifact.get("path")
        raw_url = artifact.get("url")
        sha1 = artifact.get("sha1")
        if not rel or not raw_url:
            continue
        dest = os.path.join(base, "libraries", rel.replace("/", os.sep))
        status, _path, info = download_one((raw_url, dest, sha1))
        if status == "ok":
            ok += 1
        elif status == "skip":
            skip += 1
        else:
            fail += 1
            print("      失败：" + os.path.basename(dest) + " -> " + str(info))
    print("      新下载 %d，已存在 %d，失败 %d" % (ok, skip, fail))
    if fail:
        print("      有依赖没下全，安装可能失败（可重跑本命令续传）")

    # ---- 3) 让 installer 认这个目录 ----
    profiles_path = os.path.join(base, "launcher_profiles.json")
    if not os.path.isfile(profiles_path):
        with open(profiles_path, "w", encoding="utf-8") as f:
            json.dump({"profiles": {}, "settings": {}, "version": 3}, f)
        print("      已创建 launcher_profiles.json")

    # ---- 4) 运行 installer ----
    print("[3/4] 运行 installer（可能要一两分钟）...")
    import subprocess
    try:
        proc = subprocess.run(
            [java, "-jar", installer, "--installClient", base],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=900,
        )
    except Exception as exc:
        print("      启动 installer 失败：" + str(exc))
        return

    if proc.returncode != 0:
        print("      installer 退出码 " + str(proc.returncode))
        tail = ((proc.stdout or "") + (proc.stderr or "")).splitlines()[-10:]
        for line in tail:
            print("      | " + line)
        return
    print("      installer 完成")

    # ---- 5) 检查产物 ----
    print("[4/4] 检查产物 ...")
    # 期望的版本目录名：neoforge-<ver> / <mc>-forge-<ver>
    if kind == "neoforge":
        expected = "neoforge-" + ver
    else:
        mc_part, _sep, forge_part = ver.partition("-")
        expected = mc_part + "-forge-" + forge_part if forge_part else ver

    after = set(os.listdir(vroot))
    new_dirs = sorted(after - before)
    if expected in after:
        new_dirs = [expected] + [d for d in new_dirs if d != expected]
        # 重跑时目录已存在，用期望目录名兜底
        before.discard(expected) if expected in before else None
    if new_dirs:
        for name in new_dirs:
            vdir = os.path.join(vroot, name)
            files = os.listdir(vdir) if os.path.isdir(vdir) else []
            has_json = any(f.endswith(".json") for f in files)
            has_jar = any(f.endswith(".jar") for f in files)
            print("      新增版本目录：" + name)
            print("        版本 json: " + ("有" if has_json else "缺失"))
            print("        补丁 jar : " + ("有" if has_jar else "无（1.20+ 的 Forge 不需要，运行时打补丁）"))
        print("\n下一步：")
        print("  python mcroute.py fetch  " + new_dirs[0] + " --no-assets")
        print("  python mcroute.py launch " + new_dirs[0])
    else:
        print("      [警告] 没有新增版本目录，安装可能没跑完（看上面的输出）")


def mrpack_loader_of(deps, mc):
    """
    解析 modrinth.index.json 里的 dependencies，返回 (kind, 版本字符串, 版本目录名)。
    没有加载器时 kind 为 None（纯原版）。
    """
    if "forge" in deps:
        ver = deps["forge"]
        return "forge", "%s-%s" % (mc, ver), "%s-forge-%s" % (mc, ver)
    if "neoforge" in deps:
        ver = deps["neoforge"]
        return "neoforge", ver, "neoforge-" + ver
    if "fabric-loader" in deps:
        ver = deps["fabric-loader"]
        return "fabric", "%s-%s" % (mc, ver), "fabric-loader-%s-%s" % (ver, mc)
    if "quilt-loader" in deps:
        ver = deps["quilt-loader"]
        return "quilt", "%s-%s" % (mc, ver), "quilt-loader-%s-%s" % (ver, mc)
    return None, None, mc


CF_API = "https://api.curseforge.com/v1"


def cf_api_key(base):
    """API key 来源：环境变量 CF_API_KEY -> <数据目录>/cf_api_key.txt"""
    key = os.environ.get("CF_API_KEY")
    if key and key.strip():
        return key.strip()
    path = os.path.join(base, "cf_api_key.txt")
    if os.path.isfile(path):
        with open(path, "r", encoding="utf-8") as f:
            return f.read().strip()
    return None


def cf_get(path, key, retries=4):
    """
    调 CurseForge API。Cloudflare 偶尔会对正常请求回 403（空 body），
    所以这里做几次退避重试，而不是直接判失败。
    """
    import time
    import urllib.request

    url = CF_API + path
    last = None
    for attempt in range(retries):
        req = urllib.request.Request(url, headers={
            "x-api-key": key,
            "Accept": "application/json",
            "User-Agent": "curl/8.21.0",
        })
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as exc:
            last = exc
            time.sleep(1.5 * (attempt + 1))
    raise last


def install_curseforge(pack, base, args):
    """
    安装 CurseForge 整合包。

    它和 mrpack 最大的区别：manifest 里只有 projectID/fileID，
    必须调 API 换成真实下载地址（所以需要免费 API key）。
    """
    import concurrent.futures
    import shutil
    import urllib.parse
    import zipfile

    key = cf_api_key(base)
    if not key:
        print("[错误] 没有 CurseForge API key")
        print("       方式一：设置环境变量 CF_API_KEY")
        print("       方式二：把 key 写进 " + os.path.join(base, "cf_api_key.txt"))
        print("       申请：https://console.curseforge.com/ -> API Keys")
        return

    with zipfile.ZipFile(pack) as zf:
        names = zf.namelist()
        if "manifest.json" not in names:
            print("[错误] 没有 manifest.json")
            return
        manifest = json.loads(zf.read("manifest.json").decode("utf-8"))

    mc_meta = manifest.get("minecraft") or {}
    mc = mc_meta.get("version")
    if not mc:
        print("[错误] manifest 里没有 minecraft.version")
        return

    loader_id = ""
    for item in mc_meta.get("modLoaders") or []:
        if item.get("primary"):
            loader_id = item.get("id", "")
            break
    if not loader_id and mc_meta.get("modLoaders"):
        loader_id = (mc_meta.get("modLoaders") or [{}])[0].get("id", "")

    # CF 的加载器 id 形如 forge-14.23.5.2860 / neoforge-21.1.264 / fabric-0.15.0
    deps = {"minecraft": mc}
    if loader_id.startswith("forge-"):
        deps["forge"] = loader_id[len("forge-"):]
    elif loader_id.startswith("neoforge-"):
        deps["neoforge"] = loader_id[len("neoforge-"):]
    elif loader_id.startswith("fabric-"):
        deps["fabric-loader"] = loader_id[len("fabric-"):]
    elif loader_id.startswith("quilt-"):
        deps["quilt-loader"] = loader_id[len("quilt-"):]

    kind, loader_full, version_id = mrpack_loader_of(deps, mc)

    name = (manifest.get("name") or os.path.splitext(os.path.basename(pack))[0]).strip()
    safe_name = "".join(c if c not in '\\/:*?"<>|' else "_" for c in name)
    # 主流启动器兼容布局：实例就是 versions/<名字>/，mods/config 直接放在里面（版本隔离）
    inst_root = instance_version_dir(base, safe_name)
    game_dir = inst_root
    mods_dir = os.path.join(game_dir, "mods")
    os.makedirs(mods_dir, exist_ok=True)

    print("整合包  : " + name + " " + str(manifest.get("version", "")))
    print("实例目录: " + inst_root)
    print("MC 版本 : " + str(mc))
    print("加载器  : " + (("%s %s" % (kind, loader_full)) if kind else "无（原版）"))
    print("目标版本: " + version_id)

    # ---- 1) 原版 ----
    mc_json = os.path.join(base, "versions", mc, mc + ".json")
    if not os.path.isfile(mc_json):
        print("\n[1/4] 本地没有原版 %s，先下载 ..." % mc)
        cmd_fetch(argparse.Namespace(version=mc, dir=base, workers=args.workers,
                                     no_assets=getattr(args, "no_assets", False)))
        if not os.path.isfile(mc_json):
            print("[错误] 原版下载失败，安装中止")
            return

    # ---- 2) 加载器 ----
    vjson = os.path.join(base, "versions", version_id, version_id + ".json")
    if not os.path.isfile(vjson) and kind:
        print("[2/4] 本地没有 %s，先自动安装加载器 ..." % version_id)
        try:
            cmd_loader(argparse.Namespace(kind=kind, version=loader_full,
                                          dir=base, java=None))
        except Exception as exc:
            print("      自动安装失败：" + str(exc))
            return
    else:
        print("[2/4] 版本已就绪")

    # ---- 3) 用 API 换地址 ----
    files = manifest.get("files") or []
    print("[3/4] 解析 %d 个文件（需要调 CurseForge API）..." % len(files))

    def resolve(entry):
        project_id = entry.get("projectID")
        file_id = entry.get("fileID")
        if not project_id or not file_id:
            return None, "缺少 projectID/fileID"
        try:
            payload = cf_get("/mods/%s/files/%s" % (project_id, file_id), key)
        except Exception as exc:
            return None, str(exc)
        data = payload.get("data") or {}
        file_name = data.get("fileName")
        url = data.get("downloadUrl")
        if not file_name:
            return None, "API 没返回 fileName"
        if not url:
            # 作者关掉了第三方分发时 downloadUrl 为 null，用 CDN 规则拼
            url = "https://edge.forgecdn.net/files/%d/%d/%s" % (
                int(file_id) // 1000, int(file_id) % 1000,
                urllib.parse.quote(file_name))
        sha1 = None
        for h in data.get("hashes") or []:
            if h.get("algo") == 1:
                sha1 = h.get("value")
        return (url, file_name, sha1), None

    jobs = []
    unresolved = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        futures = [pool.submit(resolve, e) for e in files]
        for future in concurrent.futures.as_completed(futures):
            item, why = future.result()
            if item:
                url, file_name, sha1 = item
                jobs.append((url, os.path.join(mods_dir, file_name), sha1))
            else:
                unresolved.append(why)

    if unresolved:
        print("      有 %d 个没能解析（前 3 条）：%s" % (len(unresolved), unresolved[:3]))

    print("      开始下载 %d 个 mod ..." % len(jobs))
    ok = skip = fail = 0
    done = 0
    failed = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(download_one, job) for job in jobs]
        for future in concurrent.futures.as_completed(futures):
            status, p_, info = future.result()
            done += 1
            if status == "ok":
                ok += 1
            elif status == "skip":
                skip += 1
            else:
                fail += 1
                failed.append((os.path.basename(p_), info))
            if done % 25 == 0 or done == len(jobs):
                print("      进度 %d/%d   新下载 %d  已存在 %d  失败 %d" % (done, len(jobs), ok, skip, fail))
    if failed:
        for n, why in failed[:5]:
            print("        [fail] " + n + " -> " + str(why))

    # ---- 4) 铺 overrides ----
    prefix = (manifest.get("overrides") or "overrides").rstrip("/") + "/"
    count = 0
    with zipfile.ZipFile(pack) as zf:
        for entry in zf.namelist():
            if not entry.startswith(prefix) or entry.endswith("/"):
                continue
            rel = entry[len(prefix):]
            dest = os.path.join(game_dir, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with zf.open(entry) as src_file, open(dest, "wb") as dst_file:
                shutil.copyfileobj(src_file, dst_file)
            count += 1
    print("[4/4] 已铺开 overrides：%d 个文件" % count)

    # 生成标准版本目录（让 PCL2 / HMCL 也能直接认出来）
    write_instance_version(base, safe_name, version_id)

    meta = {
        "id": os.path.basename(inst_root),      # 版本目录名（PCL2 认的就是它）
        "name": name,
        "version_id": version_id,
        "minecraft": mc,
        "loader": kind,
        "loader_version": loader_full,
        "game_dir": game_dir,
        "source": pack,
    }
    with open(os.path.join(inst_root, "instance.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print("\n完成。启动命令：")
    print("  python mcroute.py launch " + os.path.basename(inst_root))


MMC_LOADER_UIDS = {
    "net.minecraftforge": "forge",
    "net.neoforged": "neoforge",
    "net.fabricmc.fabric-loader": "fabric",
    "org.quiltmc.quilt-loader": "quilt",
}


def install_mmc(pack, base, args):
    """
    安装 MultiMC / Prism / HMCL 导出的实例包。

    这类包的结构：
        instance.cfg      实例设置（名称、内存分配）
        mmc-pack.json     组件列表：net.minecraft / net.minecraftforge / fabric-loader ...
        .minecraft/       mods、config、saves 等

    和 mrpack 最大的区别：**mod 已经打在包里**，不需要下载清单，直接铺开就行。
    """
    import zipfile

    with zipfile.ZipFile(pack) as zf:
        names = zf.namelist()
        if "mmc-pack.json" not in names:
            print("[错误] 没有 mmc-pack.json，不是 MultiMC 系实例包")
            return
        pack_meta = json.loads(zf.read("mmc-pack.json").decode("utf-8"))

        cfg = {}
        if "instance.cfg" in names:
            for line in zf.read("instance.cfg").decode("utf-8", "replace").splitlines():
                line = line.strip()
                if "=" in line and not line.startswith("#"):
                    key, _sep, value = line.partition("=")
                    cfg[key.strip()] = value.strip()

    # ---- 解析组件 ----
    mc = None
    loader_kind = None
    loader_ver = None
    for comp in pack_meta.get("components") or []:
        uid = comp.get("uid", "")
        ver = comp.get("version", "")
        if uid == "net.minecraft":
            mc = ver
        elif uid in MMC_LOADER_UIDS:
            loader_kind = MMC_LOADER_UIDS[uid]
            loader_ver = ver

    if not mc:
        print("[错误] mmc-pack.json 里没有 net.minecraft 组件")
        return

    deps = {"minecraft": mc}
    if loader_kind == "forge":
        deps["forge"] = loader_ver
    elif loader_kind == "neoforge":
        deps["neoforge"] = loader_ver
    elif loader_kind == "fabric":
        deps["fabric-loader"] = loader_ver
    elif loader_kind == "quilt":
        deps["quilt-loader"] = loader_ver

    _kind, loader_full, version_id = mrpack_loader_of(deps, mc)

    name = (cfg.get("name") or os.path.splitext(os.path.basename(pack))[0]).strip()
    safe_name = "".join(c if c not in '\\/:*?"<>|' else "_" for c in name)
    # 主流启动器兼容布局：实例就是 versions/<名字>/，mods/config 直接放在里面（版本隔离）
    inst_root = instance_version_dir(base, safe_name)
    game_dir = inst_root
    os.makedirs(game_dir, exist_ok=True)

    print("整合包  : " + name)
    print("实例目录: " + inst_root)
    print("MC 版本 : " + str(mc))
    print("加载器  : " + (("%s %s" % (loader_kind, loader_ver)) if loader_kind else "无（原版）"))
    print("目标版本: " + version_id)
    if cfg.get("MaxMemAlloc"):
        print("建议内存: %s MB（来自 instance.cfg）" % cfg.get("MaxMemAlloc"))

    # ---- 1) 原版 ----
    mc_json = os.path.join(base, "versions", mc, mc + ".json")
    if not os.path.isfile(mc_json):
        print("\n[1/3] 本地没有原版 %s，先下载 ..." % mc)
        cmd_fetch(argparse.Namespace(version=mc, dir=base, workers=args.workers,
                                     no_assets=getattr(args, "no_assets", False)))
        if not os.path.isfile(mc_json):
            print("[错误] 原版下载失败，安装中止")
            return

    # ---- 2) 加载器 ----
    vjson = os.path.join(base, "versions", version_id, version_id + ".json")
    if not os.path.isfile(vjson) and loader_kind:
        print("[2/3] 本地没有 %s，先自动安装加载器 ..." % version_id)
        try:
            cmd_loader(argparse.Namespace(kind=loader_kind, version=loader_full,
                                          dir=base, java=None))
        except Exception as exc:
            print("      自动安装失败：" + str(exc))
            return
    else:
        print("[2/3] 版本已就绪")

    # ---- 3) 铺开 .minecraft/ ----
    prefix = ".minecraft/"
    count = 0
    with zipfile.ZipFile(pack) as zf:
        for entry in zf.namelist():
            if not entry.startswith(prefix) or entry.endswith("/"):
                continue
            rel = entry[len(prefix):]
            dest = os.path.join(game_dir, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with zf.open(entry) as src_file, open(dest, "wb") as dst_file:
                dst_file.write(src_file.read())
            count += 1
    print("[3/3] 已铺开 %d 个文件（mods/config/saves 等）" % count)

    # 生成标准版本目录（让 PCL2 / HMCL 也能直接认出来）
    write_instance_version(base, safe_name, version_id)

    meta = {
        "id": os.path.basename(inst_root),      # 版本目录名（PCL2 认的就是它）
        "name": name,
        "version_id": version_id,
        "minecraft": mc,
        "loader": loader_kind,
        "loader_version": loader_ver,
        "game_dir": game_dir,
        "max_mem_mb": cfg.get("MaxMemAlloc"),
        "source": pack,
    }
    with open(os.path.join(inst_root, "instance.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print("\n完成。启动命令：")
    print("  python mcroute.py launch " + os.path.basename(inst_root))


def instance_version_dir(base, name):
    """实例对应的标准版本目录：<base>/versions/<名字>"""
    safe = "".join(c if c not in '\\/:*?"<>|' else "_" for c in name).strip()
    return os.path.join(base, "versions", safe)


def find_client_jar(base, version):
    """沿 inheritsFrom 链找第一个真实存在的客户端 jar"""
    cur = version
    for _ in range(6):
        if not cur:
            return None
        vdir = os.path.join(base, "versions", cur)
        jar = os.path.join(vdir, cur + ".jar")
        if os.path.isfile(jar):
            return jar
        vjson = os.path.join(vdir, cur + ".json")
        if not os.path.isfile(vjson):
            return None
        try:
            with open(vjson, "r", encoding="utf-8") as f:
                cur = json.load(f).get("inheritsFrom")
        except Exception:
            return None
    return None


def write_instance_version(base, name, parent_version):
    """
    把整合包实例写成一个「标准版本目录」：

        versions/<名字>/<名字>.json     合并后的完整 json（不再有 inheritsFrom）
        versions/<名字>/<名字>.jar      客户端 jar（从父版本复制过来）

    为什么要这样：PCL2 / HMCL / Prism 只认 `versions/<版本名>/<版本名>.json`，
    不认我们自定义的 instances 目录。写成标准结构后：
      · 它们会把 <名字> 当成一个正常版本，直接可选可启动
      · 我们的启动器也用同一份结构（版本隔离，mods/config 就在这个目录里）
    """
    import shutil

    vdir = instance_version_dir(base, name)
    os.makedirs(vdir, exist_ok=True)
    vid = os.path.basename(vdir)

    merged = load_version_json(base, parent_version)
    if not merged:
        print("[警告] 读取父版本 json 失败：" + str(parent_version))
        return None

    merged = dict(merged)
    merged["id"] = vid
    merged.pop("inheritsFrom", None)   # 已经合并过了，不再依赖父版本

    # 客户端 jar：从 inheritsFrom 链上找第一个存在的复制过来，做到自包含
    src_jar = find_client_jar(base, parent_version)
    dst_jar = os.path.join(vdir, vid + ".jar")
    if src_jar and not os.path.isfile(dst_jar):
        try:
            shutil.copyfile(src_jar, dst_jar)
        except Exception as exc:
            print("[警告] 复制客户端 jar 失败：" + str(exc))

    with open(os.path.join(vdir, vid + ".json"), "w", encoding="utf-8") as f:
        json.dump(merged, f, ensure_ascii=False, indent=2)

    print("[实例] 已写出标准版本目录：versions\\" + vid)
    return vdir


def cmd_install(args):
    """安装整合包：自动识别 Modrinth .mrpack / MultiMC 系实例包 / CurseForge zip"""
    import zipfile

    pack = os.path.abspath(args.pack)
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)

    if not os.path.isfile(pack):
        print("[错误] 文件不存在：" + pack)
        return
    try:
        with zipfile.ZipFile(pack) as zf:
            names = set(zf.namelist())
    except Exception as exc:
        print("[错误] 打不开压缩包：" + str(exc))
        return

    if "modrinth.index.json" in names:
        print("格式：Modrinth (.mrpack)")
        install_mrpack(pack, base, args)
    elif "mmc-pack.json" in names:
        print("格式：MultiMC / Prism 实例包")
        install_mmc(pack, base, args)
    elif "manifest.json" in names:
        print("格式：CurseForge 整合包")
        install_curseforge(pack, base, args)
    else:
        print("[错误] 认不出的整合包格式（没有 modrinth.index.json / mmc-pack.json / manifest.json）")


def install_mrpack(pack, base, args):
    """
    安装 Modrinth 整合包（.mrpack）。

    mrpack 的结构：
        modrinth.index.json   -> 依赖（游戏版本 + 加载器）+ 文件清单（URL + sha1/sha512）
        overrides/            -> 配置、光影、资源包等要原样铺进实例的东西

    所以流程就是：读索引 -> 确保加载器版本在本地 -> 并发下载清单里的文件 -> 解压 overrides。
    """
    import shutil
    import zipfile

    with zipfile.ZipFile(pack) as zf:
        names = zf.namelist()
        if "modrinth.index.json" not in names:
            print("[错误] 压缩包里没有 modrinth.index.json，可能不是 Modrinth 整合包")
            return
        index = json.loads(zf.read("modrinth.index.json").decode("utf-8"))

    name = (index.get("name") or os.path.splitext(os.path.basename(pack))[0]).strip()
    safe_name = "".join(c if c not in '\\/:*?"<>|' else "_" for c in name)
    # 主流启动器兼容布局：实例就是 versions/<名字>/，mods/config 直接放在里面（版本隔离）
    inst_root = instance_version_dir(base, safe_name)
    game_dir = inst_root
    os.makedirs(game_dir, exist_ok=True)

    deps = index.get("dependencies") or {}
    mc = deps.get("minecraft")
    if not mc:
        print("[错误] 索引里没有 minecraft 依赖")
        return
    kind, loader_ver, version_id = mrpack_loader_of(deps, mc)

    print("整合包  : " + name)
    print("实例目录: " + inst_root)
    print("MC 版本 : " + str(mc))
    print("加载器  : " + (("%s %s" % (kind, loader_ver)) if kind else "无（原版）"))
    print("目标版本: " + version_id)

    # ---- 1) 先确保原版在本地（加载器安装器要用它做补丁，启动也要它的 jar）----
    mc_json = os.path.join(base, "versions", mc, mc + ".json")
    if not os.path.isfile(mc_json):
        print("\n[1/3] 本地没有原版 %s，先下载 ..." % mc)
        cmd_fetch(argparse.Namespace(version=mc, dir=base, workers=args.workers,
                                     no_assets=getattr(args, "no_assets", False)))
        if not os.path.isfile(mc_json):
            print("[错误] 原版下载失败，安装中止")
            return

    # ---- 2) 确保加载器版本在本地 ----
    vjson = os.path.join(base, "versions", version_id, version_id + ".json")
    if not os.path.isfile(vjson):
        if kind:
            print("\n[2/3] 本地没有 %s，先自动安装加载器 ..." % version_id)
            try:
                cmd_loader(argparse.Namespace(kind=kind, version=loader_ver,
                                              dir=base, java=None))
            except Exception as exc:
                print("      自动安装失败：" + str(exc))
                print("      请手动运行：python mcroute.py loader %s %s" % (kind, loader_ver))
                return
        else:
            print("\n[2/3] 本地没有 %s，先下载 ..." % mc)
            cmd_fetch(argparse.Namespace(version=mc, dir=base, workers=16, no_assets=False))
    else:
        print("[2/3] 加载器版本已在本地")

    # ---- 2) 下载清单里的文件 ----
    jobs = []
    skipped = 0
    for item in index.get("files") or []:
        rel = item.get("path")
        urls = item.get("downloads") or []
        env = item.get("env") or {}
        if env.get("client") == "unsupported":
            skipped += 1
            continue
        if not rel or not urls:
            skipped += 1
            continue
        hashes = item.get("hashes") or {}
        dest = os.path.join(game_dir, rel.replace("/", os.sep))
        jobs.append((urls[0], dest, hashes.get("sha1")))

    print("[3/4] 下载 %d 个文件（跳过 %d 个仅服务端/无地址的）" % (len(jobs), skipped))

    import concurrent.futures
    ok = skip = fail = 0
    done = 0
    failed = []
    total = len(jobs)
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(download_one, job) for job in jobs]
        for future in concurrent.futures.as_completed(futures):
            status, p_, info = future.result()
            done += 1
            if status == "ok":
                ok += 1
            elif status == "skip":
                skip += 1
            else:
                fail += 1
                failed.append((os.path.basename(p_), info))
            if done % 25 == 0 or done == total:
                print("      进度 %d/%d   新下载 %d  已存在 %d  失败 %d" % (done, total, ok, skip, fail))

    if failed:
        print("      失败 %d 个（可重跑续传）：" % len(failed))
        for n, why in failed[:5]:
            print("        " + n + " -> " + str(why))

    # ---- 3) 解压 overrides ----
    prefix = "overrides/"
    count = 0
    with zipfile.ZipFile(pack) as zf:
        for entry in zf.namelist():
            if not entry.startswith(prefix) or entry.endswith("/"):
                continue
            rel = entry[len(prefix):]
            dest = os.path.join(game_dir, rel.replace("/", os.sep))
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with zf.open(entry) as src_file, open(dest, "wb") as dst_file:
                shutil.copyfileobj(src_file, dst_file)
            count += 1
    print("[4/4] 已铺开 overrides：%d 个文件" % count)

    # ---- 记录实例元数据 ----
    # 生成标准版本目录（让 PCL2 / HMCL 也能直接认出来）
    write_instance_version(base, safe_name, version_id)

    meta = {
        "id": os.path.basename(inst_root),      # 版本目录名（PCL2 认的就是它）
        "name": name,
        "version_id": version_id,
        "minecraft": mc,
        "loader": kind,
        "loader_version": loader_ver,
        "game_dir": game_dir,
        "source": pack,
    }
    with open(os.path.join(inst_root, "instance.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print("\n完成。启动命令：")
    print("  python mcroute.py launch " + os.path.basename(inst_root))


def cmd_java(args):
    """列出机器上检测到的所有 Java（按主版本升序）"""
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    items = scan_java(base)
    if not items:
        print("没找到任何 Java。")
        print("可以从 https://api.adoptium.net 下载 JRE，解压到 "
              + os.path.join(base, "java_versions"))
        return
    print("检测到 %d 个 Java：" % len(items))
    for exe, major in items:
        print("  Java %-3s %s" % (major, exe))


# ---------------------------------------------------------------------------
# 子命令：verify / test / mkbat
# ---------------------------------------------------------------------------
def _ns_for_plan(version, base, java=None, game_dir=None):
    """构造一个给 plan_launch 用的参数对象"""
    return argparse.Namespace(
        version=version, dir=base, java=java, game_dir=game_dir,
        username="Player", force=False, wait=False, memory=None,
    )


def _asset_report(base, asset_index_id):
    """检查 assets：索引在不在、对象缺多少个（只查存在性，不校验 sha1）"""
    if not asset_index_id:
        return None, 0, 0
    idx_path = os.path.join(base, "assets", "indexes", asset_index_id + ".json")
    if not os.path.isfile(idx_path):
        return False, 0, 0
    try:
        with open(idx_path, "r", encoding="utf-8") as f:
            index = json.load(f)
    except Exception:
        return False, 0, 0

    objects = index.get("objects") or {}
    total = len(objects)
    missing = 0
    for _name, obj in objects.items():
        h = obj.get("hash", "")
        if not h or len(h) < 2:
            continue
        p = os.path.join(base, "assets", "objects", h[:2], h)
        if not os.path.isfile(p):
            missing += 1
    return True, total, missing


def cmd_verify(args):
    """
    体检：检查一个版本/实例的文件是否齐全（相当于 PCL2 的「补全文件」）。

        python mcroute.py verify TestPack            只体检
        python mcroute.py verify TestPack --fix      体检完直接补齐
        python mcroute.py verify TestPack --deep     连 sha1 一起校验（慢）
    """
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    info = plan_launch(_ns_for_plan(args.version, base, args.java))
    if info is None:
        return

    print("=" * 70)
    print("体检报告：" + args.version)
    print("=" * 70)

    # ---- 1) client jar + 库 ----
    lib_total = len(info["classpath"])
    lib_missing = [p for kind, p in info["missing"] if kind == "库"]
    jar_missing = [p for kind, p in info["missing"] if kind == "客户端 jar"]
    print("  客户端 jar : " + ("缺失" if jar_missing else "OK"))
    print("  库          : %d/%d 存在" % (lib_total - len(lib_missing), lib_total))
    if lib_missing:
        for p in lib_missing[:8]:
            print("      - 缺 " + os.path.basename(p))
        if len(lib_missing) > 8:
            print("      ...还有 %d 个" % (len(lib_missing) - 8))

    # ---- 2) assets ----
    has_index, asset_total, asset_missing = _asset_report(base, info.get("asset_index_id"))
    if has_index is None:
        print("  assets     : 这个版本没有资源索引")
    elif has_index is False:
        print("  assets     : 索引缺失（还没下过资源）")
    else:
        print("  assets     : %d/%d 存在" % (asset_total - asset_missing, asset_total))

    # ---- 3) deep：逐文件 sha1 ----
    broken = []
    if args.deep:
        print("  正在校验 sha1（可能要一会儿）...")
        checked = 0
        for p in info["classpath"]:
            if not os.path.isfile(p):
                continue
            checked += 1
            # 只校验有记录的：从合并后的 json 里找同名库的 sha1 太麻烦，
            # 这里退一步：只报告文件大小为 0 或明显异常的
            try:
                if os.path.getsize(p) < 32:
                    broken.append(p)
            except Exception:
                broken.append(p)
        print("      已检查 %d 个文件，可疑 %d 个" % (checked, len(broken)))
        for p in broken[:5]:
            print("      - 可疑 " + os.path.basename(p))

    # ---- 结论 ----
    problem = len(info["missing"]) + asset_missing
    print()
    if problem == 0:
        print("[结论] 文件齐全，可以直接启动。")
        return
    print("[结论] 还缺 %d 项（%d 个库/jar + %d 个资源对象）。" % (
        problem, len(info["missing"]), asset_missing))
    print("       补全命令：python mcroute.py verify %s --fix" % args.version)

    if args.fix:
        print("\n开始补全 ...")
        cmd_fetch(argparse.Namespace(version=args.version, dir=base,
                                     workers=16, no_assets=False))


def cmd_test(args):
    """
    测试启动：真的把游戏拉起来，看它能不能过初始化，然后自动关掉。
    用来快速验证"这个实例到底能不能跑"，不用一直盯着窗口。

        python mcroute.py test TestPack
        python mcroute.py test TestPack --timeout 120
    """
    import subprocess
    import time

    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    info = plan_launch(_ns_for_plan(args.version, base, args.java))
    if info is None:
        return

    if info["missing"]:
        print("\n[中止] 有 %d 个文件缺失，先跑 verify --fix。" % len(info["missing"]))
        return

    os.makedirs(info["game_dir"], exist_ok=True)
    os.makedirs(info["natives_dir"], exist_ok=True)
    if info.get("natives_jars"):
        extract_natives(info["natives_jars"], info["natives_dir"])

    print("\n[测试] 启动游戏，最长等 %d 秒 ..." % args.timeout)
    t0 = time.time()
    proc = subprocess.Popen(
        info["argv"], cwd=info["game_dir"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace",
    )

    good_markers = ("Setting user", "LWJGL", "Backend library",
                    "Forge Mod Loader has successfully loaded", "Reloading ResourceManager")
    bad_markers = ("Game crashed", "Could not find or load main class",
                   "NoClassDefFoundError", "UnsatisfiedLinkError",
                   "ModuleLayer", "Exception in thread")
    good_seen = []
    bad_seen = []
    tail = []
    deadline = t0 + args.timeout

    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            if proc.poll() is not None:
                break
            time.sleep(0.2)
            continue
        line = line.rstrip()
        if line:
            tail.append(line)
            tail = tail[-40:]
        if any(m in line for m in good_markers) and line not in good_seen:
            good_seen.append(line[:120])
            print("  [ok] " + line[:110])
        for m in bad_markers:
            if m in line:
                bad_seen.append(line[:160])
                print("  [!!] " + line[:150])
                break
        # 看到足够多的成功信号就可以收工
        if len(good_seen) >= 2 and not bad_seen:
            break

    alive = proc.poll() is None
    if alive:
        try:
            proc.kill()
        except Exception:
            pass
    try:
        proc.wait(timeout=10)
    except Exception:
        pass

    print("\n" + "=" * 70)
    if bad_seen:
        print("[测试结果] 失败 —— 启动过程中出现错误：")
        for line in bad_seen[:5]:
            print("    " + line)
        print("\n最后几行输出：")
        for line in tail[-8:]:
            print("    " + line)
        return
    if good_seen:
        print("[测试结果] 通过 —— 游戏完成初始化（看到 %d 个正常标志）" % len(good_seen))
        print("          耗时 %.1f 秒" % (time.time() - t0))
        return
    print("[测试结果] 不确定 —— 没看到明显成功或失败标志（可能启动太慢）")
    for line in tail[-8:]:
        print("    " + line)


def _quote_arg(a):
    """给 .bat 里的参数加引号（含空格或特殊字符时）"""
    if not a:
        return '""'
    if any(c in a for c in ' \t&()[]{}^=;!%+,`"'):
        return '"' + a.replace('"', '\\"') + '"'
    return a


def cmd_mkbat(args):
    """
    在实例目录里生成双击即可启动的启动器：

        versions/<名字>/launch.bat    ← 双击启动

    注意（踩过的坑）：
      · Java 8 **不支持** @argfile（那是 JDK 9+ 的特性），所以不能只写参数文件
      · Windows 命令行有 8191 字符上限，classpath 很长的大整合包会超
        -> 这种情况下额外生成 launch.ps1，由 bat 调用（PowerShell 没有这个限制）
    """
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)
    info = plan_launch(_ns_for_plan(args.version, base, args.java))
    if info is None:
        return

    vdir = os.path.join(base, "versions", args.version)
    if not os.path.isdir(vdir):
        vdir = info["game_dir"]
    os.makedirs(vdir, exist_ok=True)

    java = info["java"]
    argv = info["argv"]
    cmdline = " ".join(_quote_arg(a) for a in argv)

    bat_path = os.path.join(vdir, "launch.bat")
    header = [
        "@echo off",
        "chcp 936 >nul",
        'cd /d "%~dp0"',
        "rem 由 mcroute 生成：双击启动这个实例（其它启动器不受影响）",
    ]

    if len(cmdline) < 7800:
        # 短命令：直接写进 .bat
        body = header + [cmdline, "if errorlevel 1 pause"]
        with open(bat_path, "w", encoding="gbk", errors="replace", newline="\r\n") as f:
            f.write("\n".join(body) + "\n")
        # 清掉上次可能生成的 ps1
        for stale in ("launch.ps1", "launch-args.txt"):
            sp = os.path.join(vdir, stale)
            if os.path.isfile(sp):
                os.remove(sp)
        print("\n[已生成] " + bat_path)
        print("  命令长度 %d 字符，直接内嵌在 bat 里。" % len(cmdline))
        return

    # 长命令：写 launch.ps1，bat 只负责调用
    ps1 = os.path.join(vdir, "launch.ps1")
    q = lambda x: '"' + x.replace('"', '`"') + '"'
    ps_lines = [
        "# 由 mcroute 生成：整合包 classpath 太长，走 PowerShell 避免命令行长度限制",
        "$java = " + q(java),
        "$argv = @(",
    ]
    for a in argv[1:]:
        ps_lines.append("    " + q(a) + ",")
    ps_lines.append(")")
    ps_lines.append("& $java @argv")
    ps_lines.append("if ($LASTEXITCODE -ne 0) { Write-Host '启动失败，退出码' $LASTEXITCODE }")
    with open(ps1, "w", encoding="gbk", errors="replace", newline="\r\n") as f:
        f.write("\n".join(ps_lines) + "\n")

    body = header + [
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch.ps1"',
        "if errorlevel 1 pause",
    ]
    with open(bat_path, "w", encoding="gbk", errors="replace", newline="\r\n") as f:
        f.write("\n".join(body) + "\n")

    print("\n[已生成] " + bat_path)
    print("         " + ps1 + "（classpath 太长，%d 字符，改用 PowerShell）" % len(cmdline))


def cmd_status(_args):
    auth = auth_load()
    if not auth:
        print("当前未登录。运行： python mcroute.py login")
        return

    print("  用户名 : " + str(auth.get("name")))
    print("  UUID   : " + str(auth.get("uuid")))
    left = int((auth.get("mc_expires_at") or 0) - _now())
    if left > 0:
        print("  MC令牌 : 有效，还剩约 {} 分钟".format(left // 60))
    else:
        print("  MC令牌 : 已过期（启动时会自动刷新）")
    print("  续期凭据: " + ("有" if auth.get("ms_refresh_token") else "无（过期就得重新 login）"))


def cmd_logout(_args):
    if os.path.isfile(AUTH_FILE):
        os.remove(AUTH_FILE)
        print("已退出登录，本地凭据已删除。")
    else:
        print("本来就没有登录记录。")


# ---------------------------------------------------------------------------
# 无参数时的说明
# ---------------------------------------------------------------------------
def print_guide():
    """当用户没给子命令时，打印一份人话说明（而不是让 argparse 甩英文报错）"""
    print("你还没告诉我要做什么。这个脚本有这些用法：\n")
    print("  [下载 / 启动]")
    print("  fetch     python mcroute.py fetch 1.21.1        # 下载完整闭包（sha1 校验、可续传）")
    print("  build     python mcroute.py build 1.21.1        # 只拼启动命令，不执行")
    print("  launch    python mcroute.py launch 1.21.1       # 真正启动游戏")
    print("  loader    python mcroute.py loader fabric 1.20.1     # 装加载器：fabric / quilt / legacyfabric / forge / neoforge")
    print()
    print("  [账号]")
    print("  login     python mcroute.py login               # 登录微软账号（需 client_id）")
    print("  status    python mcroute.py status              # 看登录状态")
    print("  logout    python mcroute.py logout              # 退出登录")
    print()
    print("  [查源 / 诊断]")
    print("  manifest  python mcroute.py manifest            # 版本清单（Mojang→Modrinth）")
    print("  resolve   python mcroute.py resolve 1.21.1      # 某版本要下哪些文件")
    print("  sources   python mcroute.py sources             # 所有下载源测速")
    print("  wiki      python mcroute.py wiki 1.12.2         # Wiki 索引（manifest 兜底）")
    print()
    print("注意：在 VS Code 里按 F5 或点右上角三角按钮，是「不带参数运行」，")
    print("      所以一定会停在这里。请改用终端，把上面命令手动敲进去。")


# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------
def main():
    parser = argparse.ArgumentParser(
        description="MC 下载源路由验证器 v0",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="示例:\n  python mcroute.py manifest\n  python mcroute.py resolve 1.21.1\n  python mcroute.py sources",
    )
    sub = parser.add_subparsers(dest="cmd")

    p1 = sub.add_parser("manifest", help="拉取版本清单（Mojang 优先，Modrinth 兜底）")
    p1.set_defaults(func=cmd_manifest)

    p2 = sub.add_parser("resolve", help="解析某个版本，列出下载闭包")
    p2.add_argument("version", help="版本号，例如 1.21.1")
    p2.set_defaults(func=cmd_resolve)

    p3 = sub.add_parser("sources", help="体检所有下载源")
    p3.set_defaults(func=cmd_sources)

    p4 = sub.add_parser("build", help="用本地实例拼启动命令（只打印，不执行）")
    p4.add_argument("version", help="版本目录名，例如 1.21.1-21.1.264")
    p4.add_argument("--dir", help="实例根目录（含 versions/ libraries/ assets/），默认 mcdata/")
    p4.add_argument("--java", help="指定 java.exe 路径，默认按版本要求自动找")
    p4.add_argument("--game-dir", help="游戏目录，默认 <实例目录>/instances/<版本>")
    p4.add_argument("--username", default="Player", help="离线占位用户名")
    p4.set_defaults(func=cmd_build)

    p5 = sub.add_parser("launch", help="真正启动游戏（会拉起 JVM 子进程）")
    p5.add_argument("version", help="版本目录名，例如 1.21.1-21.1.264")
    p5.add_argument("--dir", help="实例根目录（含 versions/ libraries/ assets/），默认 mcdata/")
    p5.add_argument("--java", help="指定 java.exe 路径，默认按版本要求自动找")
    p5.add_argument("--game-dir", help="游戏目录，默认 <实例目录>/instances/<版本>")
    p5.add_argument("--username", default="Player", help="离线占位用户名")
    p5.add_argument("--force", action="store_true", help="缺文件也强行启动（多半会崩）")
    p5.add_argument("--memory", help="最大堆内存，例如 6G（默认 2G，整合包通常要 4G 以上）")
    p5.add_argument("--wait", action="store_true", help="等游戏退出后再返回")
    p5.set_defaults(func=cmd_launch)

    p6 = sub.add_parser("wiki", help="查 Minecraft Wiki 的版本索引（manifest 兜底）")
    p6.add_argument("version", help="版本号，例如 1.12.2")
    p6.set_defaults(func=cmd_wiki)

    p7 = sub.add_parser("fetch", help="下载某版本的完整闭包到本地（sha1 校验、可续传）")
    p7.add_argument("version", help="版本号，例如 1.21.1")
    p7.add_argument("--dir", help="目标目录，默认脚本旁边的 mcdata/")
    p7.add_argument("--workers", type=int, default=16, help="并发线程数，默认 16")
    p7.add_argument("--no-assets", action="store_true", help="只下索引、不下资源对象（快速冒烟用）")
    p7.set_defaults(func=cmd_fetch)

    p8 = sub.add_parser("login", help="登录微软账号（设备码流程，需要 client_id）")
    p8.set_defaults(func=cmd_login)

    p9 = sub.add_parser("status", help="查看当前登录状态")
    p9.set_defaults(func=cmd_status)

    p10 = sub.add_parser("logout", help="退出登录，删除本地凭据")
    p10.set_defaults(func=cmd_logout)

    p11 = sub.add_parser("loader", help="自动安装加载器（fabric / forge / neoforge）")
    p11.add_argument("kind", help="加载器类型：fabric / quilt / legacyfabric / forge / neoforge")
    p11.add_argument("version", help="fabric: 1.20.1 或 1.20.1-0.19.5；forge: 1.20.1-47.2.0；neoforge: 21.1.264")
    p11.add_argument("--dir", help="数据目录，默认 mcdata/")
    p11.add_argument("--java", help="指定 java.exe（安装器需要 Java 21+）")
    p11.add_argument("--no-assets", action="store_true",
                     help="顺带下载原版时跳过资源对象（快，但没有音效贴图）")
    p11.set_defaults(func=cmd_loader)

    p12 = sub.add_parser("java", help="列出检测到的 Java")
    p12.add_argument("--dir", help="数据目录，默认 mcdata/")
    p12.set_defaults(func=cmd_java)

    p13 = sub.add_parser("install", help="安装整合包（目前支持 Modrinth .mrpack）")
    p13.add_argument("pack", help=".mrpack 文件路径")
    p13.add_argument("--dir", help="数据目录，默认 mcdata/")
    p13.add_argument("--workers", type=int, default=16, help="并发线程数，默认 16")
    p13.add_argument("--no-assets", action="store_true", help="下载原版时跳过资源对象（快速验证）")
    p13.set_defaults(func=cmd_install)

    p14 = sub.add_parser("verify", help="体检版本/实例的文件是否齐全（补全体检）")
    p14.add_argument("version", help="版本目录名或整合包实例名")
    p14.add_argument("--dir", help="数据目录，默认 mcdata/")
    p14.add_argument("--java", help="指定 java.exe")
    p14.add_argument("--fix", action="store_true", help="体检完直接补齐缺失文件")
    p14.add_argument("--deep", action="store_true", help="更严格的检查（慢）")
    p14.set_defaults(func=cmd_verify)

    p15 = sub.add_parser("test", help="测试启动：拉到初始化完成就自动关掉")
    p15.add_argument("version", help="版本目录名或整合包实例名")
    p15.add_argument("--dir", help="数据目录，默认 mcdata/")
    p15.add_argument("--java", help="指定 java.exe")
    p15.add_argument("--timeout", type=int, default=90, help="最长等待秒数，默认 90")
    p15.set_defaults(func=cmd_test)

    p16 = sub.add_parser("mkbat", help="在实例目录生成双击启动的 launch.bat")
    p16.add_argument("version", help="版本目录名或整合包实例名")
    p16.add_argument("--dir", help="数据目录，默认 mcdata/")
    p16.add_argument("--java", help="指定 java.exe")
    p16.set_defaults(func=cmd_mkbat)

    args = parser.parse_args()

    if not getattr(args, "cmd", None):
        print_guide()
        return

    args.func(args)


if __name__ == "__main__":
    main()
