"""MCP-сервер Revit: облако точек → модель → чертежи по шаблону."""

from __future__ import annotations

import json
import os
from typing import Any

from . import templates as tpl
from .client import RevitBridgeError, RevitClient

try:  # mcp >= 2.0
    from mcp.server.mcpserver import MCPServer as _Server
except ImportError:  # mcp 1.x
    from mcp.server.fastmcp import FastMCP as _Server  # type: ignore[assignment]

INSTRUCTIONS = """
Сервер управляет открытым проектом Autodesk Revit через локальный аддин-мост.

Типовой сценарий обмерных работ:
  1. revit_status — убедиться, что мост отвечает и проект открыт;
  2. revit_link_point_cloud — подключить .rcp/.rcs (файлы .e57/.las конвертируются в ReCap);
  3. revit_levels_from_point_cloud — поднять уровни по горизонтальным плоскостям;
  4. revit_walls_from_point_cloud — построить стены по вертикальным плоскостям;
  5. revit_apply_sheet_template — выпустить комплект листов по шаблону и выгрузить PDF/DWG.

Все длины на входе и выходе — миллиметры. Идентификаторы элементов — числа Revit ElementId.
""".strip()

# Формат A1 — запасной размер, если у листа не оказалось рамки с габаритами.
DEFAULT_SHEET_SIZE_MM = {"width": 841.0, "height": 594.0}

server = _Server(name="revit-mcp", instructions=INSTRUCTIONS)
client = RevitClient()


def _fail(exc: RevitBridgeError) -> dict[str, Any]:
    return {"ok": False, "error_kind": exc.kind, "error": str(exc)}


async def _call(command: str, params: dict[str, Any] | None = None, timeout: float | None = None) -> Any:
    return await client.call(command, params, timeout=timeout)


# --------------------------------------------------------------------------- состояние


@server.tool(description="Проверить связь с Revit и получить сведения об открытом проекте.")
async def revit_status() -> dict[str, Any]:
    try:
        health = await client.health()
    except RevitBridgeError as exc:
        return _fail(exc)

    result: dict[str, Any] = {"ok": True, "bridge": health, "url": client.base_url}
    try:
        result["document"] = await _call("document.info")
    except RevitBridgeError as exc:
        result["document"] = None
        result["document_error"] = str(exc)

    return result


@server.tool(description="Список уровней проекта с отметками в миллиметрах.")
async def revit_list_levels() -> dict[str, Any]:
    try:
        return {"ok": True, **await _call("levels.list")}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(description="Создать уровень на заданной отметке (мм).")
async def revit_create_level(elevation_mm: float, name: str | None = None) -> dict[str, Any]:
    try:
        result = await _call("levels.create", {"elevation_mm": elevation_mm, "name": name})
        return {"ok": True, "level": result}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Список типоразмеров проекта. kind: wall, floor, roof, ceiling, column, "
        "titleblock, viewfamilytype."
    )
)
async def revit_list_types(kind: str = "wall") -> dict[str, Any]:
    try:
        return {"ok": True, **await _call("types.list", {"kind": kind})}
    except RevitBridgeError as exc:
        return _fail(exc)


# --------------------------------------------------------------------------- облако точек


