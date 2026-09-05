"""Process-local duplicate suppression; no credentials or request bodies are retained in keys."""
import asyncio
import time
from collections import OrderedDict
from fastapi import HTTPException


class RequestCache:
    def __init__(self, maximum_completed=1024, ttl=1200, error_ttl=5):
        self.maximum_completed = maximum_completed
        self.ttl = ttl
        self.error_ttl = error_ttl
        self.pending = {}
        self.completed = OrderedDict()

    async def get(self, key, fingerprint, factory):
        now = time.monotonic()
        for old_key, entry in list(self.completed.items()):
            if entry[1] <= now:
                del self.completed[old_key]
        if key in self.completed:
            previous, _, result, error = self.completed[key]
            if previous != fingerprint:
                raise HTTPException(409, "requestId reused with different payload")
            if error:
                raise HTTPException(error[0], error[1])
            return result
        if key in self.pending:
            previous, task = self.pending[key]
            if previous != fingerprint:
                raise HTTPException(409, "requestId reused with different payload")
        else:
            task = asyncio.create_task(self._run(key, fingerprint, factory))
            self.pending[key] = (fingerprint, task)
            # A disconnected waiter must not cancel the shared, deadline-bounded provider request.
            task.add_done_callback(lambda done: None if done.cancelled() else done.exception())
        return await asyncio.shield(task)

    async def _run(self, key, fingerprint, factory):
        result = None
        error = None
        lifetime = self.ttl
        try:
            result = await factory()
            return result
        except HTTPException as exc:
            error = (exc.status_code, exc.detail)
            # Definitive failures can retry later. Ambiguous timeouts keep their tombstone to avoid rebilling.
            if exc.status_code != 504:
                lifetime = self.error_ttl
            raise
        finally:
            self.pending.pop(key, None)
            if result is not None or error is not None:
                self.completed[key] = (fingerprint, time.monotonic() + lifetime, result, error)
                while len(self.completed) > self.maximum_completed:
                    self.completed.popitem(last=False)
