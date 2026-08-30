"""Шаблоны выпуска чертежей.

Шаблон — это JSON-описание комплекта листов: какие листы создать, какие виды на них
разместить, чем заполнить штамп и куда выгрузить. Здесь только чистая логика раскрытия
шаблона в план действий; работа с Revit — в server.py.
"""

from __future__ import annotations

import json
import os
from datetime import date
from pathlib import Path
from typing import Any

VIEW_KINDS = {"plan", "ceiling_plan", "section", "elevation", "3d", "existing"}

DEFAULT_TEMPLATE_DIRS = [
    Path(__file__).resolve().parents[3] / "templates",
]


class TemplateError(ValueError):
    """Шаблон не найден, не читается или не проходит проверку."""


def template_search_dirs(extra: str | None = None) -> list[Path]:
    dirs = [Path(extra)] if extra else []

    from_env = os.environ.get("REVIT_MCP_TEMPLATES_DIR")
    if from_env:
        dirs.extend(Path(part) for part in from_env.split(os.pathsep) if part)

    dirs.extend(DEFAULT_TEMPLATE_DIRS)
    return dirs


def list_templates(extra_dir: str | None = None) -> list[dict[str, Any]]:
    found: list[dict[str, Any]] = []
    seen: set[str] = set()

    for directory in template_search_dirs(extra_dir):
        if not directory.is_dir():
            continue
        for path in sorted(directory.glob("*.json")):
            if path.stem in seen:
                continue
            seen.add(path.stem)
            try:
                data = json.loads(path.read_text(encoding="utf-8"))
                description = data.get("description", "")
                sheet_count = len(data.get("sheets", []))
            except (OSError, json.JSONDecodeError) as exc:
                description = f"(файл не читается: {exc})"
                sheet_count = 0
            found.append(
                {
                    "name": path.stem,
                    "path": str(path),
                    "description": description,
                    "sheet_blocks": sheet_count,
                }
            )

    return found


def load_template(name_or_path: str, extra_dir: str | None = None) -> dict[str, Any]:
    """Читает шаблон по имени (из папки templates) либо по прямому пути."""
    candidates: list[Path] = []
    direct = Path(name_or_path)
    if direct.suffix == ".json":
        candidates.append(direct)

    for directory in template_search_dirs(extra_dir):
        candidates.append(directory / f"{Path(name_or_path).stem}.json")

    for candidate in candidates:
        if candidate.is_file():
            try:
                template = json.loads(candidate.read_text(encoding="utf-8"))
            except json.JSONDecodeError as exc:
                raise TemplateError(f"Шаблон {candidate} содержит некорректный JSON: {exc}") from exc

            problems = validate_template(template)
            if problems:
                raise TemplateError(
                    f"Шаблон {candidate} не прошёл проверку:\n- " + "\n- ".join(problems)
                )
            template.setdefault("name", candidate.stem)
            return template

    known = ", ".join(t["name"] for t in list_templates(extra_dir)) or "нет доступных шаблонов"
    raise TemplateError(f"Шаблон '{name_or_path}' не найден. Известные шаблоны: {known}")


def validate_template(template: Any) -> list[str]:
    """Возвращает список проблем; пустой список означает корректный шаблон."""
    problems: list[str] = []

    if not isinstance(template, dict):
        return ["Корень шаблона должен быть объектом JSON."]

    sheets = template.get("sheets")
    if not isinstance(sheets, list) or not sheets:
        return ["Обязательное поле 'sheets' должно быть непустым списком."]

    for index, sheet in enumerate(sheets):
        prefix = f"sheets[{index}]"
        if not isinstance(sheet, dict):
            problems.append(f"{prefix}: описание листа должно быть объектом.")
            continue

        if not sheet.get("name") and not sheet.get("number"):
            problems.append(f"{prefix}: нужно задать хотя бы 'name' или 'number'.")

        views = sheet.get("views", [])
        if not isinstance(views, list):
            problems.append(f"{prefix}.views: должно быть списком.")
            continue

        for view_index, view in enumerate(views):
            view_prefix = f"{prefix}.views[{view_index}]"
            if not isinstance(view, dict):
                problems.append(f"{view_prefix}: описание вида должно быть объектом.")
                continue

            kind = view.get("kind")
            if kind not in VIEW_KINDS:
                problems.append(
                    f"{view_prefix}.kind: '{kind}' не поддерживается. "
                    f"Допустимо: {', '.join(sorted(VIEW_KINDS))}."
                )

            if kind == "section":
                for field in ("origin", "direction"):
                    if field not in view:
                        problems.append(f"{view_prefix}: для разреза обязательно поле '{field}'.")

            if kind == "existing" and not view.get("view_name"):
                problems.append(f"{view_prefix}: для kind='existing' обязательно поле 'view_name'.")

            scale = view.get("scale")
            if scale is not None and (not isinstance(scale, int) or scale <= 0):
                problems.append(f"{view_prefix}.scale: масштаб должен быть целым числом больше нуля.")

    export = template.get("export")
    if export is not None:
        if not isinstance(export, dict):
            problems.append("export: должно быть объектом.")
        elif export.get("format", "pdf") not in {"pdf", "dwg"}:
            problems.append("export.format: поддерживаются только 'pdf' и 'dwg'.")

    return problems