@server.tool(description="Список облаков точек, подключённых к проекту, с их габаритами.")
async def revit_list_point_clouds() -> dict[str, Any]:
    try:
        return {"ok": True, **await _call("pointcloud.list")}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Подключить облако точек (.rcp или .rcs) к проекту. Смещение и поворот задают "
        "положение облака относительно начала координат проекта."
    )
)
async def revit_link_point_cloud(
    path: str,
    offset_x_mm: float = 0,
    offset_y_mm: float = 0,
    offset_z_mm: float = 0,
    rotation_deg: float = 0,
) -> dict[str, Any]:
    try:
        result = await _call(
            "pointcloud.link",
            {
                "path": path,
                "offset_x_mm": offset_x_mm,
                "offset_y_mm": offset_y_mm,
                "offset_z_mm": offset_z_mm,
                "rotation_deg": rotation_deg,
            },
            timeout=600.0,
        )
        return {"ok": True, "point_cloud": result}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Обзор облака точек: габариты и распределение точек по высоте. "
        "Пики гистограммы соответствуют полам и потолкам — по ним удобно назначать уровни."
    )
)
async def revit_survey_point_cloud(
    point_cloud_id: int,
    max_points: int = 20000,
    bin_size_mm: float = 250.0,
) -> dict[str, Any]:
    try:
        sample = await _call(
            "pointcloud.sample",
            {"id": point_cloud_id, "max_points": max_points},
            timeout=300.0,
        )
    except RevitBridgeError as exc:
        return _fail(exc)

    points = sample.get("points", [])
    if not points:
        return {"ok": False, "error": "Облако вернуло ноль точек — проверьте область выборки."}

    zs = [p["z"] for p in points]
    histogram: dict[int, int] = {}
    for z in zs:
        histogram[int(z // bin_size_mm)] = histogram.get(int(z // bin_size_mm), 0) + 1

    peaks = sorted(histogram.items(), key=lambda item: item[1], reverse=True)[:10]

    return {
        "ok": True,
        "sampled_points": len(points),
        "bounds_mm": {
            "min": {axis: min(p[axis] for p in points) for axis in "xyz"},
            "max": {axis: max(p[axis] for p in points) for axis in "xyz"},
        },
        "elevation_peaks": [
            {"elevation_mm": round(index * bin_size_mm, 1), "points": count} for index, count in peaks
        ],
    }


@server.tool(
    description=(
        "Найти плоскости в облаке точек методом RANSAC. Возвращает горизонтальные "
        "(полы и перекрытия), вертикальные (стены, со следом в плане) и наклонные плоскости. "
        "filter_kind: horizontal, vertical, sloped."
    )
)
async def revit_detect_planes(
    point_cloud_id: int,
    max_points: int = 40000,
    distance_tolerance_mm: float = 25.0,
    max_planes: int = 12,
    min_inliers: int = 200,
    filter_kind: str | None = None,
) -> dict[str, Any]:
    try:
        result = await _call(
            "pointcloud.detect_planes",
            {
                "id": point_cloud_id,
                "max_points": max_points,
                "distance_tolerance_mm": distance_tolerance_mm,
                "max_planes": max_planes,
                "min_inliers": min_inliers,
                "filter_kind": filter_kind,
            },
            timeout=600.0,
        )
        return {"ok": True, **result}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Создать уровни по горизонтальным плоскостям облака точек. "
        "Плоскости ближе min_spacing_mm друг к другу объединяются, "
        "уже существующие отметки пропускаются."
    )
)
async def revit_levels_from_point_cloud(
    point_cloud_id: int,
    max_points: int = 40000,
    distance_tolerance_mm: float = 25.0,
    min_spacing_mm: float = 1500.0,
    name_prefix: str = "Уровень обмера",
    dry_run: bool = False,
) -> dict[str, Any]:
    try:
        detected = await _call(
            "pointcloud.detect_planes",
            {
                "id": point_cloud_id,
                "max_points": max_points,
                "distance_tolerance_mm": distance_tolerance_mm,
                "filter_kind": "horizontal",
            },
            timeout=600.0,
        )
        existing = (await _call("levels.list"))["levels"]
    except RevitBridgeError as exc:
        return _fail(exc)

    candidates = sorted(
        (plane["elevation_mm"] for plane in detected.get("planes", [])),
    )

    merged: list[float] = []
    for elevation in candidates:
        if merged and abs(elevation - merged[-1]) < min_spacing_mm:
            continue
        if any(abs(elevation - level["elevation_mm"]) < min_spacing_mm for level in existing):
            continue
        merged.append(elevation)

    if dry_run:
        return {"ok": True, "dry_run": True, "proposed_elevations_mm": merged}

    created, failed = [], []
    for index, elevation in enumerate(merged, start=1):
        try:
            created.append(
                await _call(
                    "levels.create",
                    {"elevation_mm": elevation, "name": f"{name_prefix} {index}"},
                )
            )
        except RevitBridgeError as exc:
            failed.append({"elevation_mm": elevation, "reason": str(exc)})

    return {"ok": True, "created": created, "failed": failed, "detected_planes": len(candidates)}


