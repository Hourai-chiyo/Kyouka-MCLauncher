# -*- coding: utf-8 -*-
"""补丁：下载前按路由表改写 URL（maven.neoforged.net -> neoforged.forgecdn.net）"""
import io

path = "mcroute.py"
src = io.open(path, encoding="utf-8").read()

if "def rewrite_url" in src:
    print("已经打过补丁")
    raise SystemExit(0)

anchor = "def download_one(job):"
helper = '''# ---------------------------------------------------------------------------
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


'''
if anchor not in src:
    raise SystemExit("找不到 download_one")
src = src.replace(anchor, helper + anchor, 1)

old_call = "        data, _dt = fetch_bytes(url, timeout=90)"
new_call = "        data, _dt = fetch_bytes(rewrite_url(url), timeout=90)"
if old_call not in src:
    raise SystemExit("找不到 fetch_bytes 调用")
src = src.replace(old_call, new_call, 1)

io.open(path, "w", encoding="utf-8").write(src)
print("patched: URL 改写已加入")
