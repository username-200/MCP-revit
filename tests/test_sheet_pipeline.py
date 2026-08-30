"""Проверка сборки листов без Revit: мост подменяется журналом вызовов."""

import asyncio

import pytest

from revit_mcp.client import RevitBridgeError
from revit_mcp.server import _Catalog, _create_view, _place_views

CATALOG_RESPONSES = {
    "types.list": {
        "types": [{"id": 900, "name": "A1 метрический", "family": "Рамка"}]
    },
    "views.templates.list": {"view_templates": [{"id": 800, "name": "Обмерный план"}]},
    "views.list": {"views": [{"id": 700, "name": "3D вид {3D}"}]},
}


def build_catalog() -> _Catalog:
    async def call(command, params=None):
        return CATALOG_RESPONSES[command]

    return asyncio.run(_Catalog.build(call))


class Recorder:
    def __init__(self, responses=None, failing=()):
        self.calls = []
        self.responses = responses or {}
        self.failing = set(failing)

    async def __call__(self, command, params=None, timeout=None):
        self.calls.append((command, params))
        if command in self.failing:
            raise RevitBridgeError(f"команда {command} отклонена", kind="bad_request")
        return self.responses.get(command, {"id": 1, "name": "вид"})


def test_catalog_matches_titleblock_by_short_and_full_name():
    catalog = build_catalog()

    assert catalog.titleblock_id("A1 метрический") == 900
    assert catalog.titleblock_id("Рамка: A1 метрический") == 900
    assert catalog.titleblock_id(None) is None


def test_catalog_reports_missing_titleblock():
    catalog = build_catalog()

    with pytest.raises(ValueError) as exc:
        catalog.titleblock_id("A0 расширенный")

    assert "A1 метрический" in str(exc.value)


def test_create_plan_view_passes_level_and_template():
    catalog = build_catalog()
    recorder = Recorder(responses={"views.create_plan": {"id": 11, "name": "План"}})

    asyncio.run(
        _create_view(
            {"kind": "plan", "name": "План 1", "scale": 50, "view_template": "Обмерный план", "level_id": 5},
            catalog,
            recorder,
        )
    )

    command, params = recorder.calls[0]
    assert command == "views.create_plan"
    assert params["level_id"] == 5
    assert params["view_template_id"] == 800
    assert params["view_family"] == "FloorPlan"


def test_create_plan_without_level_is_rejected():
    catalog = build_catalog()

    with pytest.raises(ValueError) as exc:
        asyncio.run(_create_view({"kind": "plan", "name": "План"}, catalog, Recorder()))

    assert "уровень" in str(exc.value).lower()


def test_elevation_uses_section_command_with_elevation_family():
    catalog = build_catalog()
    recorder = Recorder(responses={"views.create_section": {"id": 12, "name": "Фасад"}})

    asyncio.run(
        _create_view(
            {
                "kind": "elevation",
                "origin": {"x": 0, "y": 0},
                "direction": {"x": 0, "y": 1, "z": 0},
            },
            catalog,
            recorder,
        )
    )

    command, params = recorder.calls[0]
    assert command == "views.create_section"
    assert params["view_family"] == "Elevation"


def test_existing_view_is_reused_not_created():
    catalog = build_catalog()
    recorder = Recorder()

    view = asyncio.run(
        _create_view({"kind": "existing", "view_name": "3D вид {3D}"}, catalog, recorder)
    )

    assert view["id"] == 700
    assert recorder.calls == []


def test_views_without_position_are_laid_out_on_the_sheet():
    recorder = Recorder(
        responses={"sheets.info": {"titleblock_size_mm": {"width": 841.0, "height": 594.0}}}
    )
    entry = {"problems": []}

    asyncio.run(
        _place_views(
            sheet_id=50,
            placements=[({"kind": "plan"}, 11), ({"kind": "3d"}, 12)],
            call=recorder,
            entry=entry,
        )
    )

    placements = [params for command, params in recorder.calls if command == "sheets.place_view"]
    assert len(placements) == 2
    assert all(0 < params["center"]["x"] < 841 for params in placements)
    assert entry["problems"] == []


def test_explicit_position_from_template_wins():
    recorder = Recorder(
        responses={"sheets.info": {"titleblock_size_mm": {"width": 841.0, "height": 594.0}}}
    )
    entry = {"problems": []}

    asyncio.run(
        _place_views(
            sheet_id=50,
            placements=[({"kind": "plan", "position": {"x_mm": 300, "y_mm": 200}}, 11)],
            call=recorder,
            entry=entry,
        )
    )

    _, params = recorder.calls[-1]
    assert params["center"] == {"x": 300, "y": 200, "z": 0}


def test_failed_placement_is_reported_not_raised():
    recorder = Recorder(
        responses={"sheets.info": {"titleblock_size_mm": {"width": 841.0, "height": 594.0}}},
        failing={"sheets.place_view"},
    )
    entry = {"problems": []}

    asyncio.run(_place_views(50, [({"kind": "plan"}, 11)], recorder, entry))

    assert len(entry["problems"]) == 1
    assert "не размещён" in entry["problems"][0]