@server.tool(
    description=(
        "Построить стены по вертикальным плоскостям облака точек. "
        "snap_angle_deg=90 выравнивает оси стен по ортогональной сетке; "
        "dry_run возвращает предполагаемые оси без изменения модели."
    )
)
async def revit_walls_from_point_cloud(
    point_cloud_id: int,
    level_id: int,
    wall_type_id: int | None = None,
    max_points: int = 40000,
    distance_tolerance_mm: float = 25.0,
    max_planes: int = 20,
    min_length_mm: float = 800.0,
    snap_angle_deg: float = 90.0,
    height_mm: float | None = None,
    dry_run: bool = False,
) -> dict[str, Any]:
    try:
        detected = await _call(
            "pointcloud.detect_planes",
            {
                "id": point_cloud_id,
                "max_points": max_points,
                "distance_tolerance_mm": distance_tolerance_mm,
                "max_planes": max_planes,
                "filter_kind": "vertical",
            },
            timeout=600.0,
        )
    except RevitBridgeError as exc:
        return _fail(exc)

    planes = detected.get("planes", [])
    if not planes:
        return {
            "ok": False,
            "error": (
                "Вертикальных плоскостей не найдено. Увеличьте max_points или "
                "distance_tolerance_mm, либо ограничьте выборку одним этажом."
            ),
        }

    if dry_run:
        return {
            "ok": True,
            "dry_run": True,
            "candidate_walls": [
                {
                    "length_mm": round(plane["trace"]["length_mm"], 1),
                    "height_mm": round(plane["max_z_mm"] - plane["min_z_mm"], 1),
                    "heading_deg": round(plane["heading_deg"], 2),
                    "points": plane["inlier_count"],
                }
                for plane in planes
                if plane.get("trace") and plane["trace"]["length_mm"] >= min_length_mm
            ],
        }

    params: dict[str, Any] = {
        "planes": planes,
        "level_id": level_id,
        "min_length_mm": min_length_mm,
        "snap_angle_deg": snap_angle_deg,
    }
    if wall_type_id is not None:
        params["wall_type_id"] = wall_type_id
    if height_mm:
        params["height_mm"] = height_mm

    try:
        result = await _call("walls.from_planes", params, timeout=600.0)
        return {"ok": True, **result}
    except RevitBridgeError as exc:
        return _fail(exc)


# --------------------------------------------------------------------------- модель


@server.tool(
    description=(
        "Создать стены по осевым линиям. segments — список "
        "[{\"start\": {\"x\": 0, \"y\": 0}, \"end\": {\"x\": 5000, \"y\": 0}}] в миллиметрах."
    )
)
async def revit_create_walls(
    level_id: int,
    segments: list[dict[str, Any]],
    height_mm: float = 3000,
    wall_type_id: int | None = None,
    structural: bool = False,
) -> dict[str, Any]:
    params: dict[str, Any] = {
        "level_id": level_id,
        "segments": segments,
        "height_mm": height_mm,
        "structural": structural,
    }
    if wall_type_id is not None:
        params["wall_type_id"] = wall_type_id

    try:
        return {"ok": True, **await _call("walls.create", params)}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Создать перекрытие по замкнутому контуру. boundary — список точек "
        "[{\"x\": 0, \"y\": 0}, ...] в миллиметрах."
    )
)
async def revit_create_floor(
    level_id: int,
    boundary: list[dict[str, Any]],
    floor_type_id: int | None = None,
    offset_mm: float = 0,
) -> dict[str, Any]:
    params: dict[str, Any] = {
        "level_id": level_id,
        "boundary": boundary,
        "offset_mm": offset_mm,
    }
    if floor_type_id is not None:
        params["floor_type_id"] = floor_type_id

    try:
        return {"ok": True, "floor": await _call("floors.create", params)}
    except RevitBridgeError as exc:
        return _fail(exc)


