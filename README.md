# PCL SU

PCL SU（Plain Craft Launcher SU）是基于 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的 Minecraft Java 版启动器分支，继承了 [PCL](https://github.com/Meloong-Git/PCL) 和社区版的功能与成果。

[下载](https://github.com/iceriny/PCL-SU/releases) · [问题反馈](https://github.com/iceriny/PCL-SU/issues/new/choose) · [贡献指南](CONTRIBUTING.md)

## 功能

- 管理 Minecraft 实例、Java、模组、整合包与存档。
- 支持微软登录、第三方认证和离线档案。
- 从 PCL SU 的 GitHub Releases 获取启动器更新。
- 自定义启动选项、主页与主题，默认使用雾玫瑰配色。

## 运行与构建

GitHub Releases 提供 Windows x64 和 ARM64 版。建议使用 Windows 10 1809 或更新版本，并安装与启动器架构相同的 [.NET 11 Runtime](https://dotnet.microsoft.com/download/dotnet/11.0)。从旧版升级时，退出启动器后替换 EXE，保留同目录下的 `PCL` 与 `.minecraft`。

本地构建使用 .NET 11 SDK。Windows 在仓库根目录执行以下命令，将 `x64` 换成 `ARM64` 可构建 ARM64 版。

```powershell
./scripts/Publish-Windows.ps1 -Architecture x64 -Configuration Release
```

原始 EXE 位于 `artifacts/publish/x64/Plain Craft Launcher 2.exe`，分发文件位于 `artifacts/PCL_SU_Release_x64.exe`，旁边有对应的 `.sha256` 校验文件。ARM64 输出使用对应的架构目录和文件后缀。

Avalonia 版还可在 Linux 和 macOS 上本地构建。Linux 桌面默认使用 X11，设置 `PCL_USE_WAYLAND=1` 可尝试原生 Wayland。Linux 和 macOS 构建同样需要对应架构的 .NET 11 SDK，运行时需要 .NET 11 Runtime。以下命令分别对 x64 Linux 与 Apple Silicon macOS 执行本地发布。其他架构使用 `linux-arm64` 或 `osx-x64`。

```bash
DOTNET="$(command -v dotnet)" ./scripts/publish.sh linux-x64 Release
DOTNET="$(command -v dotnet)" ./scripts/publish.sh osx-arm64 Release
```

[Linux 打包脚本](scripts/package-linux.sh) 在指定输出目录生成 AppImage、deb 和 rpm。例如，x64 Release 的文件名为 `PCL_SU_Release_linux_x64.AppImage`、`PCL_SU_Release_linux_x64_amd64.deb` 和 `PCL_SU_Release_linux_x64_x86_64.rpm`。deb 与 rpm 沿用 `pcl-ce` 软件包名和启动命令，现有桌面入口仍然可用。

[macOS 打包脚本](scripts/package-macos.sh) 在指定输出目录生成 `PCL SU.app`。升级时可以将新应用包放在原安装路径，以继续使用按路径启动的快捷方式。Linux 和 macOS 打包版继续从 `~/PCLCE` 读取现有数据，也支持通过 `PCL_DATA_DIR` 与 `PCL_MINECRAFT_DIR` 指定目录。

## 自动构建与发布

`Build (CI)` 在 Windows 运行回归测试，并构建 Windows x64 和 ARM64。开发时可运行同一个测试项目。

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj --configuration CI -p:Platform=x64
```

在 [metadata.json](PCL.Launcher/Resources/metadata.json) 维护主版本、先行版本与配置迁移代号，在 [release-notes/current.md](release-notes/current.md) 编写本次更新日志。推送到 `dev` 后触发发布工作流。

```powershell
gh workflow run release-publish.yml --ref dev
```

发布准备阶段按上海日期和已有 Release 分配当日构建编号，回归与两个架构共用该编号。版本格式为 `X.Y.Z[-alpha.P|-beta.P|-rc.P]+yyyyMMddN`，其中 `N` 是当日构建次数，例如 `2.15.9-rc.1+202611172`。本地构建编号由 `artifacts/build/local-state.json` 维护，构建工具生成程序展示版本、程序集版本和文件版本。

工作流通过回归和双架构构建后校验 SHA256、签名并发布 GitHub Release。正式版使用 `PCL_SU_Release_x64.exe` 和 `PCL_SU_Release_ARM64.exe`，先行版使用 `PCL_SU_Beta_x64.exe` 和 `PCL_SU_Beta_ARM64.exe`。每个 EXE 同时提供 `.sha256` 与 `.sigstore.json`。

### 更新日志

用一组单行 `<summary>...</summary>` 纯文本概括本版变化，正文说明玩家能感受到的功能变化、问题修复和升级操作。构建工具将日志按完整版本命名，发布准备阶段冻结同一份日志供两种架构和 Release 使用。启动器会从嵌入的最近 5 份日志里读取对应版本的内容，手动替换 EXE 或离线启动时也能显示。

```markdown
<summary>CurseForge 资源搜索与下载现已可用。</summary>

## 更新内容

- 可以搜索 CurseForge 模组与整合包，并查看和下载版本。
- 改善 CurseForge 整合包导入时的资源获取体验。
```

## 服务配置

Windows 本地发布脚本可读取仓库根目录的 `.env.local`，变量名见 [.env.example](.env.example)。离线与第三方档案无需微软登录配置。发布工作流从 Actions Secrets 读取服务配置。

| 本地配置 | Actions Secret | 用途 |
| --- | --- | --- |
| `PCL_MS_CLIENT_ID` | `PCL_MS_CLIENT_ID` | 已注册用于 Minecraft 身份验证的微软公共客户端 ID |
| `PCL_CURSEFORGE_API_KEY` | `CURSEFORGE_API_KEY` | CurseForge 官方资源搜索与下载 |
| `PCL_SENTRY_DSN` | `SENTRY_DSN` | 可选的遥测接收地址 |
| `PCL_LINK_SERVER_ROOT` | `LINK_SERVER_ROOTS` | 自建大厅服务根地址。多个地址用竖线分隔 |
| `PCL_NAID_CLIENT_ID`、`PCL_NAID_CLIENT_SECRET` | `NAID_CLIENT_ID`、`NAID_CLIENT_SECRET` | 自建大厅需要 Natayark 登录时提供 |
| `PCL_LOBBY_DEFAULT_SECRET` | `LOBBY_DEFAULT_SECRET` | 自建大厅的默认联机密钥 |
| `PCL_LOBBY_REPORT_ENDPOINT` | `LOBBY_REPORT_ENDPOINT` | 可选的大厅上报接口 |
| `PCL_TELEMETRY_KEY` | `TELEMETRY_KEY` | 大厅上报接口的 Bearer 认证密钥 |
| `PCL_LOBBY_PRIVACY_URL` | `LOBBY_PRIVACY_URL` | 自建大厅的隐私政策链接 |

大厅服务沿用 `/api/link/v2/cache.ini` 与 `/api/link/v2/announce.json`。启动器在配置服务后显示大厅入口。Windows 便携版继续使用同目录下的 `PCL`，共享数据单独保存在 `%APPDATA%\PCLSU`。首次从 PCL CE 迁移时会复制可用的共享配置与配置加密密钥。

## 许可证与致谢

- [PCL.Launcher](PCL.Launcher/LICENCE) 和 [PCL.Launcher.Core](PCL.Launcher.Core/LICENCE) 遵循各目录所附的自定义许可证。其余项目目录适用 [Apache License 2.0](LICENSE)。
- 感谢原作者 [龙腾猫跃](https://github.com/Meloong-Git)、[PCL Community](https://github.com/PCL-Community) 和所有上游贡献者。
- 第三方组件遵循各自的许可证。相关声明见源码与启动器的关于页面。
