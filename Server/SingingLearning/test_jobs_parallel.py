import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace

from .jobs import JobQueue
from .store import LearningError, read_json


class _Store:
    def __init__(self, directory, limit=2):
        self.root = Path(directory)
        self.config = SimpleNamespace(queue_limit=limit, auto_train_debounce_seconds=.02)

    def snapshot(self, profile):
        return {}

    @staticmethod
    def fusion_summary(report):
        return {key: report[key] for key in ('status', 'teaching_eligible')}


class _Pipeline:
    def __init__(self):
        self.training_started = threading.Event()
        self.training_release = threading.Event()
        self.proposal_called = threading.Event()
        self.feature_called = threading.Event()
        self.lock = threading.Lock()
        self.train_calls = self.active_train_calls = self.maximum_active_train_calls = 0

    def train(self, profile, model_id):
        with self.lock:
            self.train_calls += 1
            self.active_train_calls += 1
            self.maximum_active_train_calls = max(self.maximum_active_train_calls, self.active_train_calls)
        self.training_started.set()
        try:
            if not self.training_release.wait(4):
                raise TimeoutError('test_training_release_missing')
            return dict(model_id=model_id, status='candidate', source_dataset_revision=1,
                        stale_at_completion=False, execution_authorized=False, dataset_summary={},
                        sequence_boundary=dict(status='shadow_rejected', teaching_eligible=False))
        finally:
            with self.lock:
                self.active_train_calls -= 1

    def propose(self, profile, recording_id):
        self.proposal_called.set()
        return dict(recording_id=recording_id, execution_authorized=False)

    def extract(self, profile, recording_id):
        self.feature_called.set()
        return dict(recording_id=recording_id, execution_authorized=False)


class ParallelJobTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store, self.pipeline = _Store(self.directory.name), _Pipeline()
        self.jobs = JobQueue(self.store, self.pipeline)

    def tearDown(self):
        self.pipeline.training_release.set()
        self.jobs.close(timeout=2)
        self.directory.cleanup()

    def wait_status(self, job_id, status='completed'):
        with self.jobs.condition:
            finished = self.jobs.condition.wait_for(lambda: self.jobs.items[job_id]['status'] == status, timeout=2)
        self.assertTrue(finished, self.jobs.get(job_id))
        return self.jobs.get(job_id)

    def test_proposal_and_feature_complete_while_training_is_blocked(self):
        self.jobs.start()
        train = self.jobs.enqueue('p-user', 'train')['job_id']
        self.assertTrue(self.pipeline.training_started.wait(1))
        proposal = self.jobs.enqueue('p-user', 'proposal', dict(recording_id='r-new'))['job_id']
        feature = self.jobs.enqueue('p-user', 'features', dict(recording_id='r-next'))['job_id']
        self.wait_status(proposal)
        self.wait_status(feature)
        self.assertTrue(self.pipeline.proposal_called.is_set())
        self.assertTrue(self.pipeline.feature_called.is_set())
        self.assertEqual(self.jobs.get(train)['status'], 'running')
        self.assertEqual(self.jobs.get(proposal)['queue_lane'], 'foreground')
        self.assertEqual(self.jobs.get(train)['queue_lane'], 'training')
        self.pipeline.training_release.set()
        result = self.wait_status(train)['result']
        self.assertEqual(result['sequence_boundary']['status'], 'shadow_rejected')

    def test_running_training_has_one_coalesced_successor_and_never_overlaps(self):
        self.jobs.start()
        first = self.jobs.enqueue('p-user', 'train')['job_id']
        self.assertTrue(self.pipeline.training_started.wait(1))
        second = self.jobs.enqueue('p-user', 'train', auto=True)
        third = self.jobs.enqueue('p-user', 'train', auto=True)
        self.assertNotEqual(first, second['job_id'])
        self.assertEqual(second['job_id'], third['job_id'])
        self.assertTrue(third['coalesced'])
        self.assertEqual(self.pipeline.train_calls, 1)
        self.pipeline.training_release.set()
        self.wait_status(first)
        self.wait_status(second['job_id'])
        self.assertEqual(self.pipeline.train_calls, 2)
        self.assertEqual(self.pipeline.maximum_active_train_calls, 1)

    def test_each_lane_is_bounded_without_training_exhausting_foreground_slots(self):
        # No workers yet: the queue contents and limits are deterministic.
        for index in range(2):
            self.jobs.enqueue('p-user-' + str(index), 'train')
        with self.assertRaises(LearningError) as raised:
            self.jobs.enqueue('p-overflow', 'train')
        self.assertEqual(raised.exception.code, 'queue_full')
        for index in range(2):
            self.jobs.enqueue('p-user', 'proposal', dict(recording_id='r-' + str(index)))
        with self.assertRaises(LearningError) as raised:
            self.jobs.enqueue('p-user', 'features', dict(recording_id='r-overflow'))
        self.assertEqual(raised.exception.code, 'queue_full')
        self.assertEqual(len(self.jobs.pending), 4)

    def test_close_persists_queued_jobs_and_tracks_lingering_training_worker(self):
        self.jobs.start()
        first = self.jobs.enqueue('p-user', 'train')['job_id']
        self.assertTrue(self.pipeline.training_started.wait(1))
        second = self.jobs.enqueue('p-user', 'train')['job_id']
        self.jobs.close(timeout=.05)
        self.assertTrue(self.jobs.workers_alive)
        self.assertTrue(self.jobs.training_thread.is_alive())
        self.assertFalse(self.jobs.thread.is_alive())
        queued = read_json(self.jobs.directory / (second + '.json'))
        self.assertEqual(queued['status'], 'interrupted')
        self.assertEqual(queued['error'], 'service_shutdown')
        self.assertEqual(self.jobs.get(first)['status'], 'running')
        with self.assertRaises(LearningError) as raised:
            self.jobs.enqueue('p-user', 'proposal', dict(recording_id='r-new'))
        self.assertEqual(raised.exception.code, 'worker_closed')
        self.pipeline.training_release.set()
        self.jobs.close(timeout=2)
        self.assertFalse(self.jobs.workers_alive)
        self.assertEqual(read_json(self.jobs.directory / (first + '.json'))['status'], 'completed')

    def test_restart_recovers_both_persisted_lanes(self):
        train = self.jobs.enqueue('p-user', 'train')['job_id']
        proposal = self.jobs.enqueue('p-user', 'proposal', dict(recording_id='r-new'))['job_id']
        # Simulate a lost process after recording a claimed training job. No
        # actual workers exist; the process lease is an app-level concern.
        self.jobs.items[train]['status'] = 'running'
        self.jobs._save(train)
        self.jobs.pending.clear()
        recovered = JobQueue(self.store, self.pipeline)
        try:
            for job_id in (train, proposal):
                saved = recovered.get(job_id)
                self.assertEqual(saved['status'], 'interrupted')
                self.assertEqual(saved['error'], 'service_restarted_before_completion')
                self.assertEqual(read_json(recovered.directory / (job_id + '.json'))['status'], 'interrupted')
            self.assertFalse(recovered.workers_alive)
        finally:
            recovered.close()

    def test_start_is_idempotent_and_cannot_reopen_closed_queue(self):
        self.jobs.start()
        foreground, training = self.jobs.thread, self.jobs.training_thread
        self.jobs.start()
        self.assertIs(self.jobs.thread, foreground)
        self.assertIs(self.jobs.training_thread, training)
        self.assertTrue(self.jobs.workers_alive)
        self.jobs.close()
        with self.assertRaises(LearningError):
            self.jobs.start()


if __name__ == '__main__':
    unittest.main()