# --------------------------------------------------------------------------- виды и листы


@server.tool(description="Список видов проекта.")
async def revit_list_views(placeable_only: bool = False) -> dict[str, Any]:
    try:
        return {"ok": True, **await _call("views.list", {"placeable_only": placeable_only})}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(description="Список листов проекта.")
async def revit_list_sheets() -> dict[str, Any]:
    try:
        return {"ok": True, **await _call("sheets.list")}
    except RevitBridgeError as exc:
        return _fail(exc)


# --------------------------------------------------------------------------- шаблоны чертежей


@server.tool(description="Список доступных шаблонов комплектов чертежей.")
async def revit_list_sheet_templates() -> dict[str, Any]:
    return {"ok": True, "templates": tpl.list_templates()}


@server.tool(
    description=(
        "Проверить шаблон комплекта чертежей и показать, какие листы он создаст "
        "для текущего проекта, ничего не изменяя."
    )
)
async def revit_preview_sheet_template(template: str) -> dict[str, Any]:
    try:
        definition = tpl.load_template(template)
    except tpl.TemplateError as exc:
        return {"ok": False, "error": str(exc)}

    try:
        levels = (await _call("levels.list"))["levels"]
        document = await _call("document.info")
    except RevitBridgeError as exc:
        return _fail(exc)

    sheets = tpl.expand(definition, levels, {"project_name": document.get("title", "")})

    return {
        "ok": True,
        "template": definition.get("name", template),
        "sheet_count": len(sheets),
        "sheets": [
            {
                "number": sheet["number"],
                "name": sheet["name"],
                "level": (sheet["level"] or {}).get("name"),
                "views": [view.get("kind") for view in sheet["views"]],
            }
            for sheet in sheets
        ],
    }


@server.tool(
    description=(
        "Выпустить комплект чертежей по шаблону: создать виды, листы, разместить виды, "
        "заполнить штамп и при необходимости выгрузить PDF или DWG. "
        "export_folder переопределяет папку выгрузки из шаблона."
    )
)
async def revit_apply_sheet_template(
    template: str,
    export: bool = True,
    export_folder: str | None = None,
) -> dict[str, Any]:
    try:
        definition = tpl.load_template(template)
    except tpl.TemplateError as exc:
        return {"ok": False, "error": str(exc)}

    try:
        levels = (await _call("levels.list"))["levels"]
        document = await _call("document.info")
        catalog = await _Catalog.build(_call)
    except RevitBridgeError as exc:
        return _fail(exc)

    if not levels:
        return {"ok": False, "error": "В проекте нет уровней — сначала создайте их."}

    plan = tpl.expand(definition, levels, {"project_name": document.get("title", "")})
    report: list[dict[str, Any]] = []
    sheet_numbers: list[str] = []

    for spec in plan:
        entry: dict[str, Any] = {"number": spec["number"], "name": spec["name"], "views": [], "problems": []}

        placements: list[tuple[dict[str, Any], int]] = []
        for view_spec in spec["views"]:
            try:
                view = await _create_view(view_spec, catalog, _call)
                placements.append((view_spec, view["id"]))
                entry["views"].append({"id": view["id"], "name": view["name"], "kind": view_spec["kind"]})
            except (RevitBridgeError, ValueError) as exc:
                entry["problems"].append(f"вид {view_spec.get('kind')}: {exc}")

        try:
            sheet_params: dict[str, Any] = {"number": spec["number"], "name": spec["name"]}
            titleblock_id = catalog.titleblock_id(spec.get("titleblock"))
            if titleblock_id is not None:
                sheet_params["titleblock_id"] = titleblock_id
            if spec["parameters"]:
                sheet_params["parameters"] = spec["parameters"]

            sheet = await _call("sheets.create", sheet_params)
            entry["sheet_id"] = sheet["id"]
            entry["number"] = sheet["number"]
            sheet_numbers.append(sheet["number"])
        except RevitBridgeError as exc:
            entry["problems"].append(f"лист не создан: {exc}")
            report.append(entry)
            continue

        await _place_views(sheet["id"], placements, _call, entry)
        report.append(entry)

    result: dict[str, Any] = {
        "ok": True,
        "template": definition.get("name", template),
        "sheets_created": len([item for item in report if "sheet_id" in item]),
        "sheets": report,
    }

    export_settings = definition.get("export") or {}
    folder = export_folder or export_settings.get("folder")
    if export and folder and sheet_numbers:
        command = "export.dwg" if export_settings.get("format") == "dwg" else "export.pdf"
        try:
            result["export"] = await _call(
                command,
                {
                    "folder": folder,
                    "sheet_numbers": sheet_numbers,
                    "combine": export_settings.get("combine", True),
                    "file_name": export_settings.get("file_name"),
                },
                timeout=900.0,
            )
        except RevitBridgeError as exc:
            result["export"] = {"ok": False, "error": str(exc)}
    elif export and not folder:
        result["export"] = {
            "skipped": "Папка выгрузки не задана: укажите export_folder или блок 'export' в шаблоне."
        }

    return result


