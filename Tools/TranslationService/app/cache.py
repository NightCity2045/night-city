from __future__ import annotations

from collections import OrderedDict
from dataclasses import dataclass
import time
from typing import Generic, TypeVar

T = TypeVar("T")


@dataclass
class _CacheEntry(Generic[T]):
    value: T
    expires_at: float


class TTLCache(Generic[T]):
    """A small in-memory TTL cache with LRU eviction."""

    def __init__(self, max_items: int, ttl_seconds: int) -> None:
        if max_items < 1:
            raise ValueError(f"max_items must be >= 1, got {max_items}")
        if ttl_seconds < 1:
            raise ValueError(f"ttl_seconds must be >= 1, got {ttl_seconds}")

        self._max_items = max_items
        self._ttl_seconds = ttl_seconds
        self._entries: OrderedDict[str, _CacheEntry[T]] = OrderedDict()

    @property
    def size(self) -> int:
        self._purge_expired()
        return len(self._entries)

    def get(self, key: str) -> T | None:
        entry = self._entries.get(key)
        if entry is None:
            return None

        now = time.monotonic()
        if entry.expires_at <= now:
            del self._entries[key]
            return None

        self._entries.move_to_end(key)
        return entry.value

    def put(self, key: str, value: T) -> None:
        now = time.monotonic()
        self._purge_expired(now)

        self._entries[key] = _CacheEntry(value=value, expires_at=now + self._ttl_seconds)
        self._entries.move_to_end(key)

        while len(self._entries) > self._max_items:
            self._entries.popitem(last=False)

    def clear(self) -> None:
        self._entries.clear()

    def _purge_expired(self, now: float | None = None) -> None:
        now = time.monotonic() if now is None else now
        expired_keys = [key for key, entry in self._entries.items() if entry.expires_at <= now]
        for key in expired_keys:
            self._entries.pop(key, None)
