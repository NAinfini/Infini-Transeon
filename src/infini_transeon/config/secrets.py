"""Keyring-backed secrets storage with a stable reference URI.

API keys are never stored in the YAML config. The config stores a URI of the
form ``keyring://infini-transeon/<name>`` and this module resolves/sets the
underlying OS keychain entry.
"""

from __future__ import annotations

from typing import Final
from urllib.parse import urlparse

import keyring

from infini_transeon.utils.logging import logger

SERVICE: Final[str] = "infini-transeon"
SCHEME: Final[str] = "keyring"


class SecretError(RuntimeError):
    """Raised when the keyring backend fails."""


def build_ref(name: str) -> str:
    """Build a canonical reference URI for a named secret."""
    if not name or "/" in name:
        raise ValueError(f"invalid secret name: {name!r}")
    return f"{SCHEME}://{SERVICE}/{name}"


def parse_ref(ref: str) -> tuple[str, str]:
    """Return ``(service, username)`` for a reference URI."""
    parsed = urlparse(ref)
    if parsed.scheme != SCHEME:
        raise ValueError(f"not a keyring reference: {ref!r}")
    service = parsed.netloc or SERVICE
    username = parsed.path.lstrip("/")
    if not username:
        raise ValueError(f"secret reference missing name: {ref!r}")
    return service, username


def set_secret(name: str, value: str) -> str:
    """Store ``value`` in the OS keychain under ``name``. Returns the reference URI."""
    try:
        keyring.set_password(SERVICE, name, value)
    except keyring.errors.KeyringError as exc:  # type: ignore[attr-defined]
        raise SecretError(f"failed to store secret {name!r}: {exc}") from exc
    return build_ref(name)


def get_secret(ref: str | None) -> str | None:
    """Resolve a reference URI to its stored secret, or ``None`` if absent."""
    if not ref:
        return None
    service, username = parse_ref(ref)
    try:
        return keyring.get_password(service, username)
    except keyring.errors.KeyringError as exc:  # type: ignore[attr-defined]
        logger.warning("keyring read failed for {}: {}", ref, exc)
        return None


def delete_secret(ref: str) -> None:
    """Delete the secret identified by ``ref`` if present."""
    service, username = parse_ref(ref)
    try:
        keyring.delete_password(service, username)
    except keyring.errors.PasswordDeleteError:
        pass
    except keyring.errors.KeyringError as exc:  # type: ignore[attr-defined]
        raise SecretError(f"failed to delete {ref!r}: {exc}") from exc


__all__ = [
    "SERVICE",
    "SCHEME",
    "SecretError",
    "build_ref",
    "parse_ref",
    "set_secret",
    "get_secret",
    "delete_secret",
]
