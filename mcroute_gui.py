# -*- coding: utf-8 -*-
"""
mcroute_gui.py —— mcroute 的图形界面（tkinter，零依赖，不用装任何东西）

运行方式：
    python mcroute_gui.py
    或双击 run_gui.bat

设计原则：
    1. 所有耗时操作（下载、安装、启动）都丢到后台线程，界面不会卡死
    2. 命令行那套逻辑一行都不重写，直接复用 mcroute 模块
       （所以 CLI 和 GUI 永远是同一份行为，不会两边跑偏）
    3. 输出统一进底部日志框
"""

import argparse
import json
import os
import queue
import re
import sys
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import mcroute

DATA_DIR = mcroute.DEFAULT_DATA_DIR

# 支持的加载器（顺序按实现顺序）
LOADER_KINDS = ["fabric", "quilt", "legacyfabric", "forge", "neoforge"]

LOADER_HINTS = {
    "fabric": "版本写 MC 版本即可自动挑最新：1.20.1\n也可以指定：1.20.1-0.19.5",
    "quilt": "和 Fabric 一样：1.20.1 或 1.20.1-0.27.0",
    "legacyfabric": "旧版本分支（1.8~1.13）：1.12.2",
    "forge": "格式 <MC版本>-<Forge版本>：1.20.1-47.2.0",
    "neoforge": "格式 <NeoForge版本>：21.1.264",
}


class Catcher:
    """把 CLI 里 print 的内容接进 GUI 的日志框"""

    def __init__(self, sink):
        self.sink = sink

    def write(self, text):
        if text and text.strip():
            self.sink(text.rstrip())

    def flush(self):
        pass


