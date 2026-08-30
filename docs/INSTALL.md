# Установка и настройка

## Требования

| Компонент | Версия |
| --- | --- |
| Autodesk Revit | 2023, 2024, 2025 или 2026 |
| .NET SDK | 6.0+ (для сборки аддина; Revit 2025+ требует .NET 8 SDK) |
| Python | 3.10+ |
| Autodesk ReCap | нужен, только если исходные сканы не в формате `.rcp`/`.rcs` |

Revit работает исключительно под Windows, поэтому вся связка ставится на ту же машину, где
открыт проект.

## 1. Аддин Revit

### Автоматически

```powershell
git clone https://github.com/username-200/MCP-revit.git C:\MCP-revit
cd C:\MCP-revit
.\scripts\install-addin.ps1 -RevitVersion 2023
```

Скрипт собирает проект, копирует сборку в
`%APPDATA%\Autodesk\Revit\Addins\<версия>\McpRevit\` и кладёт рядом манифест `McpRevit.addin`
и файл настроек `mcp-revit.config.json`.

### Вручную

```powershell
dotnet build revit-addin\McpRevit\McpRevit.csproj -c Release -p:RevitVersion=2023
```

Если Revit установлен не в стандартную папку, добавьте
`-p:RevitApiDir="D:\Autodesk\Revit 2023"`.

Затем скопируйте `McpRevit.dll` и `McpRevit.addin` в
`%APPDATA%\Autodesk\Revit\Addins\2023\`.

### Настройки аддина

`mcp-revit.config.json` рядом со сборкой:

```json
{ "port": 8765, "token": "", "auto_start": true }
```

Значения перекрываются переменными окружения `MCPREVIT_PORT`, `MCPREVIT_TOKEN`,
`MCPREVIT_AUTOSTART`.

### Проверка

Запустите Revit и откройте проект. На вкладке **MCP** появятся кнопки «Старт / Стоп» и «Статус».
Мост отвечает на два служебных запроса:

```powershell
curl http://127.0.0.1:8765/health     # состояние
curl http://127.0.0.1:8765/commands   # список команд
```

При первом запуске Revit спросит о доверии к незагруженному аддину — разрешите загрузку
(«Always Load»). Если Windows покажет запрос брандмауэра, разрешение не требуется: мост слушает
только петлевой интерфейс.

## 2. MCP-сервер

```powershell
cd C:\MCP-revit
py -3.11 -m venv .venv
.\.venv\Scripts\pip install -e .\mcp-server
```

Переменные окружения:

| Переменная | Назначение | По умолчанию |
| --- | --- | --- |
| `REVIT_BRIDGE_URL` | адрес моста | `http://127.0.0.1:8765` |
| `REVIT_BRIDGE_TOKEN` | общий секрет, если задан в аддине | пусто |
| `REVIT_BRIDGE_TIMEOUT` | таймаут команды, с | `180` |
| `REVIT_MCP_TEMPLATES_DIR` | дополнительная папка шаблонов | `templates/` в репозитории |

## 3. Подключение MCP-клиента

### Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\MCP-revit\\.venv\\Scripts\\python.exe",
      "args": ["-m", "revit_mcp.server"],
      "env": {
        "REVIT_BRIDGE_URL": "http://127.0.0.1:8765",
        "REVIT_MCP_TEMPLATES_DIR": "C:\\MCP-revit\\templates"
      }
    }
  }
}
```

После правки конфигурации перезапустите клиент.

### Claude Code

```powershell
claude mcp add revit -- C:\MCP-revit\.venv\Scripts\python.exe -m revit_mcp.server
```

## 4. Первый запуск

Порядок важен: Revit с открытым проектом → мост «работает» → MCP-клиент.

Попросите ассистента проверить связь — инструмент `revit_status` вернёт версию Revit, название
проекта, количество уровней и подключённых облаков точек.

## Диагностика

| Симптом | Причина и решение |
| --- | --- |
| «Мост … недоступен» | Revit не запущен, аддин не загрузился или мост остановлен. Проверьте вкладку **MCP** и `curl /health` |
| «В Revit не открыт ни один документ» | Открыт стартовый экран. Откройте `.rvt`-проект |
| «Revit не ответил за N с» | Открыт модальный диалог внутри Revit либо идёт долгая операция. Закройте диалог; для тяжёлых облаков поднимите `REVIT_BRIDGE_TIMEOUT` |
| Вкладки **MCP** нет | Аддин не загружен: проверьте путь в `McpRevit.addin` и что версия сборки совпадает с версией Revit |
| «Неверный или отсутствующий заголовок X-Mcp-Token» | Токен в аддине и в `REVIT_BRIDGE_TOKEN` различаются |
| Из облака приходит мало точек | Уменьшите `distance_tolerance_mm`, увеличьте `max_points` или ограничьте выборку областью одного этажа |
| Revit не принимает файл скана | Формат не `.rcp`/`.rcs` — сконвертируйте в ReCap |

## Обновление

```powershell
git pull
.\scripts\install-addin.ps1 -RevitVersion 2023   # Revit при этом должен быть закрыт
.\.venv\Scripts\pip install -e .\mcp-server
```

Revit держит `McpRevit.dll` открытым, пока работает, поэтому переустановка аддина на запущенном
Revit завершится ошибкой доступа к файлу.
