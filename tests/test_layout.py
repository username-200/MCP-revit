from revit_mcp.templates import auto_positions

A1_WIDTH, A1_HEIGHT = 841.0, 594.0


def test_no_views_gives_no_positions():
    assert auto_positions(0, A1_WIDTH, A1_HEIGHT) == []


def test_single_view_is_centred():
    (position,) = auto_positions(1, A1_WIDTH, A1_HEIGHT, margin_mm=20)

    assert position["x"] == A1_WIDTH / 2
    assert position["y"] == A1_HEIGHT / 2


def test_positions_stay_inside_sheet():
    for count in range(1, 13):
        for position in auto_positions(count, A1_WIDTH, A1_HEIGHT, margin_mm=20):
            assert 20 <= position["x"] <= A1_WIDTH - 20
            assert 20 <= position["y"] <= A1_HEIGHT - 20


def test_views_are_ordered_top_down_then_left_right():
    positions = auto_positions(4, A1_WIDTH, A1_HEIGHT, margin_mm=20, columns=2)

    assert positions[0]["y"] > positions[2]["y"]  # первый ряд выше второго
    assert positions[0]["x"] < positions[1]["x"]  # внутри ряда слева направо


def test_column_count_never_exceeds_view_count():
    positions = auto_positions(2, A1_WIDTH, A1_HEIGHT, columns=8)
    assert len({round(position["y"], 6) for position in positions}) == 1
