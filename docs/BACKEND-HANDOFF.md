# 后端交接

所有类型位于 `Moyue` 命名空间。现有 MainPage/ReaderPage 是开发壳，不是后端 API。前端请使用下面的服务，不要继续复制页面内旧的搜索调度逻辑。

## 初始化及生命周期

UWP 初始化顺序：

```csharp
var storage = new WindowsBackendStorage();
var store = await Store.Open(storage);
var sources = new Sources(store);
var search = new SearchService(store, sources);
var images = new ImageService(store, sources, new WindowsImageCodec());
var downloads = new DownloadEngine(store, sources, images, new WindowsNetworkPolicy());
downloads.Start();
// 低频同步；失败保留已保存线路，取消令牌随生命周期取消。
await sources.AutoSync(cancellationToken);
```

独立库调用者须注入 `IBackendStorage`、`IImageCodec` 和 `INetworkPolicy`。Windows 实现分别在 WindowsBackend.cs 和 WindowsImageCodec.cs；测试替身只用于自动测试，不能加入生产 App。

对接有两种方式：直接沿用现有 UWP 工程的后端源文件；或者引用 `src/Moyue.Backend/bin/Release/netstandard1.4/Moyue.Backend.dll`，只编入 WindowsBackend.cs、WindowsImageCodec.cs、WebpDecode.cs 三个 Windows 适配文件，并引用 Universal.WebP 原生组件、Newtonsoft.Json 13.0.3 和 HtmlAgilityPack 1.11.46。DLL 方式不要编入其余后端源文件或 WindowsBackendConvenience.cs；partial 类型不能跨程序集组合。VS2017 不建议直接 ProjectReference SDK 风格的后端工程，先用 dotnet 构建 DLL。

DLL 方式阅读文件：`var key = await images.GetPage(book, chapter, pageInfo, index, false, ct); var file = await storage.File(key);`。`ImageService.Page`、无参数 `Store.Open` 及三个参数的下载构造函数只在源文件集成时提供。

挂起调用 `downloads.Stop()` 并等待最后一次 checkpoint，在系统 deferral 允许的时间内保存。恢复调用 `Start()`。进入阅读时令 `images.Reading=true`，退出后恢复 false。该标志控制“阅读优先”调度，已经在进行的请求在当前页完成后让出。

Changed、搜索 arrived 回调可来自后台线程，前端须通过 Dispatcher 更新 UI。界面取消旧查询时取消原令牌，结果不得继续写入新页面。

打开资料后检查 `store.LoadWarning`，非空时向用户展示恢复备份的提示；不要把恢复结果当作完全无损恢复。`Sources` 和下载引擎在应用生命周期结束时 Dispose，挂起优先等待 Stop。

## 服务映射

| 功能 | 后端入口 | 结果 / 约束 |
|---|---|---|
| 收藏 | Store.SetFavorite(book, true/false) | 持久化；主键为来源 + 漫画 ID |
| 书架/历史/设置快照 | Store.Snapshot() | 深复制，可用于绑定；不直接改副本 |
| 保存进度 | Store.Record(book, chapter, page, offset) | page 从 0 起；offset 0～1；保存章节身份及时间 |
| 删除/清空历史 | Store.ClearHistory(bookKey/null) | 不影响收藏和下载 |
| 普通/高级联合搜索 | SearchService.Search(options, page, ct, arrived) | 每源独立错误与结果；部分失败不丢其他来源 |
| 本地搜索 | SearchOptions.Local=true | 收藏、已知资料、历史、下载元数据；离线可用 |
| ID 直达 | SearchService.ById(source, id, ct) | 单一来源，正整数 ID |
| 原生列表/发现 | Sources.Search(source, "", page, ct) | 接口支持时返回列表；网页验证失败明确报错 |
| 详情及章节 | Sources.Detail(book, ct) | 返回新的 Book；调用者替换旧引用；按阅读顺序排列 |
| 解析章节图片 | Sources.Pages(book, chapter, ct) | PageInfo 有重排/解密参数、referer、稳定身份与备用地址 |
| 阅读图片 | UWP ImageService.Page(..., false, ct) | 已下载且校验通过的文件优先；否则磁盘缓存/网络 |
| 独立库图片 | ImageService.GetPage(...) | 返回相对存储 key；通过注入的 Storage 读取 |
| 添加下载 | DownloadEngine.Add(book, chapter) | 防重复；默认并发 2，范围 1～32（Settings.MaxConcurrency）；解码串行 |
| 暂停/恢复/重试 | Pause(job) / Resume(job) | 取消正在请求的任务，保留已保存页，补缺页 |
| 删除下载 | Remove(job) | 等待活动任务退出，再删文件与清单；保留收藏/历史 |
| 下载等待当前调度 | Completion | 当前调度任务；新任务可能产生新的 Completion |
| 清理缓存 | ImageService.ClearCache() | 只删除 cache，主动 downloads 保留 |
| 同步发布页 | Sources.Sync(source, ct) | 候选通过接口/网页格式验证后启用；失败保留旧列表 |
| 添加/选择线路 | Sources.AddLine(source, url, image:false/true) | 网站/API 与图片分别管理；要求 HTTPS |
| 修改发布页 | Sources.SetPublication(source, url) | 手动配置可信发布入口，重置自动同步尝试间隔 |

