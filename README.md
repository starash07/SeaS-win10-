# SeaS

SeaS 是一款完全本地运行的 Windows 桌面书架与阅读器，当前版本为 `V1.1.0`。项目使用 C#、WPF 和 .NET 8 开发，不提供账号、云同步、云备份或阅读统计。

> 版本状态：`V1.1.0` 已完成当前源码的正式保护与安装包封装，`dist\setup` 只保留当前版本安装包。

本次更新内容与覆盖安装说明见 [UPDATE_README.md](UPDATE_README.md)。

## 当前功能

### 本地书架

- 导入 TXT、EPUB、Markdown 文件。
- 支持选择文件、扫描文件夹和拖入主界面导入。
- 全部书籍、我的分组、我的收藏和最近阅读视图。
- 书架搜索、正序/倒序和多种排序方式。
- 批量移除、收藏和加入分组。
- 自定义分组的新建、重命名、删除和拖动排序。
- 在分组视图中拖动书籍卡片完成分组。
- 自定义书名、作者和裁剪后的封面。
- 显示文件大小、上次打开日期和失效状态。
- 文件移动后支持手动重新定位，或批量清理失效书籍记录。
- 从书架移除不会删除用户的原始书籍文件。

### 普通阅读模式

- TXT、EPUB、Markdown 使用统一的阅读界面。
- 章节识别、目录跳转、书内搜索和搜索结果列表。
- 阅读进度、章节位置、书签及自定义书签名称。
- 滚动阅读、横向翻页和自动翻页。
- 字号、行距、字体、主题、自定义颜色和页边距。
- 正文区域足够宽时自动切换双列，随窗口和页边距平滑重排。
- 专注模式、F10 快捷开关和左侧悬停导航栏。
- 记忆阅读设置、阅读模式、窗口尺寸、位置和最大化状态。

### 摸鱼模式与 LittleFish

- SeaS 内置 LittleFish `1.5.2`（2026-09-15 修复构建），仅用于 TXT 摸鱼阅读；运行组件包含新版文本编码识别依赖 `UTF.Unknown 2.7.0`。
- 始终从 `src/SeaS.App/Plugins/LittleFish` 随附的固定版本启动。
- 不调用用户系统中单独安装的 LittleFish。
- SeaS 托盘统一接管内置 LittleFish 的隐藏、恢复和退出流程。
- EPUB、Markdown 在摸鱼模式下会提示改用普通阅读模式或取消打开。

### 桌面行为

- 单实例运行。
- 关闭主窗口默认隐藏到 SeaS 托盘。
- 托盘菜单支持打开主界面、导入书籍、扫描文件夹、切换模式和退出 SeaS。
- Release 构建不写异常日志；调试日志仅供开发构建使用。

## 本地数据

SeaS 不复制或修改用户导入的原始书籍文件。书架与阅读设置保存在：

```text
%LOCALAPPDATA%\SeaS\library.json
%LOCALAPPDATA%\SeaS\Covers\
```

正式安装前，SeaS 托管模式下的 LittleFish 设置将统一迁移到：

```text
%LOCALAPPDATA%\SeaS\LittleFish\settings.json
```

## 项目结构

```text
SeaS\
├─ SeaS.sln
├─ Directory.Build.props
├─ README.md
├─ UPDATE_README.md
├─ docs\
│  ├─ SeaS-开发记录.md
│  └─ SeaS-安装与LittleFish集成方案.md
├─ scripts\
│  ├─ check-environment.ps1
│  ├─ build-test.ps1
│  └─ build-installer.ps1
└─ src\
   ├─ SeaS.App\
   │  ├─ Assets\
   │  ├─ Models\
   │  ├─ Services\
   │  └─ Plugins\LittleFish\
   ├─ SeaS.Installer\
   └─ SeaS.Uninstaller\
```

