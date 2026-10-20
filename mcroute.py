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
            libs = {}
            for lib in parent.get("libraries") or []:
                libs[lib.get("name", "")] = lib
            for lib in value:
                libs[lib.get("name", "")] = lib
            merged["libraries"] = list(libs.values())

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
    game_dir = os.path.abspath(args.game_dir) if args.game_dir else os.path.join(base, "instances", ver)

    # ---- 1. 过滤库 + 拼 classpath ----
    classpath = []
    missing = []
    natives_jars = []
    skipped = 0

    # 1) 先按 name 去重：同名库「后出现的覆盖先出现的」（官方启动器同语义）
    #    Fabric/Forge 的 json 常同时带旧的 lwjgl 3.2.1 和新的 3.2.2，
    #    不去重就会把旧的也算进 classpath，然后误报“文件缺失”。
    libs_by_name = {}
    for lib in vj.get("libraries", []):
        libs_by_name[lib.get("name", "")] = lib

    # 2) 再过滤 + 拼 classpath
    for lib in libs_by_name.values():
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
        artifact = downloads.get("artifact")
        if artifact:
            rel = maven_path(artifact, lib)
            if rel:
                full = os.path.join(lib_root, rel.replace("/", os.sep))
                classpath.append(full)
                if not os.path.isfile(full):
                    missing.append(("库", full))

    # 1c) 客户端 jar：先找本版本目录，没有就用 inheritsFrom 的父版本目录
    client_jar = os.path.join(vdir, ver + ".jar")
    if not os.path.isfile(client_jar):
        parent_id = vj.get("inheritsFrom")
        if parent_id:
            client_jar = os.path.join(base, "versions", parent_id, parent_id + ".jar")
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
    if not any(a.startswith("-Xmx") for a in jvm_args):
        jvm_args.insert(0, "-Xmx2G")
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
DEFAULT_DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mcdata")


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
]


def rewrite_url(url):
    """按路由表把 URL 换到可用的镜像/CDN（路径保持不变）"""
    for old, new in URL_REWRITES:
        if url.startswith(old):
            return new + url[len(old):]
    return url


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

    try:
        data, _dt = fetch_bytes(rewrite_url(url), timeout=90)
    except Exception as exc:
        return ("fail", dest, str(exc))

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
        libs[lib.get("name", "")] = lib

    for lib in libs.values():
        if not rules_allow(lib):
            continue
        downloads = lib.get("downloads") or {}

        artifact = downloads.get("artifact")
        if artifact and artifact.get("url"):
            rel = maven_path(artifact, lib)
            if rel:
                jobs.append((artifact["url"],
                             os.path.join(lib_root, rel.replace("/", os.sep)),
                             artifact.get("sha1")))

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


def http_request(url, payload=None, headers=None, method=None, timeout=30):
    """
    统一发请求，返回 (JSON数据, 耗时, 是否成功)。
    失败也返回 JSON —— 微软把错误原因写在响应体里（error / error_description）。
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

    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
        return json.loads(raw.decode("utf-8")), time.time() - t0, True
    except urllib.error.HTTPError as exc:
        raw = exc.read()
        try:
            return json.loads(raw.decode("utf-8")), time.time() - t0, False
        except Exception:
            return {"error": "http_" + str(exc.code),
                    "error_description": raw.decode("utf-8", "replace")}, time.time() - t0, False


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

    kind = (args.kind or "").lower()
    ver = args.version
    base = os.path.abspath(args.dir or DEFAULT_DATA_DIR)

    if kind not in ("neoforge", "forge"):
        print("[错误] 目前支持 neoforge 和 forge")
        return

    url = installer_url(kind, ver)
    os.makedirs(base, exist_ok=True)
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
        data, dt = fetch_bytes(url, timeout=180)
    except Exception as exc:
        print("      失败：" + str(exc))
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
    except Exception as exc:
        print("      读取 install_profile.json 失败：" + str(exc))
        return

    all_libs = list(profile.get("libraries") or []) + template_libs
    print("      处理器依赖 %d 个 + 运行时库 %d 个" % (len(profile.get("libraries") or []), len(template_libs)))

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
    print("  loader    python mcroute.py loader neoforge 21.1.264  # 一条命令装好 NeoForge")
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

    p11 = sub.add_parser("loader", help="自动安装加载器（目前支持 neoforge）")
    p11.add_argument("kind", help="加载器类型：neoforge")
    p11.add_argument("version", help="加载器版本，例如 21.1.264")
    p11.add_argument("--dir", help="数据目录，默认 mcdata/")
    p11.add_argument("--java", help="指定 java.exe（安装器需要 Java 21+）")
    p11.set_defaults(func=cmd_loader)

    p12 = sub.add_parser("java", help="列出检测到的 Java")
    p12.add_argument("--dir", help="数据目录，默认 mcdata/")
    p12.set_defaults(func=cmd_java)

    args = parser.parse_args()

    if not getattr(args, "cmd", None):
        print_guide()
        return

    args.func(args)


if __name__ == "__main__":
    main()
