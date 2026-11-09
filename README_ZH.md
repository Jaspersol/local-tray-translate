<div align="center">

<img src="docs/logo.png" width="120" alt="TranslateLite logo">

# TranslateLite

### 一个离线作答的轻量 Windows 词典

**按一次热键查光标下的词，或框选一块屏幕让 Windows OCR 识别再查。全部由本地 SQLite 词典作答，不联网、不登录。**

[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](#环境要求)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4.svg)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-0078D4.svg)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![SQLite](https://img.shields.io/badge/search-SQLite%20FTS5-003B57.svg)](https://www.sqlite.org/fts5.html)
[![License](https://img.shields.io/badge/license-not%20set-lightgrey.svg)](#许可)

[English](README.md) | 中文

**[快速开始](#快速开始) · [快捷键](#快捷键) · [功能](#功能) · [词典数据](#词典数据) · [常见问题](#常见问题)**

</div>

## 为什么做这个？

多数词典工具要么做成了浏览器标签页，要么要订阅，要么一口气几百 MB 并把整个词表读进内存。TranslateLite 从本地 SQLite 文件里给出答案，以几十 MB 的体积常驻托盘。

- **纯本地** —— 单个 `dictionary.db`（ECDICT + CC-CEDICT），走 FTS5 全文检索；没有服务端
- **两个热键，两条工作流** —— `Ctrl+Alt+X` 取当前选中内容查询；`Ctrl+Alt+S` 唤出截图遮罩、OCR 识别该区域后查询识别结果
- **分层查询，而不是一条 `LIKE`** —— 精确匹配 → 用户词条 → 分词候选 → FTS5 前缀搜索 → 编辑距离兜底，所以 `helo` 也能找到 `halo`
- **用户词典单独成库** —— 自补词条放在 `user_dictionary.db`，替换主词典永远不会动到它
- **OCR 而非机器翻译** —— 截图识别出字之后去查**这些字**，这是阅读场景要的效果，不是整句翻译

## 环境要求

- **Windows 10 build 19041（20H1）及以上** —— 目标框架为 `net8.0-windows10.0.19041.0`
- 从源码构建需要 **.NET 8 SDK** 与 Windows Desktop 工作负载
- **Windows OCR 语言包** —— 在 Windows 设置中安装中英文 OCR 功能可获得最佳识别效果

## 快速开始

```powershell
dotnet restore .\TranslateLite.sln
dotnet build .\TranslateLite.sln
dotnet run --project .\src\TranslateLite\TranslateLite.csproj
```

应用启动后只驻留托盘，查询窗口从托盘菜单打开。

## 快捷键

| 快捷键 | 行为 |
| --- | --- |
| `Ctrl+Alt+X` | 取当前选中内容（经剪贴板）并查询 |
| `Ctrl+Alt+S` | 在屏幕上框选区域，OCR 识别后查询 |

两者都可在托盘图标右键 →「设置」中修改。全局热键必须包含 Ctrl、Alt、Shift 或 Win 中的至少一个修饰键，两个动作也不能用同一组组合键。若 Windows 拒绝注册（截图工具或输入法已占用），托盘菜单会把该组合键标成「未生效」并弹出说明，应用本身仍可正常使用。

## 功能

### 查询

- **分层降级**，每一层只补前面仍缺的结果：
  1. 主词典精确匹配（`normalized_word` 索引）
  2. 用户词条精确匹配，外加翻译/例句的包含匹配
  3. 分词候选逐个精确匹配（拉丁按空格与标点切分，中文做 2~4 字滑窗）
  4. 全文 FTS5 前缀搜索，按 `rank` 排序
  5. 编辑距离兜底（OSA 距离；预算：≤4 字符允许 1，≤8 允许 2，其余 3），`helo`→`halo`、`wrold`→`world` 就靠这一层
- **用户词条优先** —— 自己存过的词先于主词典中同词的条目返回
- **复用的结果窗口** —— 置顶、不占任务栏的同一个窗口，每次查询都刷新它；关闭只是隐藏，留给下次复用

### 取词与 OCR

- **冻结画面的区域遮罩** —— 进入截图时抓一帧全屏画面，在不透明窗口里显示，拖框选区时框外压暗
- **截图防抖** —— 一次截图流程进行中，再按热键或点托盘菜单都会被忽略，不会叠出多个遮罩
- **按帧合并拖拽** —— 鼠标移动合并到每帧一次选区更新，裁剪来自冻结帧，不再有固定等待
- **高 DPI 对齐** —— 选区使用 Win32 物理像素坐标，缩放显示下截图不会错位或变小
- **内存里完成 OCR** —— 选区经内存流解码并复用 OCR 引擎，不写临时 PNG 文件
- **调用系统 OCR** —— `Windows.Media.Ocr`，优先使用你已安装的 OCR 语言
- **不破坏剪贴板的取词** —— 先发 `Ctrl+C`，读出选区后尽力恢复原有的剪贴板内容

### 托盘与设置

- **常驻托盘** —— 关闭查询窗口只是隐藏；托盘菜单含「打开 / 划词翻译（带热键）/ 截图翻译（带热键）/ 设置 / 退出」
- **设置存于 `settings.json`** —— 两个热键（修饰键 + 虚拟键码）与开机自启开关以 JSON 持久化；文件损坏时回落默认值，而不是让应用起不来
- **开机自启** —— 在设置窗口「常规」中勾选后写入 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`；无需管理员权限，程序移动目录后下次启动会自动修正路径

### 检查更新

- **两条检查路径** —— 设置窗口「关于与更新」里手动检查，或打开「启动时自动检查」（每 6 小时最多一次，只提示不自动下载）。主路径请求 GitHub REST API `repos/Jaspersol/local-tray-translate/releases/latest`，读取版本、发布说明、资产与校验值；API 被限流（403）或不可达时自动切到直链备用方案：从 `releases/latest` 的重定向里读版本 tag，从 `SHA256SUMS.txt` 读资产名与 SHA256，全程不碰 `api.github.com`。
- **两条下载路径** —— 安装时优先用 release 资产的 `browser_download_url`，失败再试 API 资产地址，最后兜底到 `releases/latest/download/<name>` 直链；每个候选重试一次。
- **强制校验** —— 校验值来自 API 资产的 `digest`、`<zip>.sha256` 或 `SHA256SUMS.txt`；缺失或 SHA256 不匹配一律拒绝安装。下载还限制 HTTPS + GitHub 域名白名单（重定向逐跳校验）、包体上限与 zip 文件头。
- **安装方式** —— 便携版没有安装程序：校验通过的更新包解压到程序目录旁的 `.new`，应用退出后由脚本做目录级原子替换并重启；用户数据仍在 `%LOCALAPPDATA%\TranslateLite`，不受影响。

### 用户词典

- **独立的库文件** —— `%LOCALAPPDATA%\TranslateLite\user_dictionary.db`，与主词典分开，升级词典不会覆盖用户数据
- **管理窗口** —— 新增、编辑（双击行）、删除、按词条/翻译/标签过滤，以及从文本文件批量导入
- **批量导入格式** —— 每行一个词条，`#` 开头的行被忽略；支持制表符分隔（`.tsv`/`.txt`）与逗号分隔的 CSV（字段可用双引号包裹）：

```text
word<TAB>translation[<TAB>definition[<TAB>example]]
```

## 词典数据

首次运行时应用会创建 `%LOCALAPPDATA%\TranslateLite\dictionary.db` 并插入少量示例数据，方便试出界面效果。真实数据按 `data/schema.sql` 定义的结构写入 `entries` 表：

```sql
INSERT INTO entries(word, normalized_word, lang, phonetic, definition, translation, example, source)
VALUES ('example', 'example', 'en', 'ig-zam-puhl', 'a representative item', '示例翻译', 'This is an example.', 'ecdict');
```

推荐数据源：

- **ECDICT**：英中词条
- **CC-CEDICT**：中英词条

建议离线导入、按需查询 —— 把整本词典读进内存会让「常驻托盘的小工具」这个定位失去意义。

### 导入 ECDICT 与 CC-CEDICT

仓库内自带两个导入工具：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\download_dictionaries.ps1
python .\tools\import_dictionaries.py --ecdict .\data\raw\ecdict.csv --cedict .\data\raw\cedict_ts.u8 --output .\data\dictionary.db
python .\tools\verify_dictionary.py .\data\dictionary.db
```

`download_dictionaries.ps1` 把 ECDICT 的 CSV 与 CC-CEDICT 的 gzip 文本下载到 `data/raw/` 并解压后者；`import_dictionaries.py` 重建 `entries` 与 `entries_fts`；`verify_dictionary.py` 打印行数和几个示例查询 —— 这是区分「导入坏了」还是「查询坏了」最快的办法。

生成的 `data/dictionary.db` 会在构建时复制到输出目录；首次启动时应用把它复制到 `%LOCALAPPDATA%\TranslateLite\dictionary.db`。

## 构建与发布

```powershell
# 方式一：使用发布脚本
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\TranslateLite\publish.ps1

# 方式二：直接使用 dotnet publish
dotnet publish .\src\TranslateLite\TranslateLite.csproj -c Release -p:PublishProfile=FolderProfile -o .\publish
```

产物是自包含的目录，含 `TranslateLite.exe` 与 `dictionary.db`。`publish.ps1` 会额外打包成 `LocalTrayTranslate-<版本>-win-x64.zip`，并生成 `<zip>.sha256` 与 `SHA256SUMS.txt`；发布 Release 时三个文件都要上传，检查更新的主路径与直链兜底路径都依赖校验值。

## 项目结构

```text
src/TranslateLite
|-- App.xaml(.cs)                          启动与服务装配
|-- Interop/HotkeyService.cs                Win32 全局热键
|-- Models/DictionaryEntry.cs               查询结果模型
|-- Models/AppSettings.cs                   设置模型
|-- Services/DictionaryService.cs           SQLite 与 FTS 查询
|-- Services/OcrService.cs                  Windows OCR 适配
|-- Services/TrayService.cs                 托盘图标与菜单
|-- Services/ClipboardSelectionService.cs   经剪贴板取词
|-- Services/SettingsService.cs             settings.json 持久化
|-- Services/AutoStartService.cs            开机自启注册表项
|-- Models/UpdateInfo.cs                   检查结果与下载进度模型
|-- Services/UpdateService.cs             检查/下载/安装更新（API + 直链双路径）
`-- Windows/                               WPF 窗口：主窗口、结果、截图、设置、用户词典
```

## 技术栈

C# WPF，目标 `net8.0-windows10.0.19041.0` · `Microsoft.Data.Sqlite` + SQLite FTS5 · 系统 OCR（`Windows.Media.Ocr`）· Win32 `RegisterHotKey` · WinForms `NotifyIcon` 托盘

## 常见问题

<details>
<summary><strong>截图翻译是在翻译整句吗？</strong></summary>

不是。它是 OCR 识别 + 词典查询：识别出来的文字被当作词条去查。整句机器翻译需要应用刻意没有内置的模型。

</details>

<details>
<summary><strong>截图热键在网页或图片上什么都识别不到。</strong></summary>

Windows OCR 只按系统「看到」的样子认字；强样式网页文字、深色主题、过小字号常常低于它的置信门槛。放大后再框选，或缩小框选范围。另外确认中英文 OCR 语言包已安装。

</details>

<details>
<summary><strong>用了划词热键后剪贴板内容变了。</strong></summary>

`Ctrl+Alt+X` 会发送 `Ctrl+C`，之后尽力恢复原有剪贴板内容。对拒绝或延迟响应该复制的应用，剪贴板可能停留在选区内容上。

</details>

<details>
<summary><strong>我的数据在哪里？</strong></summary>

`%LOCALAPPDATA%\TranslateLite\` 下：`dictionary.db`、`user_dictionary.db`、`settings.json`。删掉 `dictionary.db` 后，下次启动会从安装目录里的副本重新复制一份。

</details>

<details>
<summary><strong>能替换主词典又保留自己的词条吗？</strong></summary>

可以。用户词条放在 `user_dictionary.db` 这个独立库里，替换 `dictionary.db` 不影响它们。

</details>

<details>
<summary><strong>某个热键完全没反应。</strong></summary>

已被其它程序占用（截图工具、输入法最常见）。托盘菜单会把该组合键显示为「未生效」，并弹出提示说明是哪一个；应用仍能启动，在「设置」里换一个组合即可。

</details>

<details>
<summary><strong>多显示器上框选位置和截图内容对不上。</strong></summary>

裁剪使用 Win32 物理像素。各显示器缩放比例不同时，单个全屏 WPF 窗口无法把 DIP 线性映射到像素，副屏上的框可能与裁剪区域有偏差；缩放比例一致的多屏不受影响。

</details>

## 许可

本仓库尚未包含许可文件 —— 版权归各自作者所有，保留所有权利。如需明确授权，请开 issue 说明。
