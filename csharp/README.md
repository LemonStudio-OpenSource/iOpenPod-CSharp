# iOpenPod 管理器（C# / WinUI 3 重制版）

> **你的音乐，你的 iPod。**
>
> 这是 [iOpenPod](https://github.com/TheRealSavi/iOpenPod) 的**完全 C# 重写版**：界面、日志与所有用户可见内容均为中文，
> 使用 .NET 10 + WinUI 3 构建，交付物是**自带运行时的单目录可执行程序**，无需安装 Python 或 .NET。

---

## 1. 这是什么

一个在 Windows 上管理**文件系统可访问的 iPod** 的桌面工具。它读取和写入 iPod 自己的数据库格式，
并把你电脑上的音乐、封面同步进设备。

支持范围与原项目一致：

| 家族 | 支持世代 |
| --- | --- |
| iPod / iPod classic | 全尺寸第 1–5.5 代、classic 第 6 / 6.5 / 7 代 |
| iPod mini | 第 1、2 代 |
| iPod nano | 第 1–7 代 |

**不支持**：iPod shuffle、iPod touch、iPhone。

---

## 2. 功能

### 浏览与播放
* **歌曲 / 专辑 / 艺术家 / 流派**四种浏览视图，全部带封面缩略图、搜索与多字段排序。
* 专辑详情页：大封面、曲目列表、整张播放。
* 底部播放条：上一首 / 播放暂停 / 下一首、进度拖动、音量、队列与**歌词**面板。
* 双击动作可配置（加入队列 / 下一首 / 立即播放 / 编辑信息）。

### 编辑
* 曲目元数据编辑器：标题、艺术家、专辑艺术家、专辑、流派、作曲者、备注、分组、歌词、
  音轨号与总数、碟号、年份、BPM、评分、媒体类型、合辑标记、随机跳过、记住播放位置；支持**多选批量应用**。
* 播放列表：新建、重命名、删除、添加曲目、移出曲目。
* 设备重命名（写入主播放列表名）。
* 所有编辑在内存中的**无损数据库**上进行；可以随时放弃修改并重新读取设备。

### 同步
* 扫描一个或多个宿主媒体文件夹（递归），支持 MP3 / M4A / MP4 / AAC / WAV / AIFF / FLAC / OGG / WMA 等格式。
* 自动解析 ID3v1 / ID3v2.2-2.4、MP4、FLAC、Vorbis、WAV、AIFF、ASF 标签与内嵌封面。
* 生成**同步计划**：新增 / 更新文件 / 更新信息 / 移除，移除项默认**不勾选**。
* 执行时把媒体文件、封面分片（iTHMB）与数据库放在**同一个可恢复事务**中提交。
* 同步辅助记录（`iPod_Control/iOpenPod/library-sync-helper.json`）用于识别"已同步且未变化"的文件。

### 备份与还原
* 一键把 `iPod_Control` 与 `Photos` 完整备份到电脑，附带 JSON 清单（大小、修改时间、小文件 SHA-256）。
* 快照列表、按时间排序、可删除、可打开备份目录。
* 还原前**强制**再创建一次安全快照，并逐文件回写。

### 设备
* 自动发现已挂载且含 `iPod_Control` 的卷，结合 SysInfo / SysInfoExtended / USB 硬件标识 / 卷序列号进行型号识别。
* 内置 151 个型号、218 个序列号后缀、35 组 USB 标识的完整目录。
* 原生安全弹出（加锁卷 → 卸载 → 解除介质保持 → 弹出介质），绝不强制卸载。
* 明确的写入限制提示：只读卷、缺少 FireWire GUID、使用 SQLite 音乐库的机型。

### 诊断
* 全中文日志，同时写入内存环形缓冲（界面可实时查看、筛选、复制）与磁盘文件（自动按大小归档）。

---

## 3. 技术架构

```
csharp/
├─ src/
│  ├─ iOpenPod.Storage/         存储边界：卷发现、身份绑定的文件系统会话、可恢复写入事务、安全弹出
│  ├─ iOpenPod.DeviceRegistry/  设备边界：型号目录、SysInfo 解析、识别结果（不触碰硬件）
│  ├─ iOpenPod.IPodDb/          格式边界：无损 Chunk 树、iTunesDB / ArtworkDB / PhotosDB、签名、iTHMB 编解码
│  ├─ iOpenPod.App/             应用层：设备协调、音乐库会话、同步引擎、备份、播放、设置、日志
│  └─ iOpenPod.WinUI/           WinUI 3 图形界面（中文）
└─ tests/iOpenPod.Verification/ 离线校验程序（真实数据库往返、官方签名向量、设备识别）
```

依赖方向严格单向：`WinUI → App → IPodDb → DeviceRegistry`，`App/Storage` 不反向依赖。

### 关键设计

**无损数据块树（`ChunkNode`）**
iTunesDB 与 ArtworkDB 都表示成一棵保留原始字节的树：头部字节与数据区字节原样保存，
写入时只重算「长度」与「子块计数」两类结构字段。因此**解析后立即写回可以逐字节重现原始文件**，
未知字段、填充、保留区与尾部数据都不会丢失。校验程序用真实捕获的数据库证明了这一点。

**定义驱动的格式知识**
`DatabaseDefinitions` 是工程中唯一保存"哪个字段在哪个偏移、多宽"的地方，解析器与写入器共享它。

**签名**
* **HASH58**：AES S 盒由有限域定义现算，HMAC-SHA1 密钥由 FireWire GUID 派生。
* **HASH72**：AES-128-CBC 信封，密钥 `618CA10D…`，IV 与随机数从设备 `HashInfo` 或已签名数据库中恢复。
* **HASHAB**：从 [dstaley/hashab](https://github.com/dstaley/hashab)（Unlicense，公有领域）的 C 实现完整移植，
  包含 EDON-R′ 256、Apple 的密码表与 expand/reduce 变换；通过全部 100 组官方向量。

**可恢复写入事务**
先暂存全部新内容并计算 SHA-256，再写日志、发布写入、执行删除、校验、清理。
每个被替换或删除的原文件先移动到备份区；中断后可按日志恢复。

---

## 4. 构建

需要 **.NET 10 SDK**（`dotnet --version` ≥ 10.0.100）与 Windows 10 1809 以上。

```powershell
cd csharp
dotnet build iOpenPod.sln -c Release -p:Platform=x64
```

生成自带运行时的可执行程序：

```powershell
pwsh ./build/publish.ps1
# 或
dotnet publish src/iOpenPod.WinUI -c Release -r win-x64 --self-contained true
```

产物在 `src/iOpenPod.WinUI/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`，
入口是 `iOpenPod.exe`。**整个 publish 目录都要保留**，直接运行 `iOpenPod.exe` 即可，无需安装 .NET。

> ### ⚠️ 首次使用前必须做一件事：安装 Windows App Runtime
>
> WinUI 3 应用需要**操作系统级**的 **Windows App Runtime 1.8** 组件才能初始化图形合成器。
> 即使应用是自包含发布的（.NET 与 WinAppSDK 都随包携带），这些系统组件仍然必须存在，
> 否则**窗口会出现但内容一片空白**，而且在日志里只留下"主窗口已就绪"，看不出原因。
>
> 发行包中已经附带微软官方的四个 MSIX 组件与一键安装脚本：
>
> 1. 双击 **`首次运行-安装运行时.cmd`**（只需一次，不需要管理员权限）；
> 2. 然后运行 `iOpenPod.exe`。
>
> 如果脚本失败，也可以手动依次双击 `Runtime\` 目录下的四个 `.msix` 文件。

> 说明：非打包的 WinUI 3 应用在 `dotnet publish` 时不会自动复制应用自身的 `.pri` 资源索引，
> 会导致启动时找不到 XAML 资源。项目中的 `CopyAppPriToPublish` 目标已显式修复这一点。

### 运行离线校验

```powershell
dotnet run --project tests/iOpenPod.Verification -c Release
```

校验内容：真实捕获的 iTunesDB / ArtworkDB / PhotosDB 逐字节往返、HASH58 官方向量、
HASHAB 全部 100 组官方向量、设备目录与 SysInfo 识别、编辑后逐字节复原、播放列表增删往返。

---

## 5. 已知限制

* **iPod nano 第 5 / 6 / 7 代**使用压缩的 iTunesCDB **加** SQLite 音乐库（`iTunes Library.itlp`）。
  程序可以读取并浏览这些机型（解包 iTunesCDB，支持 HASH72 / HASHAB 签名计算），
  但*写入*尚未实现，因此这几代机型当前是**只读**的，界面会明确提示原因。
  其余机型（iPod 1–5.5 代、classic、mini、nano 1–4）可以完整读写。
* 同步默认**保持原格式**，不做转码；设置中的转码选项是为后续版本预留的开关。
* 尚未实现：智能播放列表规则编辑、照片库管理、播客订阅、Last.fm / ListenBrainz 记录、
  macOS / Linux 版本、应用内自动更新。

---

## 6. 授权

GPL-3.0-or-later，与原项目一致。

HASHAB 的实现移植自 [dstaley/hashab](https://github.com/dstaley/hashab)（Unlicense，公有领域），特此致谢。

iPod 与 iTunes 是 Apple Inc. 的商标；本软件是独立作品，与 Apple 无任何关联，也未获得其背书。
