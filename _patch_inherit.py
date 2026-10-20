# -*- coding: utf-8 -*-
"""补丁：支持 inheritsFrom 合并 + 父版本 client jar 回退"""
import io

path = "mcroute.py"
src = io.open(path, encoding="utf-8").read()

# ---------- 1) 在 plan_launch 前插入合并函数 ----------
anchor = "def plan_launch(args):"
funcs = r'''def merge_version(parent, child):
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


'''
if anchor not in src:
    raise SystemExit("找不到 plan_launch 锚点")
src = src.replace(anchor, funcs + anchor, 1)

# ---------- 2) 用 load_version_json 读版本 ----------
old_load = '''    with open(vjson, "r", encoding="utf-8") as f:
        vj = json.load(f)
'''
new_load = '''    vj = load_version_json(base, ver)
    if vj is None:
        print("[错误] 版本 json 读取失败：" + vjson)
        return
'''
if old_load not in src:
    raise SystemExit("找不到 json 读取段")
src = src.replace(old_load, new_load, 1)

# ---------- 3) 客户端 jar 支持回退到父版本 ----------
old_jar = '''    # 1c) 客户端 jar：优先用本地实例里那个（可能已被加载器改过）
    client_jar = os.path.join(vdir, ver + ".jar")
    if os.path.isfile(client_jar):
        classpath.append(client_jar)
    else:
        missing.append(("客户端 jar", client_jar))
'''
new_jar = '''    # 1c) 客户端 jar：先找本版本目录，没有就用 inheritsFrom 的父版本目录
    client_jar = os.path.join(vdir, ver + ".jar")
    if not os.path.isfile(client_jar):
        parent_id = vj.get("inheritsFrom")
        if parent_id:
            client_jar = os.path.join(base, "versions", parent_id, parent_id + ".jar")
    if os.path.isfile(client_jar):
        classpath.append(client_jar)
    else:
        missing.append(("客户端 jar", client_jar))
'''
if old_jar not in src:
    raise SystemExit("找不到客户端 jar 段")
src = src.replace(old_jar, new_jar, 1)

io.open(path, "w", encoding="utf-8").write(src)
print("patched: inheritsFrom 合并已加入")