@server.tool(
    description=(
        "Выгрузить листы в PDF или DWG. Без sheet_numbers выгружаются все листы проекта."
    )
)
async def revit_export_sheets(
    folder: str,
    fmt: str = "pdf",
    sheet_numbers: list[str] | None = None,
    combine: bool = True,
    file_name: str | None = None,
) -> dict[str, Any]:
    if fmt not in {"pdf", "dwg"}:
        return {"ok": False, "error": "Поддерживаются только форматы 'pdf' и 'dwg'."}

    params: dict[str, Any] = {"folder": folder, "combine": combine, "file_name": file_name}
    if sheet_numbers:
        params["sheet_numbers"] = sheet_numbers

    try:
        return {"ok": True, **await _call(f"export.{fmt}", params, timeout=900.0)}
    except RevitBridgeError as exc:
        return _fail(exc)


@server.tool(
    description=(
        "Выполнить произвольную команду моста — на случай, если готового инструмента нет. "
        "Список команд доступен на http://127.0.0.1:8765/commands."
    )
)
async def revit_raw_command(command: str, params_json: str = "{}") -> dict[str, Any]:
    try:
        params = json.loads(params_json)
    except json.JSONDecodeError as exc:
        return {"ok": False, "error": f"params_json не является корректным JSON: {exc}"}

    try:
        return {"ok": True, "result": await _call(command, params)}
    except RevitBridgeError as exc:
        return _fail(exc)


# --------------------------------------------------------------------------- внутреннее


