"""Сквозная проверка выпуска комплекта: вместо Revit — поддельный мост на MockTransport."""

import asyncio
import json

import httpx
import pytest

from revit_mcp import server as srv


class FakeBridge:
    """Минимальная модель моста: помнит созданные виды, листы и вызовы экспорта."""

    def __init__(self, failing: set[str] | None = None) -> None:
        self.failing = failing or set()
        self.next_id = 1000
        self.created_views: list[dict] = []
        self.created_sheets: list[dict] = []
        self.placements: list[dict] = []
        self.exports: list[dict] = []

    def _new_id(self) -> int:
        self.next_id += 1
        return self.next_id

    def handle(self, request: httpx.Request) -> httpx.Response:
        body = json.loads(request.read())
        command, params = body["command"], body["params"]

        if command in self.failing:
            return httpx.Response(
                400, json={"ok": False, "error": {"type": "bad_request", "message": f"{command} отклонена"}}
            )

        return httpx.Response(200, json={"ok": True, "result": self.dispatch(command, params)})

    def dispatch(self, command: str, params: dict):
        if command == "levels.list":
            return {
                "levels": [
                    {"id": 101, "name": "Этаж 1", "elevation_mm": 0.0},
                    {"id": 102, "name": "Этаж 2", "elevation_mm": 3300.0},
                ]
            }
        if command == "document.info":
            return {"title": "Склад №4"}
        if command == "types.list":
            return {"types": [{"id": 900, "name": "A1 метрический", "family": "Рамка"}]}
        if command == "views.templates.list":
            return {"view_templates": []}
        if command == "views.list":
            return {"views": []}
        if command.startswith("views.create_"):
            view = {"id": self._new_id(), "name": params.get("name") or "вид", "params": params}
            self.created_views.append(view)
            return view
        if command == "sheets.create":
            sheet = {"id": self._new_id(), "number": params["number"], "name": params["name"], "params": params}
            self.created_sheets.append(sheet)
            return sheet
        if command == "sheets.info":
            return {"titleblock_size_mm": {"width": 841.0, "height": 594.0}}
        if command == "sheets.place_view":
            self.placements.append(params)
            return {"viewport_id": self._new_id()}
        if command == "export.pdf":
            self.exports.append(params)
            return {"format": "pdf", "files": ["/выгрузка/Обмерный комплект.pdf"]}

        raise AssertionError(f"поддельный мост не знает команду {command}")


@pytest.fixture
def bridge():
    fake = FakeBridge()
    srv.client._client = httpx.AsyncClient(
        base_url="http://127.0.0.1:8765", transport=httpx.MockTransport(fake.handle)
    )
    yield fake
    srv.client._client = None


def test_template_creates_sheet_and_view_per_level(bridge):
    result = asyncio.run(srv.revit_apply_sheet_template("plany-po-etazham", export=False))

    assert result["ok"] is True
    assert result["sheets_created"] == 2
    assert [sheet["number"] for sheet in bridge.created_sheets] == ["П-01", "П-02"]
    assert [view["params"]["level_id"] for view in bridge.created_views] == [101, 102]
    assert all(sheet["problems"] == [] for sheet in result["sheets"])


def test_view_names_and_sheet_parameters_use_project_context(bridge):
    asyncio.run(srv.revit_apply_sheet_template("obmer-ar", export=False))

    names = [view["name"] for view in bridge.created_views]
    assert "Обмерный план Этаж 2" in names

    parameters = bridge.created_sheets[0]["params"]["parameters"]
    assert parameters["Стадия"] == "Обмерные работы"
    assert parameters["Дата"] != "{date}"


def test_every_created_view_is_placed_on_its_sheet(bridge):
    asyncio.run(srv.revit_apply_sheet_template("obmer-ar", export=False))

    assert len(bridge.placements) == len(bridge.created_views)
    for placement in bridge.placements:
        assert 0 < placement["center"]["x"] < 841
        assert 0 < placement["center"]["y"] < 594


def test_export_folder_argument_overrides_template(bridge):
    result = asyncio.run(
        srv.revit_apply_sheet_template("obmer-ar", export=True, export_folder="/выгрузка")
    )

    assert bridge.exports[0]["folder"] == "/выгрузка"
    # два уровня → два плана, затем лист разрезов и лист общего вида
    assert bridge.exports[0]["sheet_numbers"] == ["АР-01", "АР-02", "АР-03", "АР-04"]
    assert result["export"]["files"]


def test_export_is_skipped_when_no_folder_known(bridge):
    result = asyncio.run(srv.revit_apply_sheet_template("plany-po-etazham", export=True))

    assert bridge.exports == []
    assert "skipped" in result["export"]


def test_unknown_template_reports_error_without_touching_revit(bridge):
    result = asyncio.run(srv.revit_apply_sheet_template("нет-такого"))

    assert result["ok"] is False
    assert bridge.created_sheets == []


def test_sheet_failure_is_reported_and_run_continues():
    fake = FakeBridge(failing={"views.create_3d"})
    srv.client._client = httpx.AsyncClient(
        base_url="http://127.0.0.1:8765", transport=httpx.MockTransport(fake.handle)
    )
    try:
        result = asyncio.run(srv.revit_apply_sheet_template("obmer-ar", export=False))
    finally:
        srv.client._client = None

    # Лист общего вида создан, но остался без вида — об этом сказано в отчёте.
    assert result["sheets_created"] == 4
    problems = [problem for sheet in result["sheets"] for problem in sheet["problems"]]
    assert any("views.create_3d" in problem for problem in problems)


def test_preview_does_not_modify_the_project(bridge):
    result = asyncio.run(srv.revit_preview_sheet_template("obmer-ar"))

    assert result["sheet_count"] == 4
    assert bridge.created_sheets == []
    assert bridge.created_views == []
