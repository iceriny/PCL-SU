# PCL SU

PCL SU（Plain Craft Launcher SU）是基于 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的 Minecraft Java 版启动器分支，继承 [PCL](https://github.com/Meloong-Git/PCL) 与社区版的功能和开源成果。

[下载](https://github.com/iceriny/PCL-SU/releases) · [问题反馈](https://github.com/iceriny/PCL-SU/issues/new/choose) · [贡献代码](https://github.com/iceriny/PCL-SU/pulls)

## 功能

- 管理 Minecraft 实例、Java、模组、整合包与存档。
- 创建微软账号、第三方认证服务器和离线档案。
- 从 PCL SU 仓库获取启动器更新。
- 自定义主页、背景、主题与启动选项。

## 运行与构建

运行环境为 Windows 和 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。建议使用 Windows 10 1809 或更高版本。

安装 .NET 10 SDK 后，可在仓库根目录构建：

```powershell
dotnet build "Plain Craft Launcher 2.slnx" --configuration Release -p:Platform=x64
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" --configuration Release -p:Platform=x64 --no-self-contained --output "Plain Craft Launcher 2/bin/Release/publish"
```

微软登录需要为此分支配置可用于 Minecraft 身份验证的客户端 ID；CurseForge 官方 API 需要 API Key。发布流程通过仓库的 Actions Secrets 注入这些服务配置。

## 自动构建与发布

推送到 `dev`、提交 Pull Request 或手动运行 `Build (CI)` 时，先在 Windows 执行下载、登录诊断、档案与配置相关回归，再构建 x64 和 ARM64 产物。

本地安装 .NET 10 SDK 后，可复用相同的回归入口：

```powershell
./scripts/Invoke-RegressionTests.ps1
```

发布稳定版时，同步修改 `metadata.json` 中的版本与递增代码、主项目的程序集版本，并添加 `release-notes/<版本>.md`。将提交推送到 `dev` 后运行：

```powershell
gh workflow run release-publish.yml --ref dev -f version=2.15.2
```

发布工作流校验版本、提交与现有标签，执行回归并构建两个架构。校验和、Sigstore 签名及全部产物准备完成后，才创建或更新同一提交的草稿 Release 并公开发布。发布资产使用 `PCL_SU_Release_x64.exe`、`PCL_SU_Release_ARM64.exe` 及对应 `.sha256`、`.sigstore.json`，启动器可通过自身更新入口获取。

## 服务配置

在本仓库的 Actions Secrets 中配置下列值，重新构建后生效：

| Secret | 用途 |
| --- | --- |
| `PCL_MS_CLIENT_ID` | Microsoft 个人账号的公共客户端 ID，启用设备代码登录 |
| `CURSEFORGE_API_KEY` | CurseForge 官方资源 API 与文件下载 |
| `SENTRY_DSN` | 可选的 SU 遥测服务；未配置时不启用遥测 |
| `LINK_SERVER_ROOTS` | 自建大厅服务根地址，多个地址用 `\|` 分隔；配置后显示大厅入口 |
| `NAID_CLIENT_ID`、`NAID_CLIENT_SECRET` | 自建大厅需要 Natayark 登录时使用 |
| `LOBBY_DEFAULT_SECRET` | 自建大厅的默认联机密钥 |
| `LOBBY_REPORT_ENDPOINT` | 可选的大厅上报接口；未配置时不发送上报 |
| `TELEMETRY_KEY` | 大厅上报接口的 Bearer 认证密钥 |
| `LOBBY_PRIVACY_URL` | 自建大厅的隐私政策链接 |

大厅后端保留现有协议。服务根地址需提供 `/api/link/v2/cache.ini` 与 `/api/link/v2/announce.json`，具体格式与上报约定见 `PageToolsGameLink`、`LobbyController`。未配置服务时隐藏大厅入口。

SU 使用独立的 `PCLSU` 用户数据目录，首次运行时复制已有 CE 共享配置及其加密密钥，原数据保留。离线与第三方档案无需上述服务密钥。

## 许可证与致谢

- `Plain Craft Launcher 2/` 遵循目录中的[自定义许可证](Plain%20Craft%20Launcher%202/LICENCE)。
- 其余目录遵循 [Apache License 2.0](LICENSE)。
- 感谢原作者[龙腾猫跃](https://github.com/Meloong-Git)、[PCL Community](https://github.com/PCL-Community) 和所有上游贡献者。
- 项目中使用的第三方组件继续遵循各自许可证，相关声明保留于源码和启动器的关于页面。
