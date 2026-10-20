"""Unified error contract.

Every error response (including FastAPI request-validation errors) uses the
envelope:  {"error": {"code": "<CODE>", "message": "<str>", "details": [...]}}

HTTP status semantics (contract, see docs/decisions.md D12):
- 422 VALIDATION_ERROR        request body/header/path invalid (incl. unknown type, payload field errors)
- 422 ROUTING_NOT_CONFIGURED  no usable route config for the approval type at submit time
- 422 ROUTING_ALL_SELF        every configured level's approver == submitter
- 404 NOT_FOUND               instance/task/config does not exist
- 403 FORBIDDEN               actor is not the submitter/approver allowed to perform this action
- 403 SELF_APPROVAL_FORBIDDEN actor attempted to approve/reject own submitted instance
- 409 STATE_CONFLICT          action illegal in current state (terminal instance, non-current level,
                              duplicate approval, withdraw by rules, etc.)
- 409 VERSION_CONFLICT        optimistic-lock version mismatch (concurrent modification)
- 409 ALREADY_URGENT          instance is already urgent
- 409 INVOICE_DUPLICATE       expense invoice number already submitted globally (D17-5)
- 409 DUPLICATE_SERVICE_NAME  same env + service_name already has an in-flight/approved instance (D17-5)
- 409 DUPLICATE_ACCOUNT       submitter already has an active personal account for the system (D17-5)
- 501 NOT_IMPLEMENTED         scaffold-stage placeholder; retired now that all endpoints are implemented
"""
from __future__ import annotations

from enum import Enum
from typing import Any

from fastapi import Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse


class ErrorCode(str, Enum):
    VALIDATION_ERROR = "VALIDATION_ERROR"
    ROUTING_NOT_CONFIGURED = "ROUTING_NOT_CONFIGURED"
    ROUTING_ALL_SELF = "ROUTING_ALL_SELF"
    NOT_FOUND = "NOT_FOUND"
    FORBIDDEN = "FORBIDDEN"
    SELF_APPROVAL_FORBIDDEN = "SELF_APPROVAL_FORBIDDEN"
    STATE_CONFLICT = "STATE_CONFLICT"
    VERSION_CONFLICT = "VERSION_CONFLICT"
    ALREADY_URGENT = "ALREADY_URGENT"
    INVOICE_DUPLICATE = "INVOICE_DUPLICATE"
    DUPLICATE_SERVICE_NAME = "DUPLICATE_SERVICE_NAME"
    DUPLICATE_ACCOUNT = "DUPLICATE_ACCOUNT"
    NOT_IMPLEMENTED = "NOT_IMPLEMENTED"


class ApiError(Exception):
    """Business error carrying an HTTP status and a machine-readable code."""

    def __init__(
        self,
        status_code: int,
        code: ErrorCode,
        message: str,
        details: list[Any] | None = None,
    ) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message
        self.details = details or []


def _envelope(code: str, message: str, details: list[Any]) -> dict[str, Any]:
    return {"error": {"code": code, "message": message, "details": details}}


async def api_error_handler(request: Request, exc: ApiError) -> JSONResponse:
    return JSONResponse(
        status_code=exc.status_code,
        content=_envelope(exc.code.value, exc.message, exc.details),
    )


async def validation_error_handler(
    request: Request, exc: RequestValidationError
) -> JSONResponse:
    # Normalize to plain strings so the envelope is always JSON-serializable.
    # NB: only pick loc/msg/type — raw error dicts may carry non-serializable ctx/input/url.
    details = [
        {
            "loc": [str(part) for part in err.get("loc", [])],
            "msg": str(err.get("msg", "")),
            "type": str(err.get("type", "")),
        }
        for err in exc.errors()
    ]
    return JSONResponse(
        status_code=422,
        content=_envelope(
            ErrorCode.VALIDATION_ERROR.value, "request validation failed", details
        ),
    )
