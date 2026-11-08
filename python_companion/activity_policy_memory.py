"""Activity-partitioned policy-memory ownership."""

from __future__ import annotations

from .models import ACTIVITY_ID_CAMPFIRE, ACTIVITY_ID_COMMON, ACTIVITY_ID_FISHING
from .policy_memory import PolicyMemory


class ActivityPolicyMemoryStore:
    def __init__(self):
        self._memories = {
            ACTIVITY_ID_COMMON: PolicyMemory(),
            ACTIVITY_ID_FISHING: PolicyMemory(),
            ACTIVITY_ID_CAMPFIRE: PolicyMemory(),
        }

    def get_memory(self, activity_id):
        return self._memories.get(activity_id) or self._memories[ACTIVITY_ID_COMMON]

    def get_summary(self, activity_id):
        return self.get_memory(activity_id).build_summary()

    def get_prompt_context(self, activity_id):
        return self.get_memory(activity_id).build_prompt_context()

    def update_from_response(self, activity_id, response, perception=None):
        self.get_memory(activity_id).update_from_response(response, perception=perception)

    def reset_activity(self, activity_id):
        if activity_id in self._memories:
            self._memories[activity_id] = PolicyMemory()

    def reset_all(self):
        for activity_id in list(self._memories.keys()):
            self._memories[activity_id] = PolicyMemory()
