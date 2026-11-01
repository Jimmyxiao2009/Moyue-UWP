# 发布入口调查与旧项目复用记录

调查日期：2026-10-04。下表保留初次调查过程，后续已实现解析与线路验证，并对 JM、香香执行桌面实网元数据检查。当前默认入口、通过情况及外部阻碍以 [后端验证记录](BACKEND-VALIDATION.md) 为准；手机端尚未验收。

## JM

- 维护者的一手 [jmcomic 配置文档](https://jmcomic.readthedocs.io/zh-cn/latest/api/config/) 将 `https://jmcomicgo.org/` 列为网页发布页。它是项目维护者记录，不能等同于已验证的移动端 API 服务。
- 网页发布页本次无法通过浏览工具读取，不能据此判断你的手机上是否可达。
- 旧项目 `src/Jm.Core/JmClient.cs:53` 有三组 BytePlus 动态 API 域名列表端点，返回加密配置，与网页地址区分。这些可作迁移参考，但本次没有重新验证响应：
  - `https://rup4a04-c01.tos-ap-southeast-1.bytepluses.com/newsvr-2025.txt`
  - `https://rup4a04-c02.tos-cn-hongkong.bytepluses.com/newsvr-2025.txt`
  - `https://rup4a04-c03.tos-cn-beijing.bytepluses.com.cn/newsvr-2025.txt`
- 图片 CDN 在旧项目 `JmImageUrl.cs` 中另有候选列表，不能从网页发布地址直接推断。

## 香香腐宅

- 发布方自称官方的 [香香腐宅公告频道](https://t.me/s/fuhouseclub?before=45) 发布了防走失页 `https://boylovepage.github.io/boylove_page/`。旧公告另有 [Trello 公告板](https://trello.com/b/V9TEaHIa)，作为备用调查线索，尚未验证当前内容及解析方式。
- [GitHub Pages 根页](https://boylovepage.github.io/) 的索引显示防走失页面，联系方式与公告一致。具体 `boylove_page/` 路径本次无法由浏览工具读取。
- 社区下载工具的 [网站接入请求](https://github.com/manga-download/hakuneko/issues/8070) 记录 `https://boylove.cc/` 及页面路径，作为交叉核对，不能代替来源自己的发布页。
- 发布页能否获得无需脚本执行的链接、当前域名归属、手机端可达性均待进一步验证。配置初始应标注“候选入口，待连通验证”。

## 漫蛙

- 本次能读取 [漫蛙走失页](https://mwmissing3.cc/)，页面提供永久和备用链接。内容支持将它作为发布页候选，但仍应检查最终落地站点是否属于同一服务。
- [漫蛙下载页](https://www.mwapp.cc/) 明确列出 `ManWaLa.CC` 发布网址及永久入口，构成交叉引用。
- [ManWaLa.CC](https://manwala.cc/) 本次只返回线路检测提示，具体地址可能由脚本或跳转选择。需调查静态配置／重定向链并验证，不能直接假设它是漫画 API 基址。
- 发布页的阅读入口、分流重定向和 APP 下载域名需分类；同步阅读线路时不导入安装包链接。

## 旧项目 WebP 和下载

已只读检查 `H:\项目\UWP\jmuwp`：

| 文件 | 可参考内容 | 迁移检查 |
| --- | --- | --- |
| `app/Jm.Uwp/WebpDecode.cs` | 检测 WebP 魔数，调用 Universal.WebP 取得尺寸及 BGRA | 10586 API 依赖、峰值分配、超长图、损坏输入 |
| `app/Jm.Uwp/libs/ARM/Universal.WebP.dll` / `.winmd` | 已有 ARM32 原生 WinRT 组件 | Release 打包、激活注册、运行库、10586 真机 |
| `app/Jm.Uwp/JmDownloader.cs` | 有界并发、还原图片、保存章节 | 失败计数、完成判定、取消、重试、换线、原子文件写入 |
| `src/Jm.Core/JmClient.cs` | JM API 域名获取及解密、多个候选请求 | 配置响应验证、缓存更新、候选快照与并发同步去重 |

原 README 记录的真机系统为 15254；本次没有编译或运行这些旧代码，也没有修改旧工程。
