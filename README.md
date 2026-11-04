# 极速下载器 · ComfyDL

一个 Windows 上的高速下载工具。粘链接就能下——软件、压缩包、镜像、视频都行；
如果下的是模型，它会自动认出来并放进 ComfyUI 对应的 `models` 子目录。

多源竞速 + aria2 多线程分段，单文件大文件能跑满带宽。

## 运行环境

- **仅支持 Windows 10 / 11（64 位）**
- 不需要安装 .NET 运行时，程序自带
- 首次启动会自动获取下载引擎 aria2（约 1.8 MB）

> macOS 和 Linux 目前**不支持**。代码是 C# / WPF 写的，WPF 只有 Windows 有；
> 移植到别的平台等于重写整个界面层，短期不会做。

## 两种下载方式

| 方式 | 文件 | 适合 |
|---|---|---|
| **安装版** | `极速下载器-Setup-x.y.z.exe` | 普通用户。装到 Program Files，有开始菜单和卸载项 |
| **绿色版** | `ComfyDL-x.y.z.zip` | 解压即用，不写注册表，放 U 盘也能跑 |

都到 [Releases](../../releases) 下载最新版。单文件 exe 也可以直接用，但建议至少用上面两种之一，方便以后更新。

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
- **多线程分段**：aria2 引擎，1 / 4 / 16 连接自动实测择优
- **断点续传**：中断后从断点继续，不重下
- **并发上限**：同时下几个由你定，其余排队
- **全局限速**：限制总带宽，不影响看视频
- **代理**：HTTP / SOCKS5，只对下载引擎生效
- **同名文件策略**：询问 / 覆盖 / 跳过 / 重命名
- **磁盘空间预检**：空间不够提前拦下，不会下到 99% 才失败
- **托盘常驻**：下载完成弹通知，不占任务栏
- **自动更新**：启动后静默检查新版本

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)：

```
dotnet publish ComfyDL.Desktop/ComfyDL.Desktop.csproj -c Release -r win-x64 ^
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

客户端启动 10 秒后读取 `version.json`，版本更高就在状态条显示提示，点击跳到 Release 页面。

更新源默认走 jsDelivr 镜像（`cdn.jsdelivr.net`），失败时自动回退到 raw.githubusercontent 和 gcore —— 因为国内直连 GitHub raw 基本不通。

## 赞助

软件免费、源码全开，没有付费版，也没有任何功能限制。如果它帮你省下了时间，欢迎请作者喝杯咖啡——纯属自愿，不影响使用。

**爱发电**：把下面这行换成你的主页地址

```
https://afdian.com/a/你的ID
```

**扫码**：把微信或支付宝收款码存成 `docs/reward.png`，然后删掉下面这行的注释

```html
<!-- <img src="docs/reward.png" width="220" alt="赞赏码"> -->
```

> 打赏不换取任何特权，也不影响 issue 的处理顺序。

## 致谢与许可

本程序自身代码以 [MIT](LICENSE) 许可发布。

发布包内含 [aria2](https://aria2.github.io/)，以 GPLv2 许可分发，未做任何修改。
