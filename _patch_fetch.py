# -*- coding: utf-8 -*-
"""补丁：让 fetch 支持「本地已有版本 json」（含 inheritsFrom 的加载器版本）"""
import io

path = "mcroute.py"
src = io.open(path, encoding="utf-8").read()

start_marker = "    # ---- 1. 找版本 json ----"
end_marker = '    print(f"      版本 json 已保存")'

try:
    i = src.index(start_marker)
    j = src.index(end_marker) + len(end_marker)
except ValueError:
    raise SystemExit("找不到 fetch 的标记段")

NEW = '''    # ---- 1. 找版本 json（本地已有就直接用，能处理加载器生成的 inheritsFrom 版本）----
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
        print("      版本 json 已保存")'''

src = src[:i] + NEW + src[j:]
io.open(path, "w", encoding="utf-8").write(src)
print("patched: fetch 支持本地版本 json")
