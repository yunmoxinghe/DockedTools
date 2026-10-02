# 脚本工具

本目录包含项目开发和维护过程中使用的独立脚本工具。这些脚本使用 .NET 10 的 **File-based apps** 特性，可以直接运行单个 `.cs` 文件，无需创建项目文件。

## 📋 可用工具

### 1. 图标调整工具 (`图标调整工具.cs`)

**功能**：批量调整 SVG 图标文件的尺寸

**用途**：将 `Assets/logos` 目录中的所有 SVG 图标统一调整为指定尺寸（默认 100×100 像素）

**运行方式**：

```bash
# 方式1：使用 --file 选项（推荐）
dotnet run --file 脚本工具\图标调整工具.cs

# 方式2：直接指定文件
dotnet run 脚本工具\图标调整工具.cs

# 方式3：简写形式
dotnet 脚本工具\图标调整工具.cs
```

**工作原理**：
- 扫描指定目录中的所有 `.svg` 文件
- 使用正则表达式修改 SVG 文件的 `width` 和 `height` 属性
- 如果 SVG 没有这些属性，会自动添加
- 保留原始的 `viewBox` 属性以保持图标比例

**处理结果**：
```
✅ 已处理: adsterra.svg
✅ 已处理: Alipay.svg
✅ 已处理: monetag.svg
✅ 已处理: WeChat.svg

批量调整完成！共处理 4 个文件。
```

---

### 2. worktree 包身份工具 (`worktree.ps1`)

**功能**：新建 git worktree 并自动获得独立的 MSIX 包身份，让多个 worktree 的 Debug 包**既能并存安装、也能同时运行**。附带体检模式，一眼看出当前 worktree 的身份对不对。

**为什么需要它**：包的 `Identity Name` 只决定"能不能并列安装"，真正决定"能不能同时跑"的是运行时单实例标识（Mutex / 事件 / 命名管道）。两者必须同源，否则第二个实例会去激活第一个然后自己退出——**装得上但跑不起来**。

本仓库从 worktree 目录名（`main-ffb0958c`）取 hash 前 4 位派生后缀 `.WTFFB0`，由 `DockedTools.csproj` 在构建时同时写进这两处。**前提是目录名带 hash**——`git worktree add ../my-fix` 这种名字不会触发隔离，本脚本就是为了保证这一点。

> 我们的 worktree 一律由 WorkBuddy 生成，目录名形如 `main-ffb0958c`，自带 8 位 hex，
> 所以后缀派生总是成立 —— **日常用不到 `-New`**，进新 worktree 跑一次 `-Status` 确认
> 身份、再 `-Build -Run` 就行。`-New` 只在手动建 worktree 时才需要。

**运行方式**：

```powershell
.\脚本工具\worktree.ps1 -Status                 # 体检（默认，只读）
.\脚本工具\worktree.ps1 -Build -Run             # 构建并启动
.\脚本工具\worktree.ps1 -ListPackages           # 列已装的包，标出归属 worktree / 孤儿包
.\脚本工具\worktree.ps1 -New feat/xxx           # 手动建 worktree（目录名自动带 hash）
.\脚本工具\worktree.ps1 -Build -Run             # 在当前 worktree 构建并启动
.\脚本工具\worktree.ps1 -ListPackages           # 列出机器上已装的 DockedTools 包
.\脚本工具\worktree.ps1 -Unregister <PackageFullName>   # 注销指定包
```

所有会改动系统的动作都可以加 `-DryRun` 预演（只打印命令，git / dotnet / winapp 全不动）。

**体检输出示例**：

```
=== worktree 身份体检 ===
  OK   DockedTools.csproj 已含隔离逻辑（_WriteWorktreeIdentity）
  OK   目录名可派生后缀 : main-ffb0958c  ->  .WTFFB0

  Identity Name   : 8B8CC4F4.482486777ECD9.Debug.WTFFB0
  单实例 Mutex    : Local\DockedAI_SingleInstance_Mutex_DEBUG.WTFFB0
  单实例管道      : DockedAI_SingleInstance_Pipe.WTFFB0
  OK   身份长度 35（限制 3~50）
  OK   Publisher 保持原样: CN=A19C62A9-...
  OK   桥接端口 17829~17839 全空
```