class _Catalog:
    """Справочники проекта: рамки, шаблоны видов, существующие виды — по именам."""

    def __init__(
        self,
        titleblocks: dict[str, int],
        titleblock_names: list[str],
        view_templates: dict[str, int],
        views: dict[str, int],
    ) -> None:
        self.titleblocks = titleblocks
        self.titleblock_names = titleblock_names
        self.view_templates = view_templates
        self.views = views

    @classmethod
    async def build(cls, call) -> "_Catalog":
        titleblocks = await call("types.list", {"kind": "titleblock"})
        view_templates = await call("views.templates.list")
        views = await call("views.list", {"placeable_only": True})

        # Рамку можно назвать и коротко («A1 метрический»), и полно («Рамка: A1 метрический»).
        lookup: dict[str, int] = {}
        names: list[str] = []
        for item in titleblocks["types"]:
            full = f"{item['family']}: {item['name']}"
            lookup[item["name"].lower()] = item["id"]
            lookup[full.lower()] = item["id"]
            names.append(full)

        return cls(
            titleblocks=lookup,
            titleblock_names=names,
            view_templates={item["name"].lower(): item["id"] for item in view_templates["view_templates"]},
            views={item["name"].lower(): item["id"] for item in views["views"]},
        )

    def titleblock_id(self, name: str | None) -> int | None:
        if not name:
            return None
        found = self.titleblocks.get(name.lower())
        if found is None:
            raise ValueError(
                f"Основная надпись '{name}' не найдена. Доступны: "
                + ", ".join(sorted(self.titleblock_names)[:20])
            )
        return found

    def view_template_id(self, name: str | None) -> int | None:
        if not name:
            return None
        return self.view_templates.get(name.lower())


async def _create_view(spec: dict[str, Any], catalog: _Catalog, call) -> dict[str, Any]:
    kind = spec["kind"]

    if kind == "existing":
        view_id = catalog.views.get(str(spec["view_name"]).lower())
        if view_id is None:
            raise ValueError(f"Вид '{spec['view_name']}' в проекте не найден.")
        return {"id": view_id, "name": spec["view_name"]}

    common: dict[str, Any] = {"name": spec.get("name"), "scale": spec.get("scale")}
    template_id = catalog.view_template_id(spec.get("view_template"))
    if template_id is not None:
        common["view_template_id"] = template_id
    if spec.get("detail_level"):
        common["detail_level"] = spec["detail_level"]

    if kind in {"plan", "ceiling_plan"}:
        if spec.get("level_id") is None:
            raise ValueError("Для плана нужен уровень: используйте 'for_each_level' или задайте level_id.")
        return await call(
            "views.create_plan",
            {
                **common,
                "level_id": spec["level_id"],
                "view_family": "CeilingPlan" if kind == "ceiling_plan" else "FloorPlan",
            },
        )

    if kind in {"section", "elevation"}:
        return await call(
            "views.create_section",
            {
                **common,
                "view_family": "Elevation" if kind == "elevation" else "Section",
                "origin": spec["origin"],
                "direction": spec["direction"],
                "width_mm": spec.get("width_mm", 10000),
                "height_mm": spec.get("height_mm", 4000),
                "depth_mm": spec.get("depth_mm", 10000),
            },
        )

    if kind == "3d":
        return await call("views.create_3d", {**common, "perspective": spec.get("perspective", False)})

    raise ValueError(f"Неизвестный тип вида '{kind}'.")


async def _place_views(
    sheet_id: int,
    placements: list[tuple[dict[str, Any], int]],
    call,
    entry: dict[str, Any],
) -> None:
    """Размещает виды на листе: по явным координатам шаблона либо автоматической сеткой."""
    if not placements:
        return

    try:
        info = await call("sheets.info", {"sheet_id": sheet_id})
        size = info.get("titleblock_size_mm") or DEFAULT_SHEET_SIZE_MM
    except RevitBridgeError:
        size = DEFAULT_SHEET_SIZE_MM

    auto = tpl.auto_positions(len(placements), size["width"], size["height"])

    for index, (spec, view_id) in enumerate(placements):
        position = spec.get("position") or {}
        center = {
            "x": position.get("x_mm", auto[index]["x"]),
            "y": position.get("y_mm", auto[index]["y"]),
            "z": 0,
        }

        try:
            await call("sheets.place_view", {"sheet_id": sheet_id, "view_id": view_id, "center": center})
        except RevitBridgeError as exc:
            entry["problems"].append(f"вид {view_id} не размещён: {exc}")


def main() -> None:
    transport = os.environ.get("REVIT_MCP_TRANSPORT", "stdio")
    server.run(transport=transport)


if __name__ == "__main__":
    main()