设置修改须在 `lock(store.SyncRoot)` 内操作 `store.Data.Settings`，然后 `await store.Save()`；生产前端通过这些 service 方法修改数据。`Data` 为旧开发壳兼容入口，不应用作跨线程绑定集合。

## 搜索语义

SearchOptions 包含 Query、Sources、Any、Exclude、Author、Tags、State、Sort 和 Local。

- ALL 在已有标题/标签资料中匹配全部词；ANY 合并最多四个关键词查询，再去重。来源身份不同的同名漫画不合并。
- JM 支持的关键词、标签及排除词语法下推到 API；其他源先查询候选，再在客户端核验条件。
- 作者、标签、状态缺少时，每源每页最多补取 16 份详情。仍未知或补取失败不会伪装成匹配，SourceSearchResult.MetadataUnknown 提供统计。
- 状态为“全部”“连载”“完结”；JM 未提供可靠状态时视为未知。Sort 为“相关度”或“标题”，相关度仅为本地标题匹配评分，不宣称跨源热度。
- 所有额外条件作用于已加载资料，SearchBatch.FiltersLoadedResultsOnly=true；MoreMayExist 只表示仍可能有下一页，不代表站点的精确总量或最后一页。
- arrived 每源返回一次，可先展示成功来源。网络失败放入该源 Error；用户取消向上抛 OperationCanceledException。

## 文件与恢复

library.json 包含收藏、元数据、历史、设置、来源和下载清单；library.backup.json 保存上个有效版本。写入先临时文件，再原子改名。v1 清单迁移到 v2，旧的“仅文件长度判成功”章节重新核验。遇到高于 v2 的资料会拒绝打开，避免降级覆盖新版本数据。

downloads/<章节 key 的 MD5>/<页号>.jpg 保存已还原图片；cache/<key>.jpg 为可删除缓存。SavedPage 保存资源路径身份、SHA-256、长度。恢复核验 JPEG 边界及 hash；同页数但图片换序不会复用旧图。URL 的签名/域名变化不会改变稳定图片身份。

下载只有每页最终写入、清单保存和校验成功后才标“已完成”。每页最多重试三次；自动图片换线最多四个候选。暂停可恢复，挂起/终止后重新补缺页；不承诺 App 终止后继续无限解析下载。

## 老设备图片预算

网络输入、完整像素、重排目标和编码临时开销都纳入限制。老设备模式单响应最多 8 MB，单解码估算预算 64 MB；一般模式 32 MB / 160 MB。全局最多 32 个图片处理流水线（与下载并发上限一致，HttpClientHandler 的每主机连接数同步提高到 32），解码只有一个；列表及阅读器还须由前端控制同时可见图片、预加载数量和 BitmapImage.DecodePixelWidth。

JPEG/PNG 由系统解码，WebP 由 Universal.WebP 原生组件处理；JM 上下条带重排，香香横向条带还原，漫蛙标记的 AES-CBC 图片解密。超过预算会报错，不会隐式关闭老设备模式。

## 网络可用性

接口被站点改版、验证页或访问权限挡住时，后端会返回具体错误，不执行网页脚本或绕过验证。发布页支持静态超链接、Base64 JSON 数组及 Base64 URL 字符串数组；只提供候选，候选需通过站点格式/API 核验。漫蛙默认入口为 manwala.cc；旧内置 mwmissing3.cc 配置迁移到这个入口，手动配置的其他入口保留。JM 发布页与旧版客户端一致，是 BytePlus 上加密的域名列表 `newsvr-2025.txt`（配置值优先，内置三个镜像兜底）；所有已知 API 节点都失败时会自动从发布页刷新一次再重试。

HTTPS 能否在 10586 上握手仍取决于站点的 TLS/证书链；桌面联网成功不替代手机实测。请对照 BACKEND-VALIDATION.md 查看已验证范围。
