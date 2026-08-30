import json

import pytest

from revit_mcp import templates as tpl

LEVELS = [
    {"id": 101, "name": "Этаж 1", "elevation_mm": 0.0},
    {"id": 102, "name": "Этаж 2", "elevation_mm": 3300.0},
    {"id": 103, "name": "Кровля", "elevation_mm": 6600.0},
]


def test_shipped_templates_are_valid():
    shipped = tpl.list_templates()
    assert shipped, "в папке templates должен лежать хотя бы один шаблон"

    for item in shipped:
        definition = json.loads(open(item["path"], encoding="utf-8").read())
        assert tpl.validate_template(definition) == [], f"{item['name']}: {tpl.validate_template(definition)}"


def test_load_template_by_name():
    definition = tpl.load_template("obmer-ar")
    assert definition["name"] == "obmer-ar"
    assert definition["sheets"]


def test_load_template_unknown_name_lists_alternatives():
    with pytest.raises(tpl.TemplateError) as exc:
        tpl.load_template("несуществующий-шаблон")

    assert "obmer-ar" in str(exc.value)


def test_validate_reports_bad_view_kind():
    problems = tpl.validate_template(
        {"sheets": [{"name": "Лист", "views": [{"kind": "аксонометрия"}]}]}
    )
    assert any("kind" in problem for problem in problems)


def test_validate_requires_section_geometry():
    problems = tpl.validate_template(
        {"sheets": [{"name": "Лист", "views": [{"kind": "section"}]}]}
    )
    assert any("origin" in problem for problem in problems)
    assert any("direction" in problem for problem in problems)


def test_validate_accepts_minimal_template():
    assert tpl.validate_template({"sheets": [{"name": "Лист", "views": []}]}) == []


def test_expand_creates_sheet_per_level():
    definition = tpl.load_template("plany-po-etazham")
    sheets = tpl.expand(definition, LEVELS)

    assert len(sheets) == 3
    assert [sheet["number"] for sheet in sheets] == ["П-01", "П-02", "П-03"]
    assert sheets[1]["name"] == "План этажа Этаж 2"
    assert sheets[1]["views"][0]["level_id"] == 102


def test_expand_numbers_mixed_blocks_continuously():
    definition = tpl.load_template("obmer-ar")
    sheets = tpl.expand(definition, LEVELS)

    # три плана по уровням + разрезы + общий вид
    assert [sheet["number"] for sheet in sheets] == ["АР-01", "АР-02", "АР-03", "АР-04", "АР-05"]
    assert sheets[-1]["views"][0]["kind"] == "3d"


def test_expand_substitutes_context_into_parameters():
    definition = {
        "sheets": [{"name": "Лист {project_name}", "parameters": {"Объект": "{project_name}"}}],
        "numbering": {"prefix": "К-", "start": 7, "digits": 3},
    }

    sheets = tpl.expand(definition, LEVELS, {"project_name": "Склад №4"})

    assert sheets[0]["number"] == "К-007"
    assert sheets[0]["name"] == "Лист Склад №4"
    assert sheets[0]["parameters"]["Объект"] == "Склад №4"


def test_expand_filters_levels_by_name():
    definition = {
        "sheets": [{"for_each_level": True, "levels": ["Кровля"], "name": "План {level_name}"}]
    }

    sheets = tpl.expand(definition, LEVELS)

    assert len(sheets) == 1
    assert sheets[0]["name"] == "План Кровля"


def test_render_keeps_unknown_placeholders_intact():
    assert tpl.render("Лист {неизвестно}", {"date": "2026-01-01"}) == "Лист {неизвестно}"