class App:
    def __init__(self, root):
        self.root = root
        self.q = queue.Queue()
        self.busy = False
        self.java_map = {}

        root.title("mcroute — Minecraft 启动器")
        root.geometry("1000x680")
        root.minsize(860, 580)

        self._build_ui()
        self.refresh_versions()
        self.refresh_instances()
        self.refresh_java()
        self.refresh_account()
        self.root.after(120, self._poll)

    # ------------------------------------------------------------------ 界面
    def _build_ui(self):
        # ---- 顶部：账号 ----
        top = ttk.Frame(self.root, padding=(10, 8, 10, 2))
        top.pack(fill="x")

        self.account_label = ttk.Label(top, text="...")
        self.account_label.pack(side="left")

        ttk.Button(top, text="退出登录", command=self.do_logout).pack(side="right", padx=(6, 0))
        ttk.Button(top, text="登录", command=self.do_login).pack(side="right")

        # ---- 标签页 ----
        self.nb = ttk.Notebook(self.root)
        self.nb.pack(fill="both", expand=True, padx=10, pady=(6, 4))

        self._tab_launch()
        self._tab_loader()
        self._tab_pack()
        self._tab_instances()

        # ---- 底部：进度 + 日志 ----
        ttk.Separator(self.root, orient="horizontal").pack(fill="x")
        bottom = ttk.Frame(self.root, padding=(10, 6))
        bottom.pack(fill="both")

        prog_row = ttk.Frame(bottom)
        prog_row.pack(fill="x", pady=(0, 4))
        self.progress = ttk.Progressbar(prog_row, mode="determinate", maximum=100, value=0)
        self.progress.pack(side="left", fill="x", expand=True)
        self.progress_label = ttk.Label(prog_row, text="", width=22)
        self.progress_label.pack(side="left", padx=(8, 0))

        self.log_box = tk.Text(bottom, height=8, wrap="word")
        self.log_box.pack(fill="both", expand=True, pady=(2, 0))

    # ---- 标签页 1：启动 ----
    def _tab_launch(self):
        page = ttk.Frame(self.nb, padding=10)
        self.nb.add(page, text="  启动  ")

        left = ttk.Frame(page)
        left.pack(side="left", fill="both", expand=True)

        ttk.Label(left, text="本地已安装版本").pack(anchor="w")
        self.listbox = tk.Listbox(left, height=14, exportselection=False)
        self.listbox.pack(fill="both", expand=True, pady=(4, 6))

        row = ttk.Frame(left)
        row.pack(fill="x")
        ttk.Button(row, text="刷新", command=self.refresh_versions).pack(side="left")
        ttk.Button(row, text="▶ 启动选中版本", command=self.do_launch).pack(side="left", padx=6)

        right = ttk.Frame(page)
        right.pack(side="left", fill="y", padx=(14, 0))

        ttk.Label(right, text="下载原版版本").pack(anchor="w")
        self.ver_entry = ttk.Entry(right, width=26)
        self.ver_entry.pack(anchor="w", pady=(4, 4))
        self.ver_entry.insert(0, "1.21.1")

        self.skip_assets = tk.BooleanVar(value=False)
        ttk.Checkbutton(right, text="跳过资源（快，但没有音效贴图）",
                        variable=self.skip_assets).pack(anchor="w")
        ttk.Button(right, text="开始下载", command=self.do_download).pack(anchor="w", pady=(6, 0))

        ttk.Separator(right, orient="horizontal").pack(fill="x", pady=10)

        ttk.Label(right, text="Java（自动按版本匹配）").pack(anchor="w")
        self.java_combo = ttk.Combobox(right, width=46, state="readonly")
        self.java_combo.pack(anchor="w", pady=(4, 2))
        ttk.Button(right, text="重新检测 Java", command=self.refresh_java).pack(anchor="w")

        ttk.Label(right, text="最大内存").pack(anchor="w", pady=(10, 0))
        self.mem_entry = ttk.Entry(right, width=10)
        self.mem_entry.pack(anchor="w", pady=(4, 0))
        self.mem_entry.insert(0, "2G")
        ttk.Label(right, text="整合包通常要 4G 以上", foreground="#666").pack(anchor="w")

    # ---- 标签页 2：加载器 ----
    def _tab_loader(self):
        page = ttk.Frame(self.nb, padding=14)
        self.nb.add(page, text="  加载器  ")

        ttk.Label(page, text="给某个 MC 版本装加载器（自动下载 + 安装 + 补库）",
                  foreground="#444").pack(anchor="w")

        form = ttk.Frame(page)
        form.pack(anchor="w", pady=(12, 0))

        ttk.Label(form, text="加载器").grid(row=0, column=0, sticky="w", pady=4)
        self.loader_combo = ttk.Combobox(form, width=16, state="readonly", values=LOADER_KINDS)
        self.loader_combo.grid(row=0, column=1, sticky="w", padx=(8, 0), pady=4)
        self.loader_combo.set("fabric")
        self.loader_combo.bind("<<ComboboxSelected>>", lambda e: self._update_loader_hint())

        ttk.Label(form, text="版本").grid(row=1, column=0, sticky="w", pady=4)
        self.loader_ver = ttk.Entry(form, width=28)
        self.loader_ver.grid(row=1, column=1, sticky="w", padx=(8, 0), pady=4)
        self.loader_ver.insert(0, "1.20.1")

        self.loader_hint = ttk.Label(page, text="", foreground="#666")
        self.loader_hint.pack(anchor="w", pady=(8, 0))
        self._update_loader_hint()

        ttk.Button(page, text="安装加载器", command=self.do_loader).pack(anchor="w", pady=(12, 0))

        note = ("说明：Forge / NeoForge 会调用官方安装器（需要几十秒到几分钟，第一次慢）；\n"
                "Fabric / Quilt / Legacy Fabric 直接从官方 meta 合成，几秒完成。\n"
                "装完后到「启动」页刷新，就能看到一个带加载器的新版本。")
        ttk.Label(page, text=note, foreground="#666", justify="left").pack(anchor="w", pady=(16, 0))

    def _update_loader_hint(self):
        kind = self.loader_combo.get()
        self.loader_hint.config(text=LOADER_HINTS.get(kind, ""))

    # ---- 标签页 3：整合包 ----
    def _tab_pack(self):
        page = ttk.Frame(self.nb, padding=14)
        self.nb.add(page, text="  整合包  ")

        ttk.Label(page, text="支持 Modrinth .mrpack / MultiMC·Prism 实例包 / CurseForge zip（自动识别）",
                  foreground="#444").pack(anchor="w")

        row = ttk.Frame(page)
        row.pack(fill="x", pady=(12, 0))

        self.pack_entry = ttk.Entry(row)
        self.pack_entry.pack(side="left", fill="x", expand=True)
        ttk.Button(row, text="浏览...", command=self.pick_pack).pack(side="left", padx=(6, 0))

        opts = ttk.Frame(page)
        opts.pack(fill="x", pady=(8, 0))
        self.pack_no_assets = tk.BooleanVar(value=False)
        ttk.Checkbutton(opts, text="跳过原版资源（不建议：整合包缺资源容易崩）",
                        variable=self.pack_no_assets).pack(anchor="w")

        ttk.Button(page, text="安装这个整合包", command=self.do_pack).pack(anchor="w", pady=(10, 0))

        note = ("说明：\n"
                "· mrpack 会按清单下载 mod（几十到几百 MB）\n"
                "· MultiMC 包的 mod 已在包里，直接解压\n"
                "· CurseForge 包需要 API key：环境变量 CF_API_KEY，"
                "或写进 mcdata/cf_api_key.txt\n"
                "装完到「实例」页就能启动。")
        ttk.Label(page, text=note, foreground="#666", justify="left").pack(anchor="w", pady=(16, 0))

    # ---- 标签页 4：实例 ----
    def _tab_instances(self):
        page = ttk.Frame(self.nb, padding=10)
        self.nb.add(page, text="  实例  ")

        ttk.Label(page, text="已安装的整合包实例（每个实例有独立的 mods/config/saves）").pack(anchor="w")

        self.inst_list = tk.Listbox(page, height=14, exportselection=False)
        self.inst_list.pack(fill="both", expand=True, pady=(6, 6))

        row = ttk.Frame(page)
        row.pack(fill="x")
        ttk.Button(row, text="刷新", command=self.refresh_instances).pack(side="left")
        ttk.Button(row, text="▶ 启动选中实例", command=self.do_launch_instance).pack(side="left", padx=6)
        ttk.Button(row, text="打开实例目录", command=self.open_instance_dir).pack(side="left")

        self.inst_meta = []

    # ------------------------------------------------------------------ 日志
    def log(self, text):
        self.q.put(text)

    PROGRESS_RE = re.compile(r"进度\s+(\d+)/(\d+)")

    def _poll(self):
        while True:
            try:
                line = self.q.get_nowait()
            except queue.Empty:
                break
            self.log_box.insert("end", line + "\n")
            self.log_box.see("end")
            self._update_progress(line)
        self.root.after(120, self._poll)

    def _update_progress(self, line):
        match = self.PROGRESS_RE.search(line)
        if not match:
            return
        done = int(match.group(1))
        total = max(int(match.group(2)), 1)
        self.progress.configure(maximum=total, value=done)
        self.progress_label.config(
            text="{}/{}   {}%".format(done, total, int(done * 100 / total))
        )

    def _reset_progress(self):
        self.progress.configure(maximum=100, value=0)
        self.progress_label.config(text="")

    # ------------------------------------------------------------------ 数据刷新
    def refresh_versions(self):
        self.listbox.delete(0, "end")
        vroot = os.path.join(DATA_DIR, "versions")
        if not os.path.isdir(vroot):
            self.log("还没有本地版本，先到「启动」页下载一个原版版本。")
            return
        names = [n for n in sorted(os.listdir(vroot))
                 if os.path.isdir(os.path.join(vroot, n))]
        for name in names:
            self.listbox.insert("end", name)
        self.log("找到 {} 个本地版本".format(len(names)))

    def refresh_instances(self):
        self.inst_list.delete(0, "end")
        self.inst_meta = []
        iroot = os.path.join(DATA_DIR, "instances")
        if not os.path.isdir(iroot):
            return
        for name in sorted(os.listdir(iroot)):
            meta_path = os.path.join(iroot, name, "instance.json")
            if not os.path.isfile(meta_path):
                continue
            try:
                with open(meta_path, "r", encoding="utf-8") as f:
                    meta = json.load(f)
            except Exception:
                continue
            self.inst_meta.append(meta)
            self.inst_list.insert(
                "end",
                "{}   [{}]".format(meta.get("name") or name, meta.get("version_id", "?")),
            )
        self.log("找到 {} 个整合包实例".format(len(self.inst_meta)))

    def refresh_java(self):
        try:
            items = mcroute.scan_java(DATA_DIR)
        except Exception as exc:
            self.log("检测 Java 失败：" + str(exc))
            items = []

        self.java_map = {}
        labels = []
        for exe, major in items:
            label = "Java {:<2}  {}".format(major, exe)
            labels.append(label)
            self.java_map[label] = exe

        self.java_combo["values"] = labels
        if labels:
            # 优先选 Java 21（多数新版本用得上）
            pick = next((l for l in labels if l.startswith("Java 21")), labels[0])
            self.java_combo.set(pick)
            self.log("检测到 {} 个 Java".format(len(labels)))
        else:
            self.log("没检测到 Java，请安装一个 JDK/JRE")

    def refresh_account(self):
        try:
            auth = mcroute.auth_load()
        except Exception:
            auth = None
        if auth and auth.get("name"):
            self.account_label.config(text="账号：" + str(auth.get("name")) + "（在线）")
        else:
            self.account_label.config(text="未登录 · 离线模式（可单机游玩）")

    # ------------------------------------------------------------------ 任务
    def _run(self, func, ns, done=None):
        if self.busy:
            messagebox.showinfo("稍等", "上一个任务还没结束")
            return
        self.busy = True

        def worker():
            old_stdout = sys.stdout
            sys.stdout = Catcher(self.q.put)
            try:
                func(ns)
            except Exception as exc:
                self.q.put("[异常] " + str(exc))
            finally:
                sys.stdout = old_stdout
                self.busy = False
                if done:
                    self.root.after(0, done)

        threading.Thread(target=worker, daemon=True).start()

    def _java(self):
        label = self.java_combo.get()
        return self.java_map.get(label) or None

    def _memory(self):
        text = self.mem_entry.get().strip() if hasattr(self, "mem_entry") else ""
        return text or "2G"

    # ---- 账号 ----
    def do_login(self):
        self._run(mcroute.cmd_login, argparse.Namespace(), done=self.refresh_account)

    def do_logout(self):
        self._run(mcroute.cmd_logout, argparse.Namespace(), done=self.refresh_account)

    # ---- 下载 ----
    def do_download(self):
        ver = self.ver_entry.get().strip()
        if not ver:
            messagebox.showinfo("提示", "先填一个版本号，例如 1.21.1")
            return
        self._reset_progress()
        self.log("开始下载 {} ...".format(ver))
        ns = argparse.Namespace(
            version=ver,
            dir=DATA_DIR,
            workers=16,
            no_assets=bool(self.skip_assets.get()),
        )
        self._run(mcroute.cmd_fetch, ns, done=self.refresh_versions)

    # ---- 启动 ----
    def _launch(self, version, game_dir=None):
        self.log("启动 {} ...".format(version))
        ns = argparse.Namespace(
            version=version,
            dir=DATA_DIR,
            java=self._java(),
            game_dir=game_dir,
            username="Player",
            force=False,
            wait=False,
            memory=self._memory(),
        )
        self._run(mcroute.cmd_launch, ns)

    def do_launch(self):
        picked = self.listbox.curselection()
        if not picked:
            messagebox.showinfo("提示", "先在左边选一个版本")
            return
        self._launch(self.listbox.get(picked[0]))

    def do_launch_instance(self):
        picked = self.inst_list.curselection()
        if not picked:
            messagebox.showinfo("提示", "先选一个实例")
            return
        meta = self.inst_meta[picked[0]]
        self._launch(meta.get("version_id"), meta.get("game_dir"))

    def open_instance_dir(self):
        picked = self.inst_list.curselection()
        if not picked:
            return
        meta = self.inst_meta[picked[0]]
        path = meta.get("game_dir") or ""
        if os.path.isdir(path):
            os.startfile(path)

    # ---- 加载器 ----
    def do_loader(self):
        kind = self.loader_combo.get()
        ver = self.loader_ver.get().strip()
        if not ver:
            messagebox.showinfo("提示", "先填加载器版本，例如 1.20.1 或 1.20.1-47.2.0")
            return
        self._reset_progress()
        self.log("安装加载器 {} {} ...".format(kind, ver))
        ns = argparse.Namespace(
            kind=kind,
            version=ver,
            dir=DATA_DIR,
            java=self._java(),
        )
        self._run(mcroute.cmd_loader, ns, done=self.refresh_versions)

    # ---- 整合包 ----
    def pick_pack(self):
        path = filedialog.askopenfilename(
            title="选择整合包",
            filetypes=[("整合包", "*.mrpack *.zip"), ("所有文件", "*.*")],
        )
        if path:
            self.pack_entry.delete(0, "end")
            self.pack_entry.insert(0, path)

    def do_pack(self):
        path = self.pack_entry.get().strip().strip('"')
        if not path or not os.path.isfile(path):
            messagebox.showinfo("提示", "先选一个整合包文件")
            return
        if not messagebox.askyesno("确认", "要安装这个整合包吗？\n\n" + path):
            return
        self._reset_progress()
        self.log("安装整合包：" + os.path.basename(path))
        ns = argparse.Namespace(
            pack=path,
            dir=DATA_DIR,
            workers=16,
            no_assets=bool(self.pack_no_assets.get()),
        )
        self._run(mcroute.cmd_install, ns,
                  done=lambda: (self.refresh_versions(), self.refresh_instances()))


def main():
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass

    root = tk.Tk()
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
