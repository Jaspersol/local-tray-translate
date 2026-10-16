# TranslateLite

TranslateLite 是一款轻量级 Windows 桌面词典查询工具，支持中英互查。专注于本地词典搜索、划词翻译、截图 OCR 翻译、托盘常驻和低资源占用。

## 技术栈

- C# WPF，基于 `net8.0-windows10.0.19041.0`
- `Microsoft.Data.Sqlite` + SQLite FTS5 全文搜索
- Windows 内置 OCR（`Windows.Media.Ocr`）
- Win32 全局快捷键
- Windows 托盘图标（WinForms `NotifyIcon`）

## 快捷键

- `Ctrl+Alt+X`：获取选中文本并在词典中查询
- `Ctrl+Alt+S`：框选屏幕区域，调用 Windows OCR 识别文字后在词典中查询

> 快捷键可通过右键托盘图标 → "设置" 进行自定义修改。

## 构建

安装 .NET 8 SDK（包含 Windows Desktop 工作负载），然后运行：

```powershell
dotnet restore .\TranslateLite.sln
dotnet build .\TranslateLite.sln
dotnet run --project .\src\TranslateLite\TranslateLite.csproj
```

## 打包

```powershell
# 方式一：使用发布脚本
powershell -NoProfile -ExecutionPolicy Bypass -File .\src\TranslateLite\publish.ps1

# 方式二：直接使用 dotnet publish
dotnet publish .\src\TranslateLite\TranslateLite.csproj -c Release -p:PublishProfile=FolderProfile -o .\publish
```

发布后会在 `publish\` 目录生成完整的应用程序文件夹，包含 `TranslateLite.exe` 和 `dictionary.db` 等所有文件。将整个 `publish` 文件夹打包为 ZIP 即可分发给其他用户使用。

## 词典数据库

首次运行时会自动创建：

```text
%LOCALAPPDATA%\TranslateLite\dictionary.db
```

应用会插入少量示例数据，方便测试 UI。实际数据应按照 `data/schema.sql` 中定义的表结构导入 `entries` 表：

```sql
INSERT INTO entries(word, normalized_word, lang, phonetic, definition, translation, example, source)
VALUES ('example', 'example', 'en', 'ig-zam-puhl', 'a representative item', '示例翻译', 'This is an example.', 'ecdict');
```

推荐数据源：

- ECDICT 用于英中词条
- CC-CEDICT 用于中英词条

建议离线导入，按需查询 SQLite。如果以低资源占用为目标，避免将整个词典加载到内存中。

## 导入 ECDICT 和 CC-CEDICT

本仓库包含两个词典数据源的导入工具：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\download_dictionaries.ps1
$py='C:\Users\25832\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
& $py .\tools\import_dictionaries.py --ecdict .\data\raw\ecdict.csv --cedict .\data\raw\cedict_ts.u8 --output .\data\dictionary.db
& $py .\tools\verify_dictionary.py .\data\dictionary.db
```

生成的 `data/dictionary.db` 会在构建时复制到应用输出目录。首次启动时，应用会将该数据库复制到 `%LOCALAPPDATA%\TranslateLite\dictionary.db`。

## 项目结构

```text
src/TranslateLite
|-- App.xaml(.cs)                      应用启动与服务组装
|-- Interop/HotkeyService.cs           Win32 全局快捷键
|-- Models/DictionaryEntry.cs          查询结果模型
|-- Models/AppSettings.cs              应用设置模型
|-- Services/DictionaryService.cs      SQLite 和 FTS 查询
|-- Services/OcrService.cs             Windows OCR 适配器
|-- Services/TrayService.cs            托盘图标和菜单
|-- Services/ClipboardSelectionService.cs  剪贴板选词服务
|-- Services/SettingsService.cs        设置持久化服务
`-- Windows/                           WPF 主窗口、弹窗和截图窗口
```

## 说明

- 截图翻译是 OCR 识别 + 词典查询，而非完整的句子机器翻译。
- 划词翻译会短暂使用 `Ctrl+C`，然后尽力恢复剪贴板之前的内容。
- OCR 引擎优先使用用户已安装的 Windows OCR 语言。建议在 Windows 设置中安装中英文 OCR 语言支持以获得最佳效果。

## 用户词典

用户自补词条存放在独立数据库 `%LOCALAPPDATA%\TranslateLite\user_dictionary.db`，与主词典 `dictionary.db` 分离，升级覆盖主库不会丢失用户数据。主窗口点击"用户词典"打开管理界面：新增、编辑（双击行）、删除、按词条/翻译/标签过滤，以及从文本文件批量导入。

批量导入格式为制表符分隔，每行一个词条，`#` 开头的行被忽略：

```text
word<TAB>translation[<TAB>definition[<TAB>example]]
```

查询时用户词条优先于主词典返回。

## 模糊查询

查询按以下优先级逐级降级，直到取满结果：

1. 主词典精确匹配（`normalized_word` 索引）
2. 用户词条精确匹配 + 翻译/例句包含匹配
3. 分词候选逐个精确匹配（拉丁按空格与标点切分，中文做 2~4 字滑窗）
4. FTS5 前缀全文搜索（`rank` 排序）
5. 编辑距离兜底（OSA 距离，覆盖 `helo`→`halo`、`wrold`→`world` 等拼写错误；距离预算：≤4 字符允许 1，≤8 允许 2，其余 3）
