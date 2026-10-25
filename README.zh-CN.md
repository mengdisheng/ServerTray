# ServerTray

Windows 托盘里的轻量 Linux 服务器监控工具。通过 SSH 直接读取系统指标，无需安装探针、Python 或中心后台。

当前交付为 **0.1.0-preview 候选版**。已验证短时性能，8 小时最终二进制耐久测试及多显示器实机验收仍待完成；发布门槛尚未全部签收。

[English](README.md) · [使用说明](docs/USAGE.zh-CN.md) · [性能与验证](docs/PERFORMANCE.zh-CN.md) · [安全说明](SECURITY.md) · [MIT](LICENSE)

## 功能

- 托盘常驻、快捷面板与原生 WPF 主窗口，随 Windows 切换明暗主题。
- 自行配置服务器、分组、标签、排序、密码或加密私钥，支持单级 SSH 跳板。
- CPU、内存、Swap、根盘空间、负载、磁盘 I/O、网速和运行时间。
- 1h / 6h / 24h / 7d 图表、持续异常与恢复通知、手动到期提醒。
- 全局设置与单台覆盖；配置导入先预览，导出不包含凭据。
- SSH 主机密钥核验、Windows DPAPI 凭据加密、有界调度与历史缓存。

首版面向 **Windows 11 x64、1～20 台 Linux**。采样默认 15 秒，并发默认 4。更短周期、慢网络及更大规模会增加开销。

## 开始使用

从 [Releases](https://github.com/mengdisheng/ServerTray/releases) 下载并解压绿色版 ZIP，运行 `ServerTray.exe`，点击“添加服务器”。用服务器控制台或可信渠道核对首次连接的 SHA256 主机指纹，然后在程序中确认。建议使用专用普通 SSH 用户。

关闭主窗口会继续常驻，右键托盘“退出 ServerTray”才停止程序。开机启动默认关闭，可在设置中开启。运行环境已包含在绿色版中，无需管理员权限或另外安装 .NET。

历史只记录程序在线期间。流量为所选网卡本次服务器启动后的累计值，不能作为月度计费依据。此版本不修改远端配置，也不控制远端服务。

## 构建

需要 Windows、Git、Python 3.10+ 和 `global.json` 指定的 .NET 10 SDK。

```powershell
./tools/build.ps1
```

脚本执行测试、敏感信息扫描、绿色版发布、第三方声明和 ZIP 打包。输出位于 `artifacts/release/`。首次使用需要联网还原 NuGet 包；锁文件须提交，CI 使用锁定还原。

```powershell
dotnet test ServerTray.sln -c Release
dotnet run --project tools/ServerTray.Bench -c Release -- --validate-config examples/config.example.json
```

## 验证与性能

发布门槛、实测范围和复现步骤见 [中文性能报告](docs/PERFORMANCE.zh-CN.md)。8 小时测试没有跑满之前，不标记为通过。可独立运行完整 SSH 协议夹具与性能测试：

```powershell
python -m pip install -r tools/requirements-test.txt
New-Item -ItemType Directory -Force artifacts/fixtures
python tools/ssh_fixture.py --receipt artifacts/fixtures/ssh.json
# 在另一终端运行
dotnet run --project tools/ServerTray.Bench -c Release -- --receipt artifacts/fixtures/ssh.json --self-test
./tools/benchmark.ps1 -Executable artifacts/release/ServerTray-win-x64/ServerTray.exe -Servers 20 -Seconds 28800
```

夹具只监听回环地址，使用虚构数据。仓库和发布包不包含真实服务器配置；项目没有预置监控目标、遥测或上传功能。

## 参与开发

采集、调度、配置、告警与存储位于 `ServerTray.Core`；Windows UI 和 DPAPI 适配位于 `ServerTray.App`。界面文本集中在 `Resources/Strings.resx`。提交前运行测试、扫描和 `git diff --check`；详细规范见 [CONTRIBUTING.md](CONTRIBUTING.md)。
