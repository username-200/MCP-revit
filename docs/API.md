# Команды моста

Мост принимает `POST http://127.0.0.1:8765/command`:

```json
{ "command": "levels.list", "params": {}, "timeout_sec": 120 }
```

Ответ:

```json
{ "ok": true, "result": { "levels": [ { "id": 312, "name": "Этаж 1", "elevation_mm": 0.0 } ] } }
```

Ошибка:

```json
{ "ok": false, "error": { "type": "not_found", "message": "Элемент 42 не найден." } }
```

Типы ошибок: `bad_request`, `not_found`, `no_document`, `wrong_document`, `unknown_command`,
`timeout`, `revit_error`, `export_failed`, `point_cloud_read_failed`, `unauthorized`.

Служебные маршруты: `GET /health`, `GET /commands`.

Все линейные величины — миллиметры, углы — градусы, идентификаторы — `ElementId` Revit.
Точки задаются объектом `{"x": 0, "y": 0, "z": 0}`.

## Документ

| Команда | Параметры | Результат |
| --- | --- | --- |
| `ping` | — | версия аддина и Revit |
| `document.info` | — | название, путь, число уровней и облаков |
| `document.save` | `path` (необяз.) | путь сохранённого файла |
| `levels.list` | — | уровни с отметками |
| `levels.create` | `elevation_mm`, `name` | созданный уровень |
| `types.list` | `kind`: `wall`, `floor`, `roof`, `ceiling`, `column`, `titleblock`, `viewfamilytype` | типоразмеры |
| `elements.delete` | `ids` | число удалённых |

## Облако точек

| Команда | Параметры | Результат |
| --- | --- | --- |
| `pointcloud.list` | — | облака с габаритами и путями |
| `pointcloud.link` | `path`, `offset_x_mm`, `offset_y_mm`, `offset_z_mm`, `rotation_deg` | подключённое облако |
| `pointcloud.sample` | `id`, `max_points`, `average_distance_mm`, `box` | точки в координатах модели |
| `pointcloud.detect_planes` | `id`, `max_points`, `distance_tolerance_mm`, `max_planes`, `min_inliers`, `min_inlier_ratio`, `trials`, `seed`, `angle_tolerance_deg`, `filter_kind`, `box` | найденные плоскости |

`box` ограничивает выборку: `{"min": {"x": …}, "max": {"x": …}}`. Без него берутся габариты
облака. Если `average_distance_mm` не задан, шаг подбирается так, чтобы запрошенное число точек
покрыло весь объём, а не плотный участок в углу.

Каждая плоскость содержит `kind` (`horizontal`, `vertical`, `sloped`), `normal`, `centroid`,
`inlier_count`, `rmse_mm`, `min_z_mm`, `max_z_mm`, `elevation_mm`, `heading_deg`, а для
вертикальных — `trace` со следом стены в плане (`start`, `end`, `length_mm`).

## Модель

| Команда | Параметры |
| --- | --- |
| `walls.create` | `level_id`, `segments`, `height_mm`, `wall_type_id`, `base_offset_mm`, `structural` |
| `walls.from_planes` | `level_id`, `planes` (из `pointcloud.detect_planes`), `wall_type_id`, `min_length_mm`, `snap_angle_deg`, `height_mm`, `structural` |
| `floors.create` | `level_id`, `boundary`, `floor_type_id`, `offset_mm` |
| `walls.join` | `ids` |

`snap_angle_deg: 90` разворачивает оси стен к ближайшему прямому углу относительно середины
отрезка. Без `height_mm` высота каждой стены берётся из диапазона высот её плоскости.

Команды построения не прерываются на первой ошибке: результат содержит `walls` с созданными
элементами и `failed` с причинами по каждому отвергнутому отрезку.

## Виды

| Команда | Параметры |
| --- | --- |
| `views.list` | `include_templates`, `placeable_only` |
| `views.create_plan` | `level_id`, `view_family` (`FloorPlan`/`CeilingPlan`), `name`, `scale`, `view_template_id`, `detail_level` |
| `views.create_section` | `origin`, `direction`, `width_mm`, `height_mm`, `depth_mm`, `view_family` (`Section`/`Elevation`), `name`, `scale`, `view_template_id` |
| `views.create_3d` | `perspective`, `name`, `scale`, `view_template_id` |
| `views.update` | `view_id` и любые общие поля вида |
| `views.templates.list` | — |

## Листы

| Команда | Параметры |
| --- | --- |
| `sheets.list` | — |
| `sheets.create` | `number`, `name`, `titleblock_id`, `parameters` |
| `sheets.place_view` | `sheet_id`, `view_id`, `center`, `viewport_type_id` |
| `sheets.move_viewport` | `viewport_id`, `center` |
| `sheets.set_parameters` | `sheet_id`, `parameters` |
| `sheets.info` | `sheet_id` — размер рамки и размещённые виды |

`parameters` — это `{"имя параметра": значение}`; параметры только для чтения и отсутствующие в
семействе рамки пропускаются, а список применённых возвращается в `parameters_set`.

## Экспорт

| Команда | Параметры |
| --- | --- |
| `export.pdf` | `folder`, `sheet_ids` или `sheet_numbers`, `combine`, `file_name`, `hide_crop_boundaries`, `hide_scope_boxes`, `hide_reference_planes`, `hide_unreferenced_view_tags` |
| `export.dwg` | `folder`, `sheet_ids` или `sheet_numbers`, `setup_name`, `prefix`, `merged_views` |

Без `sheet_ids`/`sheet_numbers` выгружаются все листы проекта. `file_name` допустим только при
`combine: true`. Revit не сообщает имена созданных файлов, поэтому мост сравнивает содержимое
папки до и после экспорта и возвращает список новых файлов в поле `files`.
