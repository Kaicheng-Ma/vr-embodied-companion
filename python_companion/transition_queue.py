"""Transition task queue for Python Layer 2A."""

from dataclasses import dataclass, field
from typing import Iterable, List


@dataclass
class TransitionTask:
    transition_id: str
    goal_type: str
    goal_target: str
    status: str = "pending"
    attempt_count: int = 0
    max_attempts: int = 5
    completion_check: str = ""
    entry_block_reason: str = ""
    notes: str = ""


@dataclass
class TransitionQueue:
    tasks: List[TransitionTask] = field(default_factory=list)

    def clear(self):
        self.tasks.clear()

    def upsert_task(self, task: TransitionTask):
        for index, existing in enumerate(self.tasks):
            if existing.transition_id == task.transition_id:
                # Duplicate evaluation of the same active transition is not a
                # new execution attempt. Preserve the existing attempt budget
                # unless a future explicit retry path raises it intentionally.
                task.attempt_count = max(existing.attempt_count, task.attempt_count, 1)
                self.tasks[index] = task
                return task

        task.attempt_count = 1
        self.tasks.append(task)
        return task

    def mark_completed(self, transition_id: str):
        self.tasks = [task for task in self.tasks if task.transition_id != transition_id]

    def remove_transitions_by_id(self, transition_ids: Iterable[str]):
        transition_id_set = {transition_id for transition_id in transition_ids if transition_id}
        if not transition_id_set:
            return
        self.tasks = [task for task in self.tasks if task.transition_id not in transition_id_set]

    def remove_known_obsolete_entry_transitions(self, valid_transition_ids: Iterable[str], known_entry_transition_ids: Iterable[str]):
        valid_transition_id_set = {transition_id for transition_id in valid_transition_ids if transition_id}
        known_entry_transition_id_set = {transition_id for transition_id in known_entry_transition_ids if transition_id}
        obsolete_transition_ids = known_entry_transition_id_set - valid_transition_id_set
        self.remove_transitions_by_id(obsolete_transition_ids)

    def has_pending_transition(self):
        return any(task.status in {"pending", "running"} for task in self.tasks)

    def current_task(self):
        if not self.tasks:
            return None
        return self.tasks[0]

    def snapshot(self):
        task = self.current_task()
        if task is None:
            return {
                "has_pending_transition": False,
                "queue_length": 0,
                "current_transition_id": "",
                "current_task_status": "",
                "current_task_goal_type": "",
                "current_task_goal_target": "",
                "current_task_completed": True,
            }

        return {
            "has_pending_transition": task.status in {"pending", "running"},
            "queue_length": len(self.tasks),
            "current_transition_id": task.transition_id,
            "current_task_status": task.status,
            "current_task_goal_type": task.goal_type,
            "current_task_goal_target": task.goal_target,
            "current_task_completed": task.status == "completed",
        }