- `src/SeaS.App`：SeaS 主程序。
- `src/SeaS.App/EmbeddedReaderControl.*`：统一阅读器界面与逻辑。
- `src/SeaS.App/Plugins/LittleFish`：SeaS 自己随附的内置 LittleFish 运行文件。
- `src/SeaS.Installer`：自定义安装界面和安装逻辑。
- `src/SeaS.Uninstaller`：不携带 payload 的独立轻量卸载器。
- `E:\txt\LittleFish`：独立 LittleFish 项目，不是 SeaS 的构建依赖，SeaS 脚本不得读写它。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- Git
- Obfuscar.GlobalTool（仅正式打包需要）
- Visual Studio 2022、Rider 或 VS Code（可选）

仓库通过 `global.json` 固定 .NET SDK `8.0.422`，允许选择同一功能带的补丁版本。

检查环境：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-environment.ps1
```

还原和构建：

```powershell
dotnet restore .\SeaS.sln
dotnet build .\SeaS.sln -c Debug --no-restore
```

运行主程序：

```powershell
dotnet run --project .\src\SeaS.App\SeaS.App.csproj
```

生成统一测试版本并更新项目根目录中的测试快捷方式：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-test.ps1
```

测试版本固定输出到：

```text
artifacts\dev
```

## 正式打包

正式打包入口是 `scripts/build-installer.ps1`。脚本只使用 `E:\txt\SeaS` 内的源码、内置组件和资源，不读取、构建或修改 `E:\txt\LittleFish` 独立项目。

首次打包前安装 Obfuscar：

```powershell
dotnet tool install -g Obfuscar.GlobalTool
```

发布 SeaS V1.1.0 时运行以下命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.1.0
```

可选参数：

- `-Version 1.1.0`：设置主程序、安装器和卸载器版本，格式必须为三段版本号；不传参数时默认使用当前版本。
- `-KeepIntermediate`：保留 `dist\installer` 中的发布、保护和封包中间文件，便于排查问题。
- `-SkipObfuscation`：跳过 Obfuscar，仅用于定位保护兼容性问题，不能用于正式发布。
- `-SkipSmokeTest`：跳过受保护程序冒烟测试，仅用于调试打包脚本，不能用于正式发布。

### 打包流水线

脚本严格按照以下顺序执行，代码保护发生在安装包封装之前：

1. 只清理 SeaS 工作区内的 `dist\installer` 和临时嵌入资源；`dist\setup` 中已发布的旧版本安装包继续保留，本次版本同名文件会被替换。
2. 以 `Release / win-x64 / self-contained` 发布 `SeaS.App`，不生成 PDB，不依赖用户预装 .NET 8 Desktop Runtime。
3. 单独发布 `SeaS.Uninstaller`，启用单文件和裁剪，生成不携带 payload 的轻量卸载器。
4. 将主程序发布结果复制到 `dist\installer\protected`，并把轻量卸载器加入其中。
5. 使用 Obfuscar 处理 `SeaS.dll`，启用私有成员重命名、字符串保护、名称复用和 `SuppressIldasm`。
6. 用保护后的 `SeaS.dll` 替换原始 DLL。WPF 界面类型保留名称，避免破坏 XAML、事件绑定和数据绑定。
7. 分别运行保护后的 `SeaS.exe` 和 `SeaS_Uninstall.exe` 冒烟测试；任一程序加载失败、异常退出或超时都会停止打包。
8. 检查 payload 不包含 PDB，再将整个 protected 目录压缩为 `payload.zip`。
9. 计算 payload 的 SHA-256，并把 ZIP 与哈希作为资源嵌入 `SeaS.Installer`。
10. 将安装器发布为自包含、单文件、内部压缩的 `win-x64` 程序。
11. 校验安装器 `ProductVersion`，输出安装包。
12. 正常完成后删除 payload 和 `dist\installer` 中间目录；使用 `-KeepIntermediate` 时除外。

中间目录关系：

```text
SeaS.App Release publish
        ↓
dist\installer\publish
        ↓ 复制并加入轻量卸载器
dist\installer\protected
        ↓ Obfuscar 保护 SeaS.dll
dist\installer\obfuscated
        ↓ 替换、冒烟测试、ZIP 与 SHA-256
SeaS.Installer\Assets\payload.zip
        ↓ 嵌入并发布单文件安装器
