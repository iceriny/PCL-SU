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
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" --configuration Release -p:Platform=x64 --no-self-contained --output "artifacts/publish/x64"
```

微软登录需要为此分支配置可用于 Minecraft 身份验证的客户端 ID；CurseForge 官方 API 需要 API Key。发布流程通过仓库的 Actions Secrets 注入这些服务配置。

## 自动构建与发布

推送到 `dev`、提交 Pull Request 或手动运行 `Build (CI)` 时，先在 Windows 执行下载、登录诊断、档案与配置相关回归，再构建 x64 和 ARM64 产物。

本地安装 .NET 10 SDK 后，可复用相同的回归入口：

```powershell
./scripts/Invoke-RegressionTests.ps1
```

在 `metadata.json` 维护主版本与先行版本，更新 `release-notes/current.md`。将提交推送到 `dev` 后运行：

```powershell
gh workflow run release-publish.yml --ref dev
```

发布准备阶段按上海日期和既有 Release 自动分配构建号，回归与两个架构共用同一个版本。完整格式为 `X.Y.Z[-alpha.P|-beta.P|-rc.P]+yyyyMMddN`，例如 `2.15.9+202611172`、`2.15.9-rc.1+202611172`；`N` 是当日构建次数，支持多位。主版本和先行版本相同时，启动器继续比较构建日期与次数。

本地直接构建时，编号由 `artifacts/build/local-state.json` 自动维护。程序集版本、文件版本、程序展示版本由构建工具从元数据生成。

工作流执行回归并构建两个架构。校验和、Sigstore 签名及全部产物准备完成后，才创建或更新同一提交的草稿 Release 并公开发布。稳定版资产使用 `PCL_SU_Release_x64.exe`、`PCL_SU_Release_ARM64.exe`，先行版使用 `PCL_SU_Beta_x64.exe`、`PCL_SU_Beta_ARM64.exe`，同时提供对应 `.sha256`、`.sigstore.json`。

### 更新日志格式

`release-notes/current.md` 使用 UTF-8 Markdown，构建时自动生成以完整版本命名的日志，发布时原样作为 GitHub Release 正文，无需手工改写构建号或日志文件名。发布准备阶段合并已有 Release 与仓库历史，冻结最近 5 份日志供测试和两个架构共同使用；EXE 启动时读取对应日志，离线与手动替换 EXE 均可显示。更新页读取唯一一组 `<summary>...</summary>` 之间的文字作为摘要，“查看详情”显示整篇 Markdown。摘要写成一行纯文本，用一两句话概括本次更新对玩家的主要作用。

```markdown
<summary>CurseForge 资源搜索与下载现已可用。</summary>

## 更新内容

- 可以搜索 CurseForge 模组、整合包等资源，并查看版本和下载。
- 改善 CurseForge 整合包导入时的资源获取体验。
```

正文围绕玩家能感受到的功能变化、问题修复，以及需要采取的升级操作展开。测试结果、构建过程和服务接入配置记录在 Actions 与开发文档中。发布前会校验摘要标签、非空摘要及正文。

## 服务配置

本地明文备份使用项目根目录的 `.env.local`，变量名见 `.env.example`。本地构建使用 `PCL_MS_CLIENT_ID` 与 `PCL_CURSEFORGE_API_KEY`；发布工作流对应的仓库 Secret 分别是 `PCL_MS_CLIENT_ID` 与 `CURSEFORGE_API_KEY`。

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
