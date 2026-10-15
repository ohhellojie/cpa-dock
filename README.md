<img src="src/CPALauncher.Wpf/Assets/logo.svg" width="56" alt="CPA Dock">

# CPA Dock

A Windows desktop companion for CLIProxyAPI.

[![Release](https://img.shields.io/github/v/release/ohhellojie/cpa-dock?label=release)](https://github.com/ohhellojie/cpa-dock/releases/latest)
[![Windows](https://img.shields.io/badge/platform-Windows-5265CE)](https://github.com/ohhellojie/cpa-dock/releases/latest)
[![MIT](https://img.shields.io/badge/license-MIT-6B7280)](LICENSE)

[CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) 的 Windows 桌面启动器。

![CPA Dock（示例数据）](docs/screenshot.png)

## 日常使用

- **服务**：安装、启动、停止，打开管理页；区分托管进程和外部运行。
- **凭证**：选择或拖入 JSON 文件；覆盖同名文件、重启服务前确认。
- **诊断**：检查进程、配置和 HTTP；搜索、筛选、复制、导出日志。
- **桌面**：托盘、开机启动、自动启动 CPA、深色模式；分别更新 CPA 和启动器。

## 开始

1. 从 [Releases](https://github.com/ohhellojie/cpa-dock/releases/latest) 下载 Windows x64 自包含包。
2. 解压，运行 `CPA Dock.exe`。无需安装 .NET。
3. 点击「安装 CPA」，或在设置中选择已有程序与 `config.yaml`。
4. 启动 CPA，打开管理页。

CPA Dock 原名 CPA Launcher。设置目录保持不变，原有配置可继续使用。

旧版自动更新失败时，退出启动器，将新版解压覆盖到原目录，再运行 `CPA Dock.exe`；保留原有 `cpa` 目录和配置文件。

## 文件

设置保存在 `%AppData%\CPALauncher\settings.json`；默认安装目录为程序同级 `cpa\`。凭证目录读取 CPA 的 `auth-dir`，日志目录由运行配置推导。

设置保存时保留上一份备份，文件损坏时尝试恢复。CPA 更新先下载，再验证启动；验证失败会恢复旧版。启动器更新中断后会在下次启动时尝试恢复；无法启动时，可在安装目录的 `.cpa-update-*` 文件夹中运行 `恢复旧版.cmd`。

CPA 和启动器更新均在解压前核对发布的 SHA256；缺少校验值或校验不一致时停止更新。导入凭证前验证 JSON，同名文件安全替换，上一份保留为 `.json.bak`。需要恢复时，停止 CPA，将备份复制回原文件名后重新启动。

退出时可停止 CPA，也可保留服务。外部启动的 CPA 需要在原程序中停止或重启。

## 构建

.NET 8 · WPF · [HandyControl](https://github.com/HandyOrg/HandyControl)

```powershell
git clone https://github.com/ohhellojie/cpa-dock.git
cd cpa-dock
dotnet test tests/CPALauncher.Wpf.Tests/CPALauncher.Wpf.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

发布包位于 `artifacts\release\`。图标源文件为 `src/CPALauncher.Wpf/Assets/logo.svg`，运行 `scripts/build-icon.ps1` 可重新生成 PNG 与 ICO。

GitHub Actions 自动运行 Windows 测试，通过后生成自包含包、保留 `CPALauncher.exe` 文件名的旧版兼容包及 `SHA256SUMS.txt`。两个包使用同一程序，分别校验。构建产物保留 14 天，不会自动发布 Release。

CPA 的 API、认证与代理能力由 [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) 提供。

[MIT License](LICENSE)
