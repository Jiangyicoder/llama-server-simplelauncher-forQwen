# Project Instructions

这是一个 Windows x64 .NET 8 WinForms 托盘启动器项目。

- 修改前先阅读 `README.md` 和 `DEVELOPMENT.md`。
- 只调整 `llama-server` 启动参数时编辑根目录的 `llama-server.args`，不要重新编译。
- 修改 C# 启动逻辑时编辑 `tray-launcher/Program.cs`；使用 README/DEVELOPMENT.md 中的 `dotnet publish` 命令构建。
- 启动器会优先查找 `llama-server\llama-server.exe`，再回退到 exe 同目录的 `llama-server.exe`。
- 保持外部参数文件的“完整替换、缺失回退内置、错误不静默回退”行为。
- 不要把 GGUF 模型、CUDA/`llama-server` 运行库、日志、`bin/`、`obj/` 或 `tray-launcher/release/` 提交到 Git。
- 参数或代码变更后检查 `git status`，并至少完成一次 `dotnet publish`；不要为了验证而启动十几 GB 的模型。
