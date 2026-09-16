# SeaS V1.0.0 安装与内置 LittleFish 方案

本文档固定 SeaS `V1.0.0` 的安装、更新、卸载和内置 LittleFish 边界。当前自定义安装器、轻量卸载器和正式打包脚本已经接入。

> 进度说明（2026-09-16）：本文保留 `V1.0.0` 已实现、已测试的安装基线。当前 `V1.1.0` 沿用本方案，重新封包并更新内置 LittleFish 至 `1.6.0` 修复构建。

## 1. 项目边界

- SeaS 安装所需的全部源码、资源和内置组件必须位于 `E:\txt\SeaS`。
- 内置 LittleFish 固定存放在 `src/SeaS.App/Plugins/LittleFish`。
- `E:\txt\LittleFish` 是独立 LittleFish 项目，不是 SeaS 的构建依赖。
- SeaS 的构建、测试和安装脚本不得读取、修改、构建或清理 `E:\txt\LittleFish`。
- SeaS 安装器不会运行或嵌套独立 LittleFish 安装器。

当前内置 LittleFish 版本为 `1.6.0`（2026-09-16 修复构建，源提交 `ad979e3`），并包含该构建所需的 `UTF.Unknown 2.7.0` 编码识别依赖；已纳入同日重新生成的 SeaS `V1.1.0` 安装包。SeaS 只通过以下相对路径启动它：

```text
<SeaS 安装目录>\Plugins\LittleFish\LittleFish.exe
```

## 2. 安装范围选择

安装前由用户选择：

### 仅为我安装

默认目录：

```text
%LOCALAPPDATA%\Programs\SeaS
```

特点：

- 默认不需要管理员权限；
- 快捷方式只为当前用户创建；
- 卸载信息写入 HKCU；
- 只影响当前 Windows 用户。

### 为使用这台电脑的所有用户安装

默认目录：

```text
%ProgramFiles%\SeaS
```

特点：

- 开始安装时请求管理员权限；
- 使用公共桌面和公共开始菜单；
- 卸载信息写入 HKLM；
- 程序文件供所有用户使用，各用户的书架和阅读设置仍分别保存在自己的 `%LOCALAPPDATA%\SeaS`。

安装器应先让用户完成范围、目录和快捷方式选择，仅在用户确认“所有用户安装”后请求 UAC，避免打开安装器立即弹出权限提示。

## 3. 安装目录规范化

用户在界面中选择的是父目录，安装器必须保证程序最终位于独立的 `SeaS` 文件夹中。

| 用户选择 | 最终目录 |
|---|---|
| `D:\Software` | `D:\Software\SeaS` |
| `D:\Software\SeaS` | `D:\Software\SeaS` |
| `D:\` | `D:\SeaS` |
| `C:\Program Files` | `C:\Program Files\SeaS` |

规则：

1. 去除首尾空白和多余引号。
2. 转换为完整绝对路径。
3. 最后一层已经为 `SeaS` 时不重复追加。
4. 选择磁盘根目录时在根目录下创建 `SeaS`。
5. 界面始终显示规范化后的最终目录。
6. V1.0.0 只支持本地磁盘目录，不支持网络或 UNC 路径。
7. 目标目录非空且无法识别为 SeaS 安装时停止安装，避免覆盖用户其他文件。

## 4. 安装目录结构

```text
<安装目录>\
├─ SeaS.exe
├─ SeaS.dll
├─ SeaS.deps.json
├─ SeaS.runtimeconfig.json
├─ 其他 SeaS 运行文件
├─ Plugins\
│  └─ LittleFish\
│     ├─ LittleFish.exe
│     ├─ LittleFish.dll
│     ├─ LittleFish.deps.json
│     ├─ LittleFish.runtimeconfig.json
│     ├─ System.Text.Encoding.CodePages.dll
│     └─ runtimes\...
├─ SeaS_Uninstall.exe
└─ .seas-manifest.txt
```

内置 LittleFish：

- 不创建独立快捷方式；
- 不创建独立开始菜单项；
- 不注册独立卸载项；
- 不创建自己的托盘图标；
- 不使用独立 LittleFish 的设置或安装位置；
- 只作为 SeaS 托管的内部运行组件。

## 5. 用户数据

所有用户数据必须与程序目录分离：

```text
%LOCALAPPDATA%\SeaS\
├─ library.json
├─ Covers\
└─ LittleFish\
   └─ settings.json
