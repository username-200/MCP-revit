"""HTTP-клиент к аддину-мосту, работающему внутри Revit."""

from __future__ import annotations

import os
from typing import Any

import httpx

DEFAULT_URL = "http://127.0.0.1:8765"
DEFAULT_TIMEOUT = 180.0


class RevitBridgeError(RuntimeError):
    """Мост ответил ошибкой либо оказался недоступен."""

    def __init__(self, message: str, kind: str = "error") -> None:
        super().__init__(message)
        self.kind = kind


class RevitClient:
    def __init__(
        self,
        base_url: str | None = None,
        token: str | None = None,
        timeout: float | None = None,
    ) -> None:
        self.base_url = (base_url or os.environ.get("REVIT_BRIDGE_URL") or DEFAULT_URL).rstrip("/")
        self.token = token if token is not None else os.environ.get("REVIT_BRIDGE_TOKEN", "")
        self.timeout = timeout or float(os.environ.get("REVIT_BRIDGE_TIMEOUT", DEFAULT_TIMEOUT))
        self._client: httpx.AsyncClient | None = None

    async def _http(self) -> httpx.AsyncClient:
        if self._client is None:
            headers = {"Content-Type": "application/json"}
            if self.token:
                # HTTP-заголовки не переносят кириллицу: без явной проверки httpx падает
                # с невнятной UnicodeEncodeError уже на первом запросе.
                if not self.token.isascii():
                    raise RevitBridgeError(
                        "Токен REVIT_BRIDGE_TOKEN должен состоять только из ASCII-символов "
                        "(латиница, цифры, знаки препинания).",
                        kind="bad_token",
                    )
                headers["X-Mcp-Token"] = self.token
            self._client = httpx.AsyncClient(
                base_url=self.base_url,
                headers=headers,
                timeout=httpx.Timeout(self.timeout),
            )
        return self._client

    async def aclose(self) -> None:
        if self._client is not None:
            await self._client.aclose()
            self._client = None

    async def call(
        self,
        command: str,
        params: dict[str, Any] | None = None,
        timeout: float | None = None,
    ) -> Any:
        """Выполняет команду моста и возвращает содержимое поля result."""
        payload: dict[str, Any] = {"command": command, "params": params or {}}
        # Revit должен успеть ответить раньше, чем оборвётся HTTP-запрос.
        payload["timeout_sec"] = (timeout or self.timeout) - 5.0

        client = await self._http()
        try:
            response = await client.post("/command", json=payload, timeout=timeout or self.timeout)
        except httpx.ConnectError as exc:
            raise RevitBridgeError(
                f"Мост {self.base_url} недоступен. Проверьте, что Revit запущен, проект открыт "
                f"и на вкладке «MCP» мост находится в состоянии «работает». Подробности: {exc}",
                kind="unavailable",
            ) from exc
        except httpx.ReadTimeout as exc:
            raise RevitBridgeError(
                f"Revit не ответил на команду '{command}' за {timeout or self.timeout:.0f} с. "
                "Вероятно, открыт модальный диалог или выполняется долгая операция.",
                kind="timeout",
            ) from exc

        try:
            body = response.json()
        except ValueError as exc:
            raise RevitBridgeError(
                f"Мост вернул не-JSON ответ (HTTP {response.status_code}): {response.text[:400]}"
            ) from exc

        if not body.get("ok"):
            error = body.get("error") or {}
            raise RevitBridgeError(
                error.get("message", "Неизвестная ошибка моста."),
                kind=error.get("type", "error"),
            )

        return body.get("result")

    async def health(self) -> dict[str, Any]:
        client = await self._http()
        try:
            response = await client.get("/health", timeout=10.0)
        except httpx.HTTPError as exc:
            raise RevitBridgeError(
                f"Мост {self.base_url} недоступен: {exc}", kind="unavailable"
            ) from exc

        return response.json().get("result", {})
