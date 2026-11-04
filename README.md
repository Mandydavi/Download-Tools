# 极速下载器 · ComfyDL

一个 Windows 上的高速下载工具。粘链接就能下——软件、压缩包、镜像、视频都行；
如果下的是模型，它会自动认出来并放进 ComfyUI 对应的 `models` 子目录。

多源竞速 + aria2 多线程分段，单文件大文件能跑满带宽。

## 运行环境

- **仅支持 Windows 10 / 11（64 位）**
- 不需要安装 .NET 运行时，程序自带
- 程序目录里没有 `aria2c.exe` 时，启动会提示自动获取下载引擎（约 1.8 MB 压缩包）

> macOS 和 Linux 目前**不支持**。代码是 C# / WPF 写的，WPF 只有 Windows 有；
> 移植到别的平台等于重写整个界面层，短期不会做。

## 下载

到 [Releases](../../releases) 下载最新版，有两种：

| 方式 | 文件 | 适合 |
|---|---|---|
| **单文件版** | `ComfyDL-x.y.z.exe` | 双击即用，不用安装，也不需要 .NET 运行时 |
| **绿色版** | `ComfyDL-x.y.z.zip` | 内含 exe、README、LICENSE，解压即用，放 U 盘也能跑 |

两个包里的程序完全一样，ZIP 只是多带了说明文件。**目前没有安装版**：不写注册表、不建开始菜单项，不想用了直接删文件即可。

## 它能下什么

**通用下载**（默认走「其他文件」目录）

- HTTP / HTTPS 直链：软件安装包、ZIP / 7z / RAR、ISO 镜像、视频、字体……
- 磁力链接与 `.torrent` 种子
- 任何站点，不需要预先支持

**模型下载**（自动归到 ComfyUI 目录）

- HuggingFace、hf-mirror、ModelScope、Civitai
- 按类型自动落到 `diffusion_models` / `loras` / `vae` / `text_encoders` / `controlnet` / `upscale_models` / `embeddings`
- 仓库地址会先列出文件清单让你挑，不用自己拼下载链接

## 功能

- **多源竞速**：同一个文件在多个镜像测速，选最快的那个
- **多线程分段**：aria2 引擎，按站点实测择优选择连接数，最高 32
- **断点续传**：中断后从断点继续，不重下
- **并发上限**：同时下几个由你定，其余排队
- **全局限速**：限制总带宽，不影响看视频
- **代理**：HTTP / SOCKS5，只对下载引擎生效
- **同名文件策略**：询问 / 覆盖 / 跳过 / 重命名
- **磁盘空间预检**：空间不够提前拦下，不会下到 99% 才失败
- **托盘常驻**：下载完成弹通知，不占任务栏
- **关闭行为三选一**：收进托盘继续下载 / 每次询问 / 直接退出
- **自动更新**：启动后静默检查新版本

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)：

```
dotnet publish ComfyDL.Desktop/ComfyDL.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out
```

## 目录结构

| 路径 | 内容 |
|---|---|
| `ComfyDL.Desktop/` | 主程序（WPF） |
| `ComfyDL.Desktop/Sources/` | 下载源适配器。新增站点只需实现 `IModelSource` 并在注册表加一行 |
| `version.json` | 更新检查用的版本元数据 |
| `.github/workflows/release.yml` | 打 tag 自动构建发版 |

## 更新机制

客户端启动 10 秒后检查更新：先读仓库的 `releases.atom` 拿到最新 tag，再取该 tag 下的 `version.json`；
版本更高就在状态条显示提示，点击跳到 Release 页面。

用 tag 地址而不是分支地址，是为了绕开 jsDelivr 对 `@main` 最长 12 小时的缓存。
取不到时会自动回退到 jsDelivr 分支地址、raw.githubusercontent 和 gcore 镜像 —— 国内直连 GitHub raw 基本不通。

## 赞助

软件免费、源码全开，没有付费版，也没有任何功能限制。如果它帮你省下了时间，欢迎请作者喝杯咖啡——纯属自愿，不影响使用。

<table>
  <tr>
    <td align="center"><b>微信支付</b></td>
    <td align="center"><b>支付宝</b></td>
  </tr>
  <tr>
    <td><img src="docs/reward-wechat.jpg" width="230" alt="微信赞赏码"></td>
    <td><img src="docs/reward-alipay.jpg" width="230" alt="支付宝赞赏码"></td>
  </tr>
</table>

> 打赏不换取任何特权，也不影响 issue 的处理顺序。

## 致谢与许可

本程序自身代码以 [MIT](LICENSE) 许可发布。

发布包**不含** [aria2](https://aria2.github.io/)。程序发现缺少 `aria2c.exe` 时会引导你从其官方发布页获取；
aria2 以 GPLv2 许可分发，本项目未对它做任何修改，也不随包分发。
