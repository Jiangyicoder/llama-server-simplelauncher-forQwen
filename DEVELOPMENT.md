# Development Guide

这份文档是给后续维护者和代码代理使用的项目速查。运行和安装说明请先看 [README.md](README.md)。

## 先看哪里

| 文件 | 作用 |
| --- | --- |
| `tray-launcher/Program.cs` | 托盘 UI、单实例控制、参数读取、服务启动、健康检查和进程树清理 |
| `tray-launcher/Qwen3.8-Tray.csproj` | .NET 8 Windows Forms 项目和单文件发布配置 |
| `llama-server.args` | exe 同目录的外部启动参数样例；存在时完全替代内置参数 |
| `README.md` | 用户安装、目录结构和参数文件说明 |
| `RELEASE_NOTES.md` | 当前版本的发布说明和校验信息 |

## 启动流程

1. `Program.Main` 创建本地互斥体，保证只运行一个托盘实例；传入 `--exit` 时只通知现有实例退出。
2. `TrayApplicationContext` 创建日志、托盘菜单和本地健康检查计时器。
3. `ResolveServerPath` 优先查找 `llama-server\llama-server.exe`，找不到时回退到 exe 同目录的 `llama-server.exe`。
4. 如果 exe 同目录存在 `llama-server.args`，`AddExternalArguments` 读取并解析它；否则 `AddBuiltInArguments` 使用内置默认值。
5. 启动进程后加入 Windows Job Object，避免退出托盘后遗留模型进程。
6. 每两秒请求 `http://127.0.0.1:1234/health`；服务就绪后启用 Web 界面菜单。

## 修改参数还是修改代码

- 只调 `llama-server` 参数：编辑根目录的 `llama-server.args`，重启托盘即可，不需要编译。
- 外部参数文件是完整替换，不会和内置参数合并；文件为空或格式错误时启动失败并写日志。
- 参数文件支持空行、`#`/`;` 注释、引号和 `{BASE_DIR}`/`%BASE_DIR%` 路径变量。
- 修改启动逻辑、托盘菜单、健康检查、进程清理或内置回退参数：编辑 `tray-launcher/Program.cs`，然后重新发布。
- 如果改变内置默认参数，通常也应同步更新 `llama-server.args` 样例，避免文档和回退行为不一致。

## 构建与快速验证

在仓库根目录执行：

```text
dotnet publish tray-launcher/Qwen3.8-Tray.csproj -c Release -r win-x64 --self-contained false -o tray-launcher/release
```

发布输出应包含 `Qwen3.8-Tray.exe` 和 `llama-server.args`。不启动大模型也可以用下面的命令做基础启动检查；它只会通知现有实例退出：

```text
Qwen3.8-Tray.exe --exit
```

完整启动验证需要准备好 `llama-server`、所有依赖 DLL 和三个模型文件。运行日志位于 exe 同目录的 `qwen3.8-tray.log`。

## 发布检查清单

- 在 `tray-launcher/Qwen3.8-Tray.csproj` 更新 `<Version>`，并同步 `RELEASE_NOTES.md`。
- 发布 `Qwen3.8-Tray.exe`、当前 `llama-server.args` 样例和文档。
- 计算 exe 的 SHA-256，并在发布说明中记录。
- 不要把 GGUF 模型、CUDA/`llama-server` 运行库、日志、`bin/`、`obj/` 或本地发布目录提交到 Git。
- MIT 许可只覆盖本项目启动器源码；模型和第三方运行时遵循各自许可证。

## 固定行为和边界

- 服务默认绑定 `127.0.0.1:1234`，健康地址是 `/health`，Web 地址是 `/`。
- `llama-server.args` 必须和正在运行的 exe 放在同一目录；从 `tray-launcher/release` 运行时，应编辑该目录中的副本。
- 修改参数文件后必须重启托盘；已经启动的 `llama-server` 不支持热加载参数。
