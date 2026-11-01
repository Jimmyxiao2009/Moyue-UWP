# 墨阅 / Moyue

Windows 10 Mobile 漫画阅读器后端，目标 Lumia 930 / 10.0.10586.0，ARM32。现有页面是开发壳，前端由后续开发者负责。

后端包含 JM、香香腐宅、漫蛙的接口解析、三源高级搜索、书架与历史、图片处理、下载队列、发布页同步及同源线路切换。逻辑与 Windows 存储、网络状态、图片解码适配器分开，可独立测试。

- [后端交接与接口](docs/BACKEND-HANDOFF.md)
- [验证记录与限制](docs/BACKEND-VALIDATION.md)
- [原设计规划](docs/PRODUCT-PLAN.md)

## 构建

纯后端：`.NET SDK` 执行 `dotnet build src/Moyue.Backend/Moyue.Backend.csproj`，产出 `netstandard1.4` 库。目标版本为旧 UWP 兼容性选择，不能升级至 netstandard2.0。

自动测试：`dotnet run --project tests/Moyue.Backend.Tests/Moyue.Backend.Tests.csproj`。测试运行器使用 .NET 10，后端本身并不依赖 .NET 10。

UWP：VS2017 15.9、UWP 开发工作负载及 Windows SDK 19041，打开 `Moyue.sln`。桌面选择 x86，手机选择 ARM；Release 开启 .NET Native。包清单最低系统为 10586。

后端 `.cs` 文件同时编入 UWP 工程与独立后端库。DLL 对接只编入 WindowsBackend.cs、WindowsImageCodec.cs、WebpDecode.cs，引用原生 WebP 组件及 NuGet 依赖；不编入 WindowsBackendConvenience.cs 或重复的后端源文件。初始化示例见交接文档。

`Universal.WebP` 的 ARM/x86/x64 组件来自用户授权参考的旧项目，其独立来源及许可证尚未核验。发布前需确认该组件的再分发条件。
