# Memo

一个基于 .NET 8 和 Avalonia UI 的 Windows 桌面备忘录应用。应用采用无边框圆角窗口设计，支持系统托盘、全局快捷键、窗口置顶、本地 JSON 持久化，以及长按拖拽重排备忘录。

## 功能特性

- 备忘录新增、编辑、删除和排序
- Enter 保存，Shift + Enter 换行
- 双击备忘录进入编辑，保存后自动移到列表顶部
- 长按备忘录卡片后拖拽重排
- 自定义无边框窗口，支持拖动、最小化、关闭和置顶
- 系统托盘支持打开窗口、新建备忘录、切换置顶和退出应用
- 可配置关闭按钮行为：最小化到托盘或直接关闭
- 可配置全局快捷键：置顶、最小化、显示窗口
- 备忘录和设置保存到本机 JSON 文件

## 技术栈

- .NET 8
- Avalonia UI 11.1
- C#
- Windows Forms NotifyIcon / RegisterHotKey，用于系统托盘和全局快捷键
- System.Text.Json，用于本地数据持久化

## 运行环境

当前项目目标框架为 `net8.0-windows`，需要 Windows 和 .NET 8 SDK。

```bash
dotnet --version
```

如果没有安装 .NET 8 SDK，可以从 Microsoft 官网安装后再运行项目。

## 快速开始

在仓库根目录执行：

```bash
cd Memo-avalonia
dotnet restore
dotnet run
```

也可以直接指定项目文件运行：

```bash
dotnet run --project Memo-avalonia/Memo.csproj
```

## 构建

```bash
dotnet build Memo-avalonia/Memo.csproj
```

发布 Release 版本示例：

```bash
dotnet publish Memo-avalonia/Memo.csproj -c Release -r win-x64 --self-contained false
```

发布产物默认位于：

```text
Memo-avalonia/bin/Release/net8.0-windows/win-x64/publish/
```

## 数据存储

应用会在用户 AppData 目录下创建 `Memo` 文件夹：

```text
%AppData%/Memo/
```

主要文件：

- `memos.json`：备忘录列表
- `settings.json`：关闭行为和快捷键设置

删除这些文件后，应用会在下次启动时使用空数据和默认设置重新创建。

## 默认快捷键

| 功能 | 默认快捷键 |
| --- | --- |
| 切换置顶 | Ctrl + Alt + T |
| 最小化到托盘 | Ctrl + Alt + M |
| 显示窗口 | Ctrl + Alt + N |

快捷键可以在应用的设置窗口中修改。按 Esc 可以清空某个快捷键。

## 项目结构

```text
Memo-avalonia/
├── Memo.csproj                       # Avalonia 桌面应用项目文件
├── App.axaml / App.axaml.cs          # 应用入口、托盘、设置加载和全局快捷键初始化
├── Program.cs                        # Avalonia 启动配置
├── Views/                            # 主窗口、设置窗口和托盘菜单窗口
├── Components/Dialogs/               # 关闭行为选择、确认等弹窗组件
├── ViewModels/                       # 主窗口状态和备忘录集合管理
├── Models/                           # 备忘录、设置和快捷键模型
├── Services/                         # JSON 本地存储服务
├── Platform/Windows/                 # Windows 托盘和全局快捷键实现
├── Behaviors/                        # 长按拖拽重排等交互行为
├── UI/                               # 通用 UI 动画/辅助控制器
└── Assets/                           # 应用图标资源
```

## 使用说明

1. 在顶部输入框输入内容。
2. 按 Enter 保存为一条备忘录。
3. 按 Shift + Enter 在输入框中换行。
4. 双击已有备忘录可以编辑。
5. 点击卡片右侧删除按钮可以删除。
6. 长按卡片并拖动可以调整顺序。
7. 点击标题栏图钉按钮可以切换窗口置顶。
8. 右键系统托盘图标可以打开托盘菜单，左键双击托盘图标可以显示主窗口。

## 开发说明

- 备忘录集合由 `MainViewModel` 管理，保存逻辑在 `JsonMemoStorage` 中。
- 应用设置由 `JsonSettingsStorage` 保存，启动后异步加载并应用到主窗口和全局快捷键服务。
- 长按拖拽排序封装在 `DragReorderManager`，避免主窗口代码承担过多列表交互细节。
- 由于使用 Windows 托盘和全局快捷键能力，当前项目按 Windows 桌面应用配置。若要支持其他平台，需要替换托盘、快捷键和目标框架相关实现。