```

- SeaS 不复制、移动或删除用户导入的原始 TXT、EPUB、Markdown 文件。
- 更新程序不得删除 `%LOCALAPPDATA%\SeaS`。
- SeaS 托管模式下的 LittleFish 必须使用 `%LOCALAPPDATA%\SeaS\LittleFish\settings.json`。
- 独立 LittleFish 的设置、书签、窗口和阅读位置与 SeaS 内置版本完全隔离。

## 6. 全新安装流程

1. 用户选择安装范围。
2. 用户选择安装父目录和快捷方式选项。
3. 安装器规范化为最终 `SeaS` 目录。
4. 校验目录权限、剩余空间、目录内容和安装架构。
5. 将 payload 解压到随机临时暂存目录。
6. 校验 payload 哈希、路径和必需文件。
7. 检查目标目录中的 SeaS 和内置 LittleFish 是否正在运行。
8. 需要时请求正常退出；无法退出时由用户确认是否强制关闭。
9. 从暂存目录写入正式安装目录。
10. 写入 `.seas-manifest.txt`。
11. 按用户选择创建快捷方式。
12. 写入对应安装范围的卸载注册信息。
13. 安装成功后按用户选择启动 SeaS。

必须先完成暂存和校验，再修改正式安装目录，避免 payload 不完整时留下半安装状态。

## 7. 进程识别

安装、更新和卸载只能处理当前 SeaS 安装目录中的：

```text
<安装目录>\SeaS.exe
<安装目录>\Plugins\LittleFish\LittleFish.exe
```

禁止只使用以下方式批量结束进程：

```csharp
Process.GetProcessesByName("LittleFish")
```

标准顺序：

1. 请求 SeaS 正常退出；
2. SeaS 通过既有托管通道关闭内置 LittleFish；
3. 等待正常退出；
4. 仍被占用时显示明确提示；
5. 用户确认后，只按完整可执行文件路径强制关闭目标进程。

不得影响用户单独安装或便携运行的 LittleFish。

## 8. 更新、修复与降级

安装器根据注册表中的版本和安装位置区分：

- 未安装：全新安装；
- 已安装更低版本：更新；
- 已安装相同版本：修复安装；
- 已安装更高版本：降级提示，默认阻止，用户明确确认后才继续。

更新和修复时：

- 打开安装包时同时读取 HKCU 与 HKLM，按产品身份识别已有安装，并立即显示“准备更新”；
- 自动填入并锁定原安装范围和安装目录，点击“开始更新”后再次校验记录并原地覆盖；
- 原目录按完整路径复用，不追加软件名子目录；原先为全电脑安装的更新继续请求管理员权限；
- 保留所有本地用户数据；
- 使用旧 `.seas-manifest.txt` 识别程序文件；
- 清理新版不再分发的旧文件；
- 快捷方式状态与用户选择同步，取消勾选时删除已有快捷方式；
- 更新失败时恢复旧程序文件。

移动安装目录通过卸载后重新安装完成，不在更新界面中直接迁移。

## 9. 快捷方式与卸载注册

可选快捷方式：

- 桌面 `SeaS.lnk`；
- 开始菜单 `SeaS`；
- 开始菜单 `卸载 SeaS`。

“仅为我安装”使用当前用户桌面、开始菜单和：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\SeaS
```

“所有用户安装”使用公共桌面、公共开始菜单和：

```text
HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\SeaS
```

