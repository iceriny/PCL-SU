# AGENTS.md

## 发布与打包

- 在 `Plain Craft Launcher 2/metadata.json` 维护主版本、先行版本及配置迁移代号。构建工具自动生成构建信息、程序集和文件版本，源文件中不手工维护构建日期或次数。
- 完整版本格式为 `X.Y.Z[-alpha.P|-beta.P|-rc.P]+yyyyMMddN`，`N` 为正整数。日期按上海时区计算，次数支持多位；例如 `2.15.9-rc.1+202611172`。
- 正式发布时，准备阶段从既有 Release 自动分配当日编号，回归、x64 与 ARM64 构建共用同一编号。本地构建通过 `artifacts/build/local-state.json` 自动计数。
- 当前待发布的日志固定写入 `release-notes/current.md`，构建时自动以完整版本命名，发布阶段冻结同一份日志快照。历史取自已有 Release 与仓库日志，EXE 仅嵌入不晚于自身版本的最近 5 份，新增时剔除最旧项。启动日志从嵌入资源读取。
- 更新日志面向玩家，使用一组单行 `<summary>...</summary>` 摘要，正文简洁说明实际功能变化与问题修复。
- 本地使用 Release 配置、.NET 10 SDK、`--no-self-contained` 打包，按架构固定输出到 `artifacts/publish/x64/` 或 `artifacts/publish/ARM64/`。
- 原始打包 EXE 的固定路径：
  - `artifacts/publish/x64/Plain Craft Launcher 2.exe`
  - `artifacts/publish/ARM64/Plain Craft Launcher 2.exe`
- 用于分发的 EXE 的固定路径：
  - `artifacts/PCL_SU_Release_x64.exe`
  - `artifacts/PCL_SU_Release_ARM64.exe`
- 对应的 `.sha256` 和 `.sigstore.json` 与分发 EXE 放在同一目录，文件名追加相应后缀。
- 推送 `dev` 后，通过 `release-publish.yml` 发布。等待回归、两个架构构建、签名和 Release 发布完成，再将正式 Release 的六个资产下载到 `artifacts/`，校验 SHA256、EXE 版本与标签提交。
- 交付时给出 Release 链接和本地分发 EXE 的绝对路径，明确当前完成的版本。
