# SyncFiles - Visual Studio 文件同步与自动化工具

[![GitHub issues](https://img.shields.io/github/issues/sammiler/Sync.Files?style=flat-square)](https://github.com/sammiler/Sync.Files/issues)
[![GitHub stars](https://img.shields.io/github/stars/sammiler/Sync.Files?style=flat-square)](https://github.com/sammiler/Sync.Files/stargazers)
[![License: MIT](https://img.shields.io/github/license/sammiler/Sync.Files?style=flat-square)](https://github.com/sammiler/Sync.Files/blob/main/LICENSE)

**[English Version (Coming Soon / Or link if you have one)]**

**SyncFiles** 是一个功能强大的 Visual Studio 扩展插件，旨在简化您的开发工作流程。它通过自动化文件同步、脚本执行以及智能工作流程管理，帮助开发者提高效率。您可以直接从 GitHub 同步文件和目录，运行 Python 脚本，并根据文件变更自动触发预设的工作流程，一切尽在 Visual Studio IDE 中。

➡️ **访问我们的 GitHub 仓库：[https://github.com/sammiler/Sync.Files](https://github.com/sammiler/Sync.Files)** ⬅️

## 🚀 核心功能

*   **GitHub 文件同步**：轻松从公共或私有 GitHub 仓库同步单个文件或整个目录到您的本地项目。
*   **Python 脚本执行**：内置终端界面，方便您直接在 Visual Studio 中运行和管理 Python 脚本。
*   **文件变更监控**：监控指定文件或目录的变更（创建、修改、删除、重命名），并自动触发关联的脚本执行。
*   **智能工作流程**：通过简单的 YAML 文件配置，快速设置和执行复杂的工作流程，自动化一系列任务。
*   **环境变量管理**：灵活配置和使用环境变量，支持自定义变量及内置变量（如 `PROJECT_DIR`, `USER_HOME`）。
*   **脚本组织管理**：将常用的脚本进行分组管理，便于查找和执行，提高工作效率。

## 🛠️ 安装方法

1.  打开 Visual Studio。
2.  导航到 **扩展 (Extensions)** > **管理扩展 (Manage Extensions)**。
3.  在搜索框中输入 "SyncFiles"。
4.  找到 SyncFiles 插件并点击 **安装 (Install)**。
5.  重启 Visual Studio 以激活扩展。

## 📖 使用指南

### 1. 打开 SyncFiles 工具窗口
    - 在 Visual Studio 菜单栏中，选择 **视图 (View)** > **其他窗口 (Other Windows)** > **SyncFiles**。

### 2. GitHub 文件同步
    1. 在 SyncFiles 工具窗口中，选择“同步映射 (Sync Mappings)”标签页。
    2. 点击“添加 (Add)”按钮，配置 GitHub 源 URL 和本地目标路径。
        *   **支持的 URL 格式**：
            *   单个文件: `https://github.com/用户名/仓库名/blob/分支名/路径/文件名`
            *   目录: `https://github.com/用户名/仓库名/tree/分支名/路径/目录名`
            *   (也支持 `raw.githubusercontent.com` 的文件链接)
    3. 选中要同步的条目，点击“同步 (Sync)”按钮开始同步操作。

### 3. Python 脚本执行
    1. 在 SyncFiles 工具窗口中，选择“脚本 (Scripts)”标签页。
    2. 点击设置按钮（通常是齿轮图标），配置 Python 解释器路径和项目脚本的根目录。
    3. 脚本列表将显示指定目录下的 Python 脚本。
    4. 双击脚本名称或选中后点击“运行 (Run)”按钮，在内置终端中执行脚本。

### 4. 文件监控配置
    1. 在 SyncFiles 工具窗口中，选择“文件监控 (File Watchers)”标签页。
    2. 点击“添加 (Add)”按钮，创建新的监控条目。
    3. 指定要监控的文件或目录路径。
    4. 选择监控的事件类型（如：修改、创建、删除）。 TODO
    5. 指定当文件发生变更时要执行的脚本。
    6. 启用该监控条目。当监控路径下的文件发生指定类型的变更时，将自动执行关联脚本。

### 5. 智能工作流程
    1. 准备一个 YAML 文件，其中包含工作流程的配置信息（如环境变量、文件同步任务、脚本执行序列等）。
    2. 在 SyncFiles 工具窗口中，选择“工作流程 (Workflows)”标签页。
    3. 点击“加载工作流程 (Load Workflow)”。
    4. 输入 YAML 文件的 URL 或本地文件路径。
    5. SyncFiles 将根据 YAML 配置自动设置环境、同步所需文件并按顺序执行定义的任务。

## ⚙️ 配置文件

插件的所有配置信息都存储在您项目根目录下的 `.vs/syncFilesConfig.xml` 文件中。这包括：
*   GitHub 同步映射关系
*   Python 环境配置（解释器路径、脚本目录）
*   自定义环境变量设置
*   文件监控规则
*   脚本组和脚本条目

**注意**：`.vs` 目录通常被 Git 忽略。如果您希望团队共享配置，可以考虑将此文件（或其部分内容）提交到版本控制，或者使用智能工作流程中的 YAML 文件来分发配置。

## 📂 支持的项目类型

SyncFiles 旨在与多种 Visual Studio 项目类型兼容：
*   标准的 Visual Studio 解决方案项目 (`.sln`)
*   通过 "打开文件夹 (Open Folder)" 模式打开的项目
*   CMake 项目 (能自动检测 `CMakeLists.txt` 文件并适配)

## 📋 系统要求

*   Visual Studio 2022 或更高版本
*   .NET Framework 4.7.2 或更高版本
*   **可选**: Python 环境 (如果需要使用 Python 脚本执行功能)

## ❓ 常见问题 (FAQ)

1.  **问：配置文件保存在哪里？**
    答：配置信息存储在您项目根目录下的 `.vs/syncFilesConfig.xml` 文件中。

2.  **问：如何更改 Python 解释器路径？**
    答：在 SyncFiles 工具窗口的“脚本”标签页，点击设置（齿轮图标），在弹出的设置窗口中可以配置 Python 解释器路径和脚本根目录。

3.  **问：SyncFiles 支持哪些类型的环境变量？**
    答：支持用户自定义的环境变量。此外，还提供了一些方便的内置变量，例如：
    *   `PROJECT_DIR`: 当前 Visual Studio 项目的根目录。
    *   `USER_HOME`: 当前用户的HOME目录。
    *   以及其他可能在后续版本中添加的变量。

4.  **问：内置终端执行 Python 脚本时显示乱码怎么办？**
    答：请确保您的 Python 脚本文件本身使用 UTF-8 编码保存。SyncFiles 插件在执行 Python 脚本时，默认会尝试设置 `PYTHONIOENCODING=utf-8` 环境变量，以帮助解决多数编码问题。如果问题依旧，请检查脚本的实际编码。

## 🤝 贡献

我们欢迎各种形式的贡献！如果您有任何建议、发现 Bug 或想添加新功能，请随时：
*   [提交 Issue](https://github.com/sammiler/Sync.Files/issues)
*   Fork 本仓库并提交 Pull Request

## 📄 许可证

本项目采用 **MIT License**。详情请参阅仓库根目录下的 [`LICENSE`](LICENSE) 文件。

---

感谢使用 SyncFiles！我们希望这个工具能为您的开发工作带来便利。如果您喜欢它，请在 [GitHub](https://github.com/sammiler/Sync.Files) 上给我们一个 ⭐ Star！