安装器不允许同一 SeaS 以两个安装范围重复注册：

- 发现一条已有记录时，沿用它的范围和目录；
- HKCU 与 HKLM 同时指向同一目录时，安装完成后只保留实际安装范围对应的一条记录；
- 两条有效记录指向不同目录时停止安装，要求先清理旧版本，避免误覆盖。

注册信息至少包含：

- `DisplayName=SeaS`
- `DisplayVersion=1.0.0`
- `Publisher`
- `InstallLocation`
- `DisplayIcon`
- `UninstallString`
- 安装范围与预计占用空间

## 10. 失败回滚

更新前将即将覆盖的旧程序文件备份到临时目录。发生以下问题时停止安装并恢复旧版本：

- 文件被占用；
- 权限不足；
- payload 缺失或哈希不符；
- 磁盘空间不足；
- 快捷方式或注册表写入失败；
- 内置 LittleFish 文件不完整。

回滚后清理本次暂存文件，不留下部分更新状态。

## 11. 卸载

SeaS 使用独立的轻量卸载器，不复制完整安装包充当卸载器。

卸载流程：

1. 轻量卸载器复制到临时目录并重新启动。
2. 按完整路径关闭 SeaS 和它的内置 LittleFish。
3. 删除对应安装范围的桌面和开始菜单快捷方式。
4. 删除对应的 HKCU 或 HKLM 卸载信息。
5. 根据 `.seas-manifest.txt` 删除程序文件。
6. 删除空的 `SeaS` 安装目录。
7. 清理临时卸载器。

如果另一条重复卸载记录已经先删除了共享程序文件和 `.seas-manifest.txt`，卸载器会把该目录识别为残留状态，只删除本条卸载注册、对应快捷方式和 `SeaS_Uninstall.exe`。如果 `SeaS.exe` 或 `SeaS.dll` 仍存在但清单缺失，则继续停止卸载，避免误删未知文件。

卸载界面提供：

```text
同时删除书架、阅读进度和本地设置
```

该选项默认不勾选。即使勾选，也只能删除 `%LOCALAPPDATA%\SeaS`，不得删除用户的原始书籍文件或独立 LittleFish 数据。

## 12. 打包与体积控制

- Release payload 不包含 PDB、调试日志、测试文件和开发文档。
- 内置 LittleFish 不包含它自己的安装器。
- 安装器和轻量卸载器分开生成。
- 不把完整安装包复制到安装目录。
- 正式打包前确定 .NET 8 Desktop Runtime 检测或自包含策略。
- 对 payload 进行 SHA-256 校验和 ZIP 路径越界保护。
- 条件允许时为安装器、SeaS 和卸载器添加代码签名。

当前实现使用自包含 `win-x64` 发布，不要求用户预装 .NET 8 Desktop Runtime。打包前使用 Obfuscar 对 `SeaS.dll` 进行基础混淆和字符串保护，再运行受保护程序冒烟测试。WPF 界面类型为了保持 XAML 和绑定稳定而跳过重命名。安装器启用单文件内部压缩，V1.0.0 安装包约 145 MB。

当前构建没有 Authenticode 代码签名证书，因此系统可能显示“未知发布者”；公开分发前应补充签名。

## 13. V1.0.0 实现与验收状态

1. 已实现安装范围选择和目录规范化。
2. 已保持 SeaS 与独立 LittleFish 的程序路径和进程识别隔离。
3. 已实现 payload 哈希、ZIP 路径校验、文件清单、版本判断和失败回滚。
4. 已实现桌面/开始菜单快捷方式与 HKCU/HKLM 卸载注册逻辑。
5. 已实现不携带 payload 的轻量卸载器和可选用户数据删除。
6. 已采用自包含运行时并启用安装器单文件压缩。
7. 已完成当前用户范围的全新安装、受保护程序启动和卸载自动回归。
8. 待人工验收 UAC 下的所有用户安装，以及公开分发前的代码签名。
