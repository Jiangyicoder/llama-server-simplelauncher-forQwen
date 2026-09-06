# Qwen3.8 Tray Launcher

一个面向 Windows x64 的 `llama-server` 托盘启动器。它负责启动和关闭模型服务、监控健康状态，并提供快速打开本地 Web 界面的入口。

This repository contains a Windows x64 tray launcher for `llama-server`. It starts and stops the local model server, monitors its health endpoint, and provides a tray shortcut to the local Web UI.

> 本项目只包含托盘启动器源码和配置模板，不包含 GGUF 模型、`llama-server` 运行时或 CUDA/NVIDIA 运行库。请分别从对应的模型/运行时项目获取并遵守它们的许可证。

## 功能 / Features

- 从托盘启动、监控和关闭 `llama-server`。
- 通过 Windows Job Object 关闭时清理模型进程树。
- 模型服务就绪后显示通知，并可打开 `http://127.0.0.1:1234/`。
- 启动参数优先从 exe 同目录的 `llama-server.args` 读取，无需因为调参重新编译。
- 兼容两种 `llama-server.exe` 布局：
  1. `llama-server\llama-server.exe`（优先）；
  2. `llama-server.exe`（与托盘 exe 同目录，回退）。
- 没有外部参数文件时自动使用程序内置的默认参数。

## 发布包目录结构 / Release Layout

将发布包解压到同一个目录后，推荐使用下面的结构：

```text
Qwen3.8-27B/
├── Qwen3.8-Tray.exe
├── llama-server.args
├── llama-server/
│   ├── llama-server.exe
│   └── llama-server.exe 所需的 DLL 文件
├── Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-NVFP4-mixed.gguf
├── Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-FastMTP-32K.gguf
└── mmproj-Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-BF16.gguf
```

如果把 `llama-server.exe` 放在 `Qwen3.8-Tray.exe` 同目录，也可以直接启动；它依赖的 DLL 需要放在同目录或系统可搜索路径中。

## 使用外部启动参数 / External Arguments

仓库和 Release 包中的 [llama-server.args](llama-server.args) 是当前运行参数样例。复制或编辑它后，重启托盘启动器即可生效，不需要重新编译。外部文件存在时会完全替代内置参数；删除或移走该文件后会回退到内置参数。

格式支持：

- 空行、以 `#` 或 `;` 开头的内容会被忽略；
- 一行可以写一个或多个参数；
- 可用单引号或双引号包住带空格的参数；
- `{BASE_DIR}` 或 `%BASE_DIR%` 会替换为 `Qwen3.8-Tray.exe` 所在目录。

例如：

```text
--model "{BASE_DIR}\my-model.gguf"
--ctx-size 32768
--host 127.0.0.1
--port 1234
```

配置文件存在但为空或格式错误时，启动器会记录错误并提示，不会静默改用另一套参数。

## 运行要求 / Requirements

- Windows x64；
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)；
- 与模型和显卡环境匹配的 `llama-server` 及其 DLL；
- 对应的 GGUF 主模型、MTP 草稿模型和多模态投影文件（参数文件中的文件名可自行修改）。

启动器默认只监听 `127.0.0.1:1234`，不会主动暴露到局域网。

## 从源码构建 / Build from Source

需要安装 .NET 8 SDK。在仓库根目录执行：

```text
dotnet publish tray-launcher/Qwen3.8-Tray.csproj -c Release -r win-x64 --self-contained false -o tray-launcher/release
```

发布输出会包含 `Qwen3.8-Tray.exe` 和 `llama-server.args`。`SelfContained=false` 表示目标机器需要安装 .NET 8 Desktop Runtime。

## GitHub Release 建议 / Release Contents

建议将以下文件作为 GitHub Release 资产上传：

- `Qwen3.8-Tray.exe`
- `llama-server.args`
- `README.md`
- `LICENSE`
- `THIRD-PARTY-NOTICES.md`

为避免仓库过大，发布包不应直接提交十几 GB 的模型文件或 CUDA 运行库；这些文件应由使用者按其原始项目的发布渠道获取。

## 许可证 / License

本项目自有的托盘启动器源码采用 [MIT License](LICENSE)。模型、`llama-server`/llama.cpp、CUDA/cuBLAS 以及其他运行库不属于本许可证范围，请以各自随附的许可证和分发条款为准。