**目录名不带 hash 时**会给出两条补救路径：`git worktree move` 改名，或每次带 `-Suffix WTXXXX`（走 `-p:WorktreeSuffix=` 传给 MSBuild，包身份与单实例标识一起变，无需改代码）。亦可设环境变量 `WORKTREE_SUFFIX`。

**体检还会抓两类隐藏问题**：

- **后缀撞车**：后缀只取 hash 前 4 位（16^4 = 65536 种）。若已有一个同名包指向别的目录，说明另一个 worktree 抢到了同一后缀 → 报 FAIL 并提示换 `-Suffix`。
- **孤儿包**：worktree 删掉后包仍留在系统里，指向一个不存在的目录。`-ListPackages` 会标出来并给出可复制的 `-Unregister` 命令。

**约束**：只在 Debug 下生效，Release 的后缀恒为空串，行为与加隔离前完全一致。只改 `Identity Name`，不动 `Publisher`——Publisher 必须与签名证书 Subject 一致，改了就得换证书。

---

## 🚀 关于 .NET 10 File-based Apps

### 什么是 File-based Apps？

File-based apps 是 .NET 10 Preview 4 引入的新特性，允许开发者直接运行单个 C# 文件，无需创建传统的 `.csproj` 项目文件。

### 核心优势

✅ **零配置运行** - 无需创建项目文件  
✅ **轻量级脚本** - 适合工具、脚本和小型应用  
✅ **包管理** - 通过 `#:package` 指令添加 NuGet 包  
✅ **Native AOT** - 默认支持原生编译，启动快速  

### 使用方法

#### 基本运行

```bash
# 运行单个 C# 文件
dotnet run --file script.cs

# 传递参数
dotnet run --file script.cs -- arg1 arg2

# 简写形式
dotnet script.cs
```

#### 添加 NuGet 包

在 `.cs` 文件顶部添加包引用：

```csharp
#:package Newtonsoft.Json@13.0.1
#:package Spectre.Console@*

using Newtonsoft.Json;
using Spectre.Console;

// 你的代码...
```

#### 设置属性

```csharp
#:property TargetFramework=net10.0
#:property PublishAot=false
```

#### 引用其他文件

```csharp
#:include helpers.cs
#:include models/**/*.cs
```

#### 指定 SDK

```csharp
#:sdk Microsoft.NET.Sdk.Web
```

### 其他命令

```bash
# 编译
dotnet build script.cs

# 发布为可执行文件
dotnet publish script.cs

# 打包为 .NET 工具
dotnet pack script.cs

# 转换为传统项目
dotnet project convert script.cs

# 清理构建输出
dotnet clean script.cs
```

### Shell 直接执行（Unix/Linux）

在文件顶部添加 shebang：

```csharp
#!/usr/bin/env -S dotnet --
#:package Spectre.Console

using Spectre.Console;
AnsiConsole.MarkupLine("[green]Hello![/]");
```

赋予执行权限并运行：

```bash
chmod +x script.cs
./script.cs
```

### 注意事项

⚠️ **项目文件优先级** - 如果当前目录包含 `.csproj` 文件，需要使用 `--file` 选项明确指定运行单文件应用

⚠️ **构建缓存** - SDK 会缓存构建输出以提高性能，如需清除缓存：
```bash
dotnet clean file-based-apps
```

⚠️ **并发运行** - 同时运行多个实例可能导致文件冲突，建议先构建：
```bash
dotnet build script.cs
dotnet run script.cs --no-build
```

---

## 📚 参考资源

- [Microsoft Learn - File-based apps](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)
- [Microsoft DevBlogs - A simpler way to start with C#](https://devblogs.microsoft.com/dotnet/announcing-dotnet-run-app/)
- [Andrew Lock - Exploring dotnet run app.cs](https://andrewlock.net/exploring-dotnet-10-preview-features-1-exploring-the-dotnet-run-app.cs/)

---

## 🛠️ 创建新工具

如需创建新的脚本工具，请遵循以下步骤：

1. **创建 `.cs` 文件**
   ```csharp
   using System;
   
   Console.WriteLine("Hello from script!");
   ```

2. **添加必要的包引用**（可选）
   ```csharp
   #:package PackageName@version
   ```

3. **运行测试**
   ```bash
   dotnet run --file 脚本工具\your-script.cs
   ```

4. **更新本 README**，添加工具说明

---

**最后更新日期**：2026年7月5日
