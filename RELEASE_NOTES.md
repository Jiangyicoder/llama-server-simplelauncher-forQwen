# Qwen3.8 Tray Launcher v1.1.0

## Highlights

- 支持从 exe 同目录的 `llama-server.args` 读取全部 `llama-server` 启动参数。
- `llama-server.args` 不存在时自动回退到内置默认参数。
- `llama-server.exe` 支持两种位置：优先 `llama-server\llama-server.exe`，否则使用 exe 同目录版本。
- 托盘状态、健康检查、Web 界面入口和进程树清理保持不变。

## Included in this release

- `Qwen3.8-Tray.exe`
- `llama-server.args`（当前启动参数样例，可直接编辑）
- `README.md`
- `LICENSE`（MIT）
- `THIRD-PARTY-NOTICES.md`

## Not included

本发布不包含 GGUF 模型、`llama-server` 运行时、CUDA/cuBLAS DLL 或其他第三方运行库。请从对应项目的官方/授权渠道获取，并遵守各自许可证。

## Verification

当前 Windows x64 单文件启动器 SHA-256：

```text
1fff0834df12e30680f1c47958fe0e23932fef8d2c5e1c941884970833caf08d  Qwen3.8-Tray.exe
```

构建命令：

```text
dotnet publish tray-launcher/Qwen3.8-Tray.csproj -c Release -r win-x64 --self-contained false -o tray-launcher/release
```
