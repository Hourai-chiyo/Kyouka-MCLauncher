# mcroute-legacy（Python 版，已冻结）

> ⚠️ 这是 **历史版本**，只做兼容性修复，不再加新功能。
> 主版本（C#，157 KB 单文件）请看[仓库根目录](../README.md)。

## 这是什么

最初用 Python 写的 mcroute：核心逻辑（下载源路由 / 版本合并 / 加载器安装 / 整合包安装 / 启动）
都在 `mcroute.py` 里，图形界面在 `mcroute_gui.py`（tkinter 四页）。

保留它的原因：

1. **对照实现** —— C# 版是从它移植过来的，逻辑注释最全；遇到诡异兼容问题可以拿它对照
2. **唯一的正版登录实现** —— `login` 命令（device code → MS → XBL → XSTS）只有 Python 版有
3. **功能更全的几个命令** —— `sources`（下载源测速）、`wiki`（Wiki 索引兜底）、`status` / `logout`

## 怎么跑

```bat
:: 需要 Python 3.10+ 和 requests
pip install requests
python mcroute.py                     :: 查看全部命令
python mcroute.py fetch 1.21.1
python mcroute_gui.py                 :: 图形界面
```

## 怎么打包

```bat
build_exe.bat        :: PyInstaller -> dist\mcroute\（约 30 MB）
build_installer.bat  :: csc 编译 exe 安装器 -> dist\mcroute-setup.exe
build_msi.bat        :: WiX 生成 MSI
```

## 和主版本的差异

| | 主版本（C#） | legacy（Python） |
|---|---|---|
| 体积 | 157 KB | ~30 MB（内嵌运行时） |
| 依赖 | .NET Framework 4.8（Win10/11 自带） | 无（打包版） |
| 启动速度 | 秒开 | 1.5~2 秒 |
| 界面 | WinForms + WPF 两套新界面 | tkinter 旧界面 |
| 正版登录 | ❌ 未移植 | ✅ `login` |
| 源测速 / Wiki 兜底 | ❌ | ✅ `sources` / `wiki` |

## 目录

```
legacy/
├── mcroute.py             核心逻辑（约 3200 行）
├── mcroute_gui.py         tkinter 界面
├── build_exe.bat          打包 exe
├── build_installer.bat    打包安装器
├── build_msi.bat          打包 MSI
├── installer/             C# 安装器源码 + WiX 配置
├── packaging/             vbs 启动器 / 卸载脚本 / MSI 锚点文件
├── run.bat / run_gui.bat  源码运行入口
└── docs/                  旧界面截图
```
