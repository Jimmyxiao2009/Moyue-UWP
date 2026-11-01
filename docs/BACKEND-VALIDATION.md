# 后端验证记录

日期：2026-10-04。目标 Lumia 930 / Windows 10 Mobile 10.0.10586.0 / ARM32。前端页面保留为开发壳，由后续开发者实现正式界面。

## 自动测试

执行 `dotnet run --project tests/Moyue.Backend.Tests/Moyue.Backend.Tests.csproj`，32 项通过，0 项失败。运行器为 .NET 10，链接实际后端源文件，不是另一份实现。

覆盖：高级搜索 ALL/ANY/排除词/作者/标签/状态及来源身份、局部失败、缺失元数据补取、取消、本地搜索；收藏/历史/设置持久化、备份恢复、v1 迁移、原子写入失败、新资料拒绝降级；三源响应解析与章节顺序；发布页静态和 Base64 数组、候选格式验证、失败保留旧线路；JM 请求时间戳及加密；像素重排、AES-CBC 和内存预算；下载并发上限、串行解码、暂停/恢复（含快照对象和阅读等待竞态）、网络等待、挂起、同页数换序、失败重试、CDN 换线、同页请求去重、下载文件 SHA-256 校验及缓存清理隔离。

HTTP、存储、图片解码和网络状态以可控替身注入，验证成功和故障路径。测试里的简化 JPEG 字节只用于存储/调度，不验证 Windows 实际图片解码。

## 构建

使用 VS2017 Community 15.9 / MSBuild 15 / Windows SDK 19041。UWP target 19041，包最低版本保持 10586；并未安装 10586 SDK。

| 项目 / 配置 | 结果 |
|---|---|
| Moyue.Backend / netstandard1.4 / Release | 成功；0 错误 |
| UWP / x86 / Debug | 成功；0 错误 |
| UWP / ARM / Debug | 成功；0 错误 |
| UWP / ARM / Release / .NET Native | 成功；0 错误 |

现代 SDK 对 netstandard1.4 发出 NETSDK1215 建议警告，此目标为旧 UWP 兼容性保留。Release 的 Default.rd.xml 已作为 Content 输入编译，以保留 JSON 反射资料，不再出现“未提供运行时指令”的 ILT0027 警告。

检查 ARM Native 生成清单：MinVersion=10.0.10586.0，依赖 Microsoft.NET.Native.Framework.1.7、Microsoft.NET.Native.Runtime.1.7 和 Microsoft.VCLibs.140.00。本机对应 ARM Native Framework/Runtime 1.7 依赖包的最低系统均为 10049。编译与清单检查不等于已在手机上安装运行。

构建示例（PowerShell）：

```powershell
dotnet build src/Moyue.Backend/Moyue.Backend.csproj -c Release
& 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe' app\Moyue\Moyue.csproj /t:Build /p:Configuration=Release /p:Platform=ARM /p:OutputPath=bin\Verification\ARM\Release\ /v:minimal /nologo
```

先前阶段通过 Computer Use 在 VS2017 编译、部署并启动过桌面开发壳。此次后端改动以命令行重新构建；不将先前启动视为最终后端版本的 UI 验收。当前没有交付已签名手机安装包。

## 桌面实网

| 来源 | 实际结果 | 尚未证明 |
|---|---|---|
| JM | 动态 API 列表同步后 4 条候选通过接口核验；列表、详情、1 章的 23 个图片地址解析成功；单字“星”搜索成功返回 0 条，未伪造结果 | 实际图片下载、原生 WebP 运行和手机 TLS；搜索返回空也不证明高级条件能查遍全站 |
| 香香腐宅 | 搜索返回 18 条；示例详情解析 60 章，示例章解析 60 个图片地址；已排除空 data-original 对应的广告 src | 图片端到端解码下载、需要登录章节和手机 TLS |
| 漫蛙 | 已解析最新发布页 manwala.cc 的 8 条静态 Base64 线路；旧阅读线路出现 HTTP 403 或跳转至其他站点；新 8 条线路的搜索路径均 HTTP 404，未取得可验证的漫画页 | 当前站点搜索/详情/阅读的联网验收，不能列为可用来源 |

实网命令：`dotnet run --project tests/Moyue.Backend.Tests/Moyue.Backend.Tests.csproj -- --jm` 与 `--boy`。`--live` 是三源诊断，受外部网络和站点状态影响，不属于稳定自动测试。实网检查读取响应资料，没有把站点内容写成测试样本。

发布入口依据：[JM 维护者配置文档](https://jmcomic.readthedocs.io/zh-cn/latest/api/config/)、[香香发布页](https://boylovepage.github.io/)、[漫蛙下载页](https://www.mwapp.cc/) 与 [漫蛙发布页](https://manwala.cc/)。解析规则参考维护者的一手实现：[BoyLove](https://github.com/keiyoushi/extensions-source/tree/main/src/zh/boylove)、[Manwa](https://github.com/keiyoushi/extensions-source/tree/main/src/zh/manwa)、[JM 客户端](https://github.com/hect0x7/JMComic-Crawler-Python/blob/master/src/jmcomic/jm_client_impl.py)。这些外部端点可随时变化，自动同步必须先验证再启用；同步失败保留旧线路。

## 尚待设备和外部条件验收

10586 真机的证书链/TLS、Universal.WebP ARM 组件激活及真实 JPEG/PNG/WebP 解码、连续长章节峰值内存、挂起/恢复和存储满需要手机运行测试。当前只证明后端故障路径、编译及上述桌面元数据接口范围，不能承诺三家始终可连通。

漫蛙需要一个当前能返回漫画页的公开线路，或站点更新后的接口契约，才能完成联网验收。已有解析器拒绝错误站点和验证页，不把它们当作空搜索成功。用户手动选择的发布页和 API/图片线路可以通过后端接口更新。

下载在应用前台/获准运行期间执行；保存后可恢复。没有宣称 10586 应用退出后持续解析、解码与下载全部章节。