def render(text: str, context: dict[str, Any]) -> str:
    """Подставляет значения контекста в строку шаблона, не падая на лишних скобках."""
    if not isinstance(text, str):
        return text
    try:
        return text.format(**context)
    except (KeyError, IndexError, ValueError):
        return text


def expand(
    template: dict[str, Any],
    levels: list[dict[str, Any]],
    context: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    """Раскрывает шаблон в плоский список листов с подставленными именами и номерами.

    Блок с "for_each_level": true превращается в лист на каждый уровень модели.
    """
    base_context: dict[str, Any] = {
        "date": date.today().isoformat(),
        "project_name": "",
        "level_name": "",
        "level_index": 0,
        "level_elevation_mm": 0,
    }
    base_context.update(context or {})

    numbering = template.get("numbering", {})
    prefix = numbering.get("prefix", "")
    digits = int(numbering.get("digits", 2))
    counter = int(numbering.get("start", 1))

    common_parameters = template.get("sheet_parameters", {})
    result: list[dict[str, Any]] = []

    for block in template["sheets"]:
        if block.get("for_each_level"):
            targets = _select_levels(levels, block.get("levels"))
            if not targets:
                continue
        else:
            targets = [None]

        for level_index, level in enumerate(targets):
            item_context = dict(base_context)
            if level is not None:
                item_context.update(
                    {
                        "level_name": level.get("name", ""),
                        "level_index": level_index + 1,
                        "level_elevation_mm": level.get("elevation_mm", 0),
                    }
                )
            item_context["index"] = counter
            item_context["auto_number"] = f"{prefix}{counter:0{digits}d}"

            number = render(block.get("number") or "{auto_number}", item_context)
            name = render(block.get("name") or number, item_context)

            parameters = {
                key: render(value, item_context)
                for key, value in {**common_parameters, **block.get("parameters", {})}.items()
            }

            result.append(
                {
                    "number": number,
                    "name": name,
                    "level": level,
                    "parameters": parameters,
                    "titleblock": block.get("titleblock", template.get("titleblock")),
                    "views": [_expand_view(view, item_context, level) for view in block.get("views", [])],
                }
            )
            counter += 1

    return result


def _select_levels(levels: list[dict[str, Any]], wanted: Any) -> list[dict[str, Any]]:
    if not wanted:
        return list(levels)

    names = {str(name).strip().lower() for name in wanted}
    return [level for level in levels if str(level.get("name", "")).strip().lower() in names]


def _expand_view(
    view: dict[str, Any], context: dict[str, Any], level: dict[str, Any] | None
) -> dict[str, Any]:
    expanded = dict(view)
    for field in ("name", "view_name", "view_template"):
        if field in expanded:
            expanded[field] = render(expanded[field], context)

    if level is not None:
        expanded.setdefault("level_id", level.get("id"))

    return expanded


def auto_positions(
    count: int,
    sheet_width_mm: float,
    sheet_height_mm: float,
    margin_mm: float = 20.0,
    columns: int | None = None,
) -> list[dict[str, float]]:
    """Раскладка видов по листу сеткой — используется, когда в шаблоне нет явных координат.

    Возвращает координаты центров видовых экранов в системе листа (начало — левый нижний угол).
    """
    if count <= 0:
        return []

    columns = columns or max(1, round((count * sheet_width_mm / sheet_height_mm) ** 0.5))
    columns = min(columns, count)
    rows = -(-count // columns)  # округление вверх

    usable_width = max(sheet_width_mm - 2 * margin_mm, 1.0)
    usable_height = max(sheet_height_mm - 2 * margin_mm, 1.0)
    cell_width = usable_width / columns
    cell_height = usable_height / rows

    positions: list[dict[str, float]] = []
    for index in range(count):
        column = index % columns
        row = index // columns
        positions.append(
            {
                "x": margin_mm + cell_width * (column + 0.5),
                # Первый вид ставим сверху: листы читают сверху вниз.
                "y": sheet_height_mm - margin_mm - cell_height * (row + 0.5),
            }
        )

    return positions
