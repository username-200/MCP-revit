# Установка плагина revit-mcp-server

Порядок перехода с прежнего моста на плагин
[LuDattilo/revit-mcp-server](https://github.com/LuDattilo/revit-mcp-server).

Сведения об установке взяты из README того проекта; при расхождениях верьте
первоисточнику — он обновляется независимо от этого репозитория.

---

## 1. Снять прежний аддин

Revit должен быть закрыт.

```powershell
cd C:\MCP-revit
.\scripts\uninstall-addin.ps1
```

Скрипт удаляет `McpRevit.addin` и папку `McpRevit` из каталогов аддинов Revit
2023–2027. Формально это не обязательно — прежний мост слушал порт 8765, а новый
плагин занимает 8080, — но две вкладки на ленте и два TCP-слушателя ни к чему.

## 2. Требования

| Что | Зачем |
| --- | --- |
| Node.js 18+ | На нём работает MCP-сервер плагина |
| Revit 2023–2027 | Поддерживаются все пять версий |
| Windows 10/11 | Revit существует только под Windows |

Проверка Node.js:

```powershell
node --version
```

Если команды нет — поставьте LTS-версию: `winget install OpenJS.NodeJS.LTS`,
затем откройте новое окно PowerShell.

## 3. Установка плагина

### Готовый установщик

В README проекта предлагается однострочник:

```powershell
powershell -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/LuDattilo/revit-mcp-server/main/scripts/install.ps1 | iex"
```

Он определяет установленные версии Revit, скачивает нужный релиз, распаковывает
его и снимает блокировку с DLL.

Учтите, что эта конструкция выполняет скачанный скрипт без просмотра, а его
содержимое может измениться в любой момент. Безопаснее скачать и прочитать:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/LuDattilo/revit-mcp-server/main/scripts/install.ps1" -OutFile "$env:TEMP\install-revit-mcp.ps1"
notepad "$env:TEMP\install-revit-mcp.ps1"
& "$env:TEMP\install-revit-mcp.ps1" -RevitVersion 2026
```

### Установка вручную

Скачайте ZIP со страницы [Releases](https://github.com/LuDattilo/revit-mcp-server/releases)
под свою версию Revit. **Исходники клонировать бесполезно** — плагину нужны
скомпилированные `.dll`, которых в репозитории нет.

Распакуйте содержимое в `%AppData%\Autodesk\Revit\Addins\<версия>\`. Должно
получиться так:

```
Addins\2026\
├── mcp-servers-for-revit.addin
└── revit_mcp_plugin\
    ├── RevitMCPPlugin.dll
    ├── RevitMCPSDK.dll
    ├── Newtonsoft.Json.dll
    ├── tool_schemas.json
    └── Commands\
```

Манифест `.addin` лежит **прямо** в папке версии, а не внутри `revit_mcp_plugin`.

Windows помечает скачанные файлы как заблокированные, и Revit такие сборки не
загрузит. Снять пометку разом:

```powershell
Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\2026" -Recurse -File | Unblock-File
```

## 4. Подключить MCP-клиент

**Claude Code:**

```powershell
claude mcp add mcp-server-for-revit -- npx -y mcp-server-for-revit
```

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
    "mcpServers": {
        "mcp-server-for-revit": {
            "command": "npx",
            "args": ["-y", "mcp-server-for-revit"]
        }
    }
}
```

После правки перезапустите клиент: конфигурация читается при старте.

## 5. Проверка

1. Запустите Revit, откройте проект.
2. Вкладка **Add-Ins**, панель **Revit MCP Plugin** — три кнопки:
   **Revit MCP Switch**, **MCP Panel**, **Settings**.
3. Нажмите **Revit MCP Switch**, дождитесь зелёного индикатора.
4. В клиенте спросите что-нибудь простое: «покажи сведения о проекте Revit»
   (инструмент `get_project_info`).

Если видна только кнопка **Switch**, а **MCP Panel** и **Settings** нет — плагин
установлен не полностью. Обычная причина: скопированы исходники вместо релиза.

## Диагностика

| Симптом | Причина |
| --- | --- |
| Вкладки Add-Ins нет | `.addin` лежит не в корне папки версии, либо DLL заблокированы Windows |
| «Connection refused» | Revit закрыт или переключатель MCP выключен |
| Порт 8080 занят | Проверьте: `netstat -an \| findstr 8080` |
| Инструментов не видно в клиенте | Перезапустите клиент — список инструментов кэшируется |
| «Parameter not found» | Имена параметров зависят от языка интерфейса Revit |

## Чего в этом плагине нет

Среди 124 инструментов **нет работы с облаками точек**: ни подключения сканов, ни
детекции плоскостей, ни построения стен по облаку. Обмерная задача, ради которой
затевался прежний мост, штатными средствами плагина не решается.

Обходной путь — инструмент `send_code_to_revit`, выполняющий произвольный C# внутри
Revit: через него можно вызвать те же методы Revit API. Наработки для такого
переноса сохранены в [../porting/README.md](../porting/README.md).
