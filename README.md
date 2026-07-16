<a id="english"></a>
# Sloc

[English](#english) | [中文](#中文)

[![CI](https://github.com/coldhighsun/Sloc/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/Sloc/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/Sloc)](https://www.nuget.org/packages/Sloc)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Sloc?label=nuget%20downloads)](https://www.nuget.org/packages/Sloc)
[![GitHub All Releases](https://img.shields.io/github/downloads/coldhighsun/Sloc/total?label=release%20downloads)](https://github.com/coldhighsun/Sloc/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Sloc (**S**ource **L**ines **O**f **C**ode) is a .NET global command-line tool for counting lines of source code. It analyzes files individually, distinguishing **code lines**, **comment lines**, and **blank lines**, then aggregates results by programming language. It supports 23 auto-detected languages, three output formats (table, JSON, HTML), per-file detail view, and comment health indicators.

## Features

- Count code / comment / blank / total lines
- Correctly handles single-line comments, block comments, and multi-line block comments
- Built-in comment rules for 23 common languages (auto-detected by file extension)
- Recursive directory scanning with `--include` / `--exclude` glob filters
- Automatically excludes `bin`, `obj`, `artifacts`, `.git`, `.vs`, `.vscode`, `.idea`, `node_modules`, and similar directories by default
- Three output formats: colored table (default), JSON, and HTML
- Comment Health column in table output showing comment-density indicator (None / Low / Fair / Good / High / Dense)
- Format auto-detected from the `--output` file extension (`.json` → JSON, `.html` / `.htm` → HTML)
- Files or directories that cannot be read are skipped gracefully; a summary of skipped paths and reasons is shown at the end

## Installation

Install via [winget](https://learn.microsoft.com/windows/package-manager/winget/) (Windows):

```bash
winget install coldhighsun.sloc
```

Or install as a .NET global tool (requires .NET 8 SDK):

```bash
dotnet tool install --global Sloc
```

Update or uninstall:

```bash
winget upgrade coldhighsun.sloc
winget uninstall coldhighsun.sloc

dotnet tool update --global Sloc
dotnet tool uninstall --global Sloc
```

## Usage

```bash
sloc <path> [options]
```

### Examples

```bash
# Count current directory (recursive)
sloc

# Count a specific directory
sloc ./src

# Count a single file
sloc ./src/Program.cs

# Only count C# files
sloc ./src --include "**/*.cs"

# Exclude test directories
sloc . --exclude "**/tests/**"

# Show per-file details
sloc ./src --by-file

# Output as JSON
sloc ./src --format json

# Save results as HTML report (opens in a browser)
sloc ./src --format html --output report.html

# Auto-detect format from file extension
sloc ./src --output sloc-report.json

# Include files with unknown extensions
sloc . --all

# Do not recurse into subdirectories
sloc ./src --no-recursive
```

### Options

| Option | Short | Description |
| --- | --- | --- |
| `path` (argument) | | File or directory to analyze, defaults to current directory `.` |
| `--include` | `-i` | File glob pattern to include, can be specified multiple times |
| `--exclude` | `-e` | File glob pattern to exclude, can be specified multiple times |
| `--format` | `-f` | Output format: `Table` (default), `Json`, or `Html` |
| `--output` | `-o` | Output file path for `Json` / `Html` formats; format is inferred from the file extension when `--format` is not specified |
| `--no-recursive` | | Do not recurse into subdirectories |
| `--no-health` | | Hide the Comment Health column and percentage breakdowns |
| `--by-file` | | Show per-file details in addition to the language summary |
| `--paged` | `-p` | Show paged output |
| `--all` | | Include files with unknown extensions (grouped as `Other`) |
| `--help` | `-h` | Show help |
| `--version` | | Show version |

### Sample Output

```
╭──────────────┬───────┬─────────────┬─────────────┬─────────────┬───────┬────────────────╮
│ Language     │ Files │        Code │     Comment │       Blank │ Total │ Comment Health │
├──────────────┼───────┼─────────────┼─────────────┼─────────────┼───────┼────────────────┤
│ C#           │    12 │  840 ( 75%) │  120 ( 11%) │  160 ( 14%) │ 1,120 │ ■ Good         │
│ XML          │     3 │   45 ( 76%) │    6 ( 10%) │    8 ( 14%) │    59 │ —              │
│              │       │             │             │             │       │                │
│ Total        │    15 │  885 ( 75%) │  126 ( 11%) │  168 ( 14%) │ 1,179 │ —              │
╰──────────────┴───────┴─────────────┴─────────────┴─────────────┴───────┴────────────────╯
```

With `--by-file` (tree view per file):

```
╭───────────────────────────┬──────────┬─────────────┬─────────────┬─────────────┬───────┬────────────────╮
│ File                      │ Language │        Code │     Comment │       Blank │ Total │ Comment Health │
├───────────────────────────┼──────────┼─────────────┼─────────────┼─────────────┼───────┼────────────────┤
│ ├── 📁 Cli                │          │             │             │             │       │                │
│ │   ├── AnalyzeHandler.cs │ C#       │   68 ( 76%) │   12 ( 13%) │    9 ( 10%) │    89 │ ■ Good         │
│ │   └── Program.cs        │ C#       │   34 ( 83%) │    3 (  7%) │    4 ( 10%) │    41 │ ■ Fair         │
│ └── 📁 Core               │          │             │             │             │       │                │
│     ├── Analyzer.cs       │ C#       │  120 ( 75%) │   20 ( 13%) │   20 ( 13%) │   160 │ ■ Good         │
│     └── Models.cs         │ C#       │   80 ( 74%) │   12 ( 11%) │   16 ( 15%) │   108 │ ■ Good         │
│                           │          │             │             │             │       │                │
│ Total                     │ 4        │  302 ( 76%) │   47 ( 12%) │   49 ( 12%) │   398 │ —              │
╰───────────────────────────┴──────────┴─────────────┴─────────────┴─────────────┴───────┴────────────────╯
```

## Supported Languages

C#, C/C++, Java, Kotlin, Swift, JavaScript, TypeScript, Python, Go, Rust, PHP, Ruby, F#, Visual Basic, SQL, PowerShell, Shell, YAML, JSON, HTML, XML, CSS, SCSS/Less.

> Files with unknown extensions are grouped as `Other` when using `--all`, with only code lines and blank lines distinguished.

## Building from Source

```bash
# Restore and build
dotnet build

# Run tests
dotnet test

# Pack as a NuGet tool package (output to ./nupkg)
dotnet pack src/Sloc.Cli/Sloc.Cli.csproj -c Release -o ./nupkg

# Install from local package and verify
dotnet tool install --global --add-source ./nupkg Sloc
sloc ./src
```

## Known Limitations

- String literals are not parsed, so comment markers inside strings (e.g., `"// not a comment"`) may be misclassified.
- Line classification is based on text matching of comment symbols, without full lexical analysis.

## License

This project is released under the [MIT License](https://opensource.org/licenses/MIT).

---

<a id="中文"></a>
# Sloc

[English](#english) | [中文](#中文)

[![CI](https://github.com/coldhighsun/Sloc/actions/workflows/ci.yml/badge.svg)](https://github.com/coldhighsun/Sloc/actions/workflows/ci.yml)
[![NuGet Version](https://img.shields.io/nuget/v/Sloc)](https://www.nuget.org/packages/Sloc)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Sloc)](https://www.nuget.org/packages/Sloc)
[![GitHub All Releases](https://img.shields.io/github/downloads/coldhighsun/Sloc/total?label=release%20downloads)](https://github.com/coldhighsun/Sloc/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Sloc（**S**ource **L**ines **O**f **C**ode）是一个用于统计源代码行数的 .NET 全局命令行工具。它会逐文件分析代码，区分**代码行**、**注释行**和**空行**，并按编程语言进行聚合汇总，支持 23 种语言自动识别、三种输出格式（表格、JSON、HTML）、逐文件明细视图以及注释健康度指标。

## 功能特性

- 统计代码行 / 注释行 / 空行 / 总行数
- 正确处理单行注释、块注释以及跨多行的块注释
- 内置 23 种常见语言的注释规则（按文件扩展名自动识别）
- 递归扫描目录，支持 `--include` / `--exclude` glob 过滤
- 默认排除 `bin`、`obj`、`artifacts`、`.git`、`.vs`、`.vscode`、`.idea`、`node_modules` 等目录
- 三种输出格式：彩色表格（默认）、JSON 与 HTML
- 表格输出新增注释健康度列，显示注释密度指标（无 / 低 / 一般 / 良好 / 较高 / 过密）
- 未指定 `--format` 时，可根据 `--output` 文件扩展名自动推断格式（`.json` → JSON，`.html` / `.htm` → HTML）
- 无法读取的文件或目录会被自动跳过，并在最终结果中列出所有跳过的路径及原因

## 安装

通过 [winget](https://learn.microsoft.com/windows/package-manager/winget/) 安装（Windows）：

```bash
winget install coldhighsun.sloc
```

或作为 .NET 全局工具安装（需要 .NET 8 SDK）：

```bash
dotnet tool install --global Sloc
```

更新或卸载：

```bash
winget upgrade coldhighsun.sloc
winget uninstall coldhighsun.sloc

dotnet tool update --global Sloc
dotnet tool uninstall --global Sloc
```

## 使用方法

```bash
sloc <path> [options]
```

### 示例

```bash
# 统计当前目录（递归）
sloc

# 统计指定目录
sloc ./src

# 统计单个文件
sloc ./src/Program.cs

# 仅统计 C# 文件
sloc ./src --include "**/*.cs"

# 排除测试目录
sloc . --exclude "**/tests/**"

# 显示逐文件明细
sloc ./src --by-file

# 以 JSON 格式输出
sloc ./src --format json

# 保存为 HTML 报告（可在浏览器中打开）
sloc ./src --format html --output report.html

# 根据文件扩展名自动推断格式
sloc ./src --output sloc-report.json

# 包含未知扩展名的文件
sloc . --all

# 不递归子目录
sloc ./src --no-recursive
```

### 选项

| 选项 | 简写 | 说明 |
| --- | --- | --- |
| `path`（参数） | | 要分析的文件或目录，默认为当前目录 `.` |
| `--include` | `-i` | 要包含的文件 glob 模式，可多次指定 |
| `--exclude` | `-e` | 要排除的文件 glob 模式，可多次指定 |
| `--format` | `-f` | 输出格式：`Table`（默认）、`Json` 或 `Html` |
| `--output` | `-o` | `Json` / `Html` 格式的输出文件路径；未指定 `--format` 时根据文件扩展名自动推断格式 |
| `--no-recursive` | | 不递归扫描子目录 |
| `--no-health` | | 隐藏注释健康度列及百分比数据 |
| `--by-file` | | 在语言汇总之外额外显示逐文件明细 |
| `--paged` | `-p` | 显示分页输出 |
| `--all` | | 包含扩展名未知的文件（归入 `Other`） |
| `--help` | `-h` | 显示帮助 |
| `--version` | | 显示版本 |

### 输出示例

```
╭──────────────┬───────┬─────────────┬─────────────┬─────────────┬───────┬────────────────╮
│ Language     │ Files │        Code │     Comment │       Blank │ Total │ Comment Health │
├──────────────┼───────┼─────────────┼─────────────┼─────────────┼───────┼────────────────┤
│ C#           │    12 │  840 ( 75%) │  120 ( 11%) │  160 ( 14%) │ 1,120 │ ■ Good         │
│ XML          │     3 │   45 ( 76%) │    6 ( 10%) │    8 ( 14%) │    59 │ —              │
│              │       │             │             │             │       │                │
│ Total        │    15 │  885 ( 75%) │  126 ( 11%) │  168 ( 14%) │ 1,179 │ —              │
╰──────────────┴───────┴─────────────┴─────────────┴─────────────┴───────┴────────────────╯
```

使用 `--by-file` 选项（按文件树形展示）：

```
╭───────────────────────────┬──────────┬─────────────┬─────────────┬─────────────┬───────┬────────────────╮
│ File                      │ Language │        Code │     Comment │       Blank │ Total │ Comment Health │
├───────────────────────────┼──────────┼─────────────┼─────────────┼─────────────┼───────┼────────────────┤
│ ├── 📁 Cli                │          │             │             │             │       │                │
│ │   ├── AnalyzeHandler.cs │ C#       │   68 ( 76%) │   12 ( 13%) │    9 ( 10%) │    89 │ ■ Good         │
│ │   └── Program.cs        │ C#       │   34 ( 83%) │    3 (  7%) │    4 ( 10%) │    41 │ ■ Fair         │
│ └── 📁 Core               │          │             │             │             │       │                │
│     ├── Analyzer.cs       │ C#       │  120 ( 75%) │   20 ( 13%) │   20 ( 13%) │   160 │ ■ Good         │
│     └── Models.cs         │ C#       │   80 ( 74%) │   12 ( 11%) │   16 ( 15%) │   108 │ ■ Good         │
│                           │          │             │             │             │       │                │
│ Total                     │ 4        │  302 ( 76%) │   47 ( 12%) │   49 ( 12%) │   398 │ —              │
╰───────────────────────────┴──────────┴─────────────┴─────────────┴─────────────┴───────┴────────────────╯
```

## 支持的语言

C#、C/C++、Java、Kotlin、Swift、JavaScript、TypeScript、Python、Go、Rust、PHP、Ruby、F#、Visual Basic、SQL、PowerShell、Shell、YAML、JSON、HTML、XML、CSS、SCSS/Less。

> 扩展名未知的文件在使用 `--all` 时会被归入 `Other` 类别，仅区分代码行与空行。

## 从源码构建

```bash
# 还原与构建
dotnet build

# 运行测试
dotnet test

# 打包为 NuGet 工具包（输出到 ./nupkg）
dotnet pack src/Sloc.Cli/Sloc.Cli.csproj -c Release -o ./nupkg

# 从本地包安装并验证
dotnet tool install --global --add-source ./nupkg Sloc
sloc ./src
```

## 已知限制

- 不解析字符串字面量，因此出现在字符串内部的注释符号（例如 `"// 这不是注释"`）可能被误判。
- 行的分类基于注释符号的文本匹配，不进行完整的词法分析。

## 许可证

本项目基于 [MIT 许可证](https://opensource.org/licenses/MIT) 发布。
