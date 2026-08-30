import asyncio
import json

import httpx
import pytest

from revit_mcp.client import RevitBridgeError, RevitClient


def make_client(handler) -> RevitClient:
    client = RevitClient(base_url="http://127.0.0.1:8765", token="s3cret")
    client._client = httpx.AsyncClient(
        base_url=client.base_url,
        headers={"X-Mcp-Token": client.token},
        transport=httpx.MockTransport(handler),
    )
    return client


def test_call_returns_result_payload():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["token"] = request.headers.get("X-Mcp-Token")
        seen["body"] = json.loads(request.read())
        return httpx.Response(200, json={"ok": True, "result": {"levels": [{"id": 1}]}})

    client = make_client(handler)
    result = asyncio.run(client.call("levels.list"))

    assert result == {"levels": [{"id": 1}]}
    assert seen["url"].endswith("/command")
    assert seen["token"] == "s3cret"
    assert seen["body"]["command"] == "levels.list"


def test_call_raises_with_bridge_error_kind():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            400,
            json={"ok": False, "error": {"type": "not_found", "message": "Уровень 42 не найден."}},
        )

    client = make_client(handler)

    with pytest.raises(RevitBridgeError) as exc:
        asyncio.run(client.call("levels.create", {"elevation_mm": 0}))

    assert exc.value.kind == "not_found"
    assert "42" in str(exc.value)


def test_non_json_response_is_reported_readably():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="<html>Internal Server Error</html>")

    client = make_client(handler)

    with pytest.raises(RevitBridgeError) as exc:
        asyncio.run(client.call("ping"))

    assert "не-JSON" in str(exc.value)


def test_connection_failure_explains_how_to_fix():
    def handler(request: httpx.Request) -> httpx.Response:
        raise httpx.ConnectError("connection refused", request=request)

    client = make_client(handler)

    with pytest.raises(RevitBridgeError) as exc:
        asyncio.run(client.call("ping"))

    assert exc.value.kind == "unavailable"
    assert "мост" in str(exc.value).lower()


def test_non_ascii_token_is_rejected_with_a_clear_message():
    client = RevitClient(base_url="http://127.0.0.1:8765", token="секрет")

    with pytest.raises(RevitBridgeError) as exc:
        asyncio.run(client.call("ping"))

    assert exc.value.kind == "bad_token"
    assert "ASCII" in str(exc.value)


def test_command_timeout_is_shorter_than_http_timeout():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.read())
        return httpx.Response(200, json={"ok": True, "result": None})

    client = make_client(handler)
    client.timeout = 60.0
    asyncio.run(client.call("ping"))

    assert seen["body"]["timeout_sec"] == 55.0