dist\setup\SeaS_Setup_v{version}.exe
```

正式交付文件输出到：

```text
dist\setup\
└─ SeaS_Setup_v{version}.exe
```

### 代码保护边界

- Obfuscar 只处理 SeaS 自有的 `SeaS.dll`。
- `src\SeaS.App\Plugins\LittleFish` 中的内置 LittleFish 使用项目内已经确认的版本，不进行二次混淆。
- 打包脚本不会查找或调用外部 LittleFish 安装目录，因此不会与用户单独安装的 LittleFish 发生依赖或覆盖。
- 安装器和卸载器分别发布，安装目录中不会复制一份完整安装包充当卸载器。
- 当前属于基础混淆和字符串保护，不等同于无法逆向的绝对加密。

### 安装逻辑

当前安装器支持：

- “仅为我安装”默认写入 `%LOCALAPPDATA%\Programs\SeaS`，使用当前用户快捷方式和 HKCU 卸载信息。
- “为使用这台电脑的所有用户安装（管理员）”在用户点击开始安装后通过 Windows `runas` 请求 UAC；授权后使用 Program Files、公共快捷方式和 HKLM 卸载信息。
- 用户选择父目录后自动创建或复用 `SeaS` 子目录。
- 全新安装、版本更新、同版本修复和受控降级。
- 按完整路径关闭 SeaS 及其内置 LittleFish，不影响独立 LittleFish。
- 安装前将 payload 解压到临时暂存目录，检查 SHA-256、ZIP 路径和必要文件，再写入正式目录。
- 更新前备份现有安装，失败时回滚；使用 `.seas-manifest.txt` 清理新版不再分发的旧文件。
- 打开安装包时立即检查 HKCU 与 HKLM；检测到已有记录就显示“准备更新”，自动填入并锁定原安装范围和目录，点击“开始更新”后直接在原位置覆盖。全电脑安装在开始更新时请求 UAC；全新安装才允许选择范围和目录。
- 更新开始前重新校验安装记录，旧版的自定义目录按原路径使用，不再追加 `SeaS` 子目录；同目录的跨范围重复记录会在安装成功后合并。
- 安装成功后写入快捷方式、安装范围对应的卸载注册项和轻量卸载器。
- 卸载器从临时目录运行，按完整路径关闭 SeaS 和内置 LittleFish，再依据安装清单删除程序文件；程序已被另一记录删除时会安全清理残留卸载项。
- 卸载默认保留本地书架与阅读设置；用户可以选择删除当前用户的 `%LOCALAPPDATA%\SeaS`，但不会删除导入的原始书籍文件。

### 发布验证

打包脚本自动验证受保护主程序和卸载器能够加载。生成正式包后还应执行：

1. 当前用户范围的全新安装、启动和卸载。
2. 实际 UAC 环境下的所有用户安装和卸载。
3. 相同版本修复、旧版本更新和高版本降级提示。
4. 先后选择当前用户和所有用户、但使用同一目录时，系统中始终只保留一个 SeaS 卸载项。
5. TXT、EPUB、Markdown、专注模式、托盘接管和内置 LittleFish 的发布版人工回归。

当前本地构建没有商业代码签名证书，Windows 可能显示“未知发布者”。这不影响安装包哈希校验和本地安装，但公开分发前建议补充 Authenticode 签名。

详细方案见 `docs/SeaS-安装与LittleFish集成方案.md`。

## V1.1.0 发布状态与后续事项

- 根据 V1.0.0 安装后的实际使用反馈继续修复问题并优化体验。
- `V1.1.0` 安装包已完成 Obfuscar 代码保护、受保护主程序/卸载器冒烟测试、安装器自检和 SHA-256 校验，并包含 2026-09-15 更新的内置 LittleFish 修复构建。

- 将 SeaS 托管模式下的 LittleFish 设置迁移到 `%LOCALAPPDATA%\SeaS\LittleFish`。
- 在实际 UAC 环境中人工验收“为所有用户安装”。
- 完成 TXT、EPUB、Markdown、专注模式和托盘接管的发布版人工回归。
- 公开分发前决定是否购买代码签名证书。

暂不计划支持 PDF、云同步、开机自启、系统级右键打开或文件关联。
