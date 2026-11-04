# 极速下载器 · ComfyDL

给 ComfyUI 用户用的模型下载器。粘一条链接，自动认来源、自动挑最快的镜像、自动归到正确的 `models` 子目录。

Windows 桌面端，C# / WPF / .NET 10 原生，单文件免安装。

## 它能做什么

- **多源竞速**：HuggingFace / hf-mirror / ModelScope / Civitai，同一个文件测速后选最快的
- **多线程分段**：aria2 引擎，1 / 4 / 16 连接自动实测择优，支持断点续传
- **自动归类**：按模型类型落进 `diffusion_models` / `loras` / `vae` / `text_encoders` 等目录
- **批量与队列**：并发上限、限速、代理、同名文件策略、下载前磁盘空间预检
- **模型库**：扫描本地模型，按类型与大小浏览
- **托盘常驻**：下载完成弹通知，不占任务栏

## 下载

到 [Releases](../../releases) 下载最新的 `极速下载器-x.y.z.exe`，双击即用，不需要装 .NET 运行时。

> 首次启动会自动检查并获取下载引擎 aria2（约 1.8 MB）。如果安全软件拦截，把 `aria2c.exe` 加入信任区即可。

## 从源码构建

需要 .NET 10 SDK：

```
dotnet publish ComfyDL.Desktop/ComfyDL.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 目录说明

| 路径 | 内容 |
|---|---|
| `ComfyDL.Desktop/` | 主程序（WPF） |
| `ComfyDL.Desktop/Sources/` | 下载源适配器，新增站点只需实现 `IModelSource` 并注册 |
| `version.json` | 更新检查用的版本元数据 |
| `.github/workflows/release.yml` | 打 tag 自动构建并发版 |

## 更新机制

客户端启动 10 秒后读取 `version.json`，版本更高就在状态条显示提示，点击跳到 Release 页面。

## 致谢与许可

本程序自身代码以 MIT 许可发布，见 [LICENSE](LICENSE)。

发布包内含 [aria2](https://aria2.github.io/)，以 GPLv2 许可分发，未做任何修改。
