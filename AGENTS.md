# AGENTS.md

## 发布与打包

- 发布稳定版时，同步更新 `Plain Craft Launcher 2/metadata.json` 的版本与递增代码、主项目的 `AssemblyVersion` 和 `FileVersion`，并添加 `release-notes/<版本>.md`。
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
