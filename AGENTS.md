# AGENTS.md — 编译规范（所有 agent 必须遵守）

本项目是 .NET 9 WinForms 程序（G49 鼠标电量监控），入口源码在 `src/`。
编译时必须严格遵守以下规则，违反任何一条都算编译失败。

## 铁律

1. **不出项目目录**：所有读写只允许发生在 `D:\projects\G49MouseMonitor\` 内部。读系统盘上的 SDK/工具可以，但**禁止向项目目录之外的任何位置写入**（尤其是 C 盘的用户目录、临时目录、NuGet 缓存、CLI 日志等）。
2. **编译前先备份旧版本 exe**：dist 里已有可运行产物时，先把旧产物备份到 `backup/<时间戳>/`，再开始编译。时间戳格式 `yyyyMMdd-HHmmss`。
3. **NuGet 包已在项目内**：所有依赖已缓存于 `.nuget/`（由根目录 `nuget.config` 的 `globalPackagesFolder` 固定）。**不得删除或修改 `nuget.config`**，编译必须离线完成，不需要也不应该联网还原。新增依赖前必须先征得用户同意。
4. **产物放 `dist/`**：dist 是唯一的交付目录。

## 标准编译流程

在项目根目录执行。环境变量必须原样设置，把所有临时/缓存/日志写入都压进项目内：

```bash
cd /d/projects/G49MouseMonitor

# 1) 备份旧产物（dist 为空或首次编译可跳过）
TS=$(date +%Y%m%d-%H%M%S) && mkdir -p "backup/$TS"
cp dist/G49BatteryMonitor.exe dist/G49BatteryMonitor.dll dist/G49BatteryMonitor.pdb \
   dist/G49BatteryMonitor.deps.json dist/G49BatteryMonitor.runtimeconfig.json \
   dist/Microsoft.Windows.SDK.NET.dll dist/WinRT.Runtime.dll "backup/$TS/"

# 2) 编译（临时目录、CLI 主目录、HTTP 缓存全部重定向到项目内，关闭遥测）
export TMP="D:\projects\G49MouseMonitor\.tmp" \
       TEMP="D:\projects\G49MouseMonitor\.tmp" \
       DOTNET_CLI_HOME="D:\projects\G49MouseMonitor\.tmp\dotnet-home" \
       NUGET_HTTP_CACHE_PATH="D:\projects\G49MouseMonitor\.nuget\http-cache" \
       DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build src/G49BatteryMonitor.csproj -c Release

# 3) 复制产物到 dist（同样只复制这 7 个构建文件）
OUT="src/bin/Release/net9.0-windows10.0.19041.0"
cp "$OUT/G49BatteryMonitor.exe" "$OUT/G49BatteryMonitor.dll" "$OUT/G49BatteryMonitor.pdb" \
   "$OUT/G49BatteryMonitor.deps.json" "$OUT/G49BatteryMonitor.runtimeconfig.json" \
   "$OUT/Microsoft.Windows.SDK.NET.dll" "$OUT/WinRT.Runtime.dll" dist/
```

## 其他注意事项

- 编译前确认 `G49BatteryMonitor.exe` 没有正在运行（托盘程序可能常驻，文件被锁会导致覆盖失败）：`tasklist //FI "IMAGENAME eq G49BatteryMonitor.exe"`。若在运行，先报告用户，不要擅自杀进程。
- 构建要求 .NET SDK 9.x（当前环境为 9.0.318），目标框架 `net9.0-windows10.0.19041.0`（系统通知依赖 Windows SDK 投影包）。
- 编译结果应为 **0 警告 0 错误**；出现警告要在汇报中说明，不得静默。
- `.tmp/`、`.nuget/`、`obj/`、`bin/`、`backup/` 都是本地产物/缓存目录，可以自由读写，但不要把它们里的东西提交或发布。
- 不要把构建产物或备份放到项目目录以外的任何位置。
