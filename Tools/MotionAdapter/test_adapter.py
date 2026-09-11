import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
import torch

from .adapter import MotionAdapter, load_adapter
from .data import read_records, read_bundle, write_bundle
from .generate_prompts import generate
from .import_probe import import_export
from .train import fit


class ContractTests(unittest.TestCase):
    def test_native_export_rejects_mixed_runs_and_text_order(self):
        rows = generate(2)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'dataset.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows), encoding='utf-8')
            (root/'prompts.txt').write_text(''.join(r['text']+'\n' for r in rows), encoding='utf-8')
            np.ones((2,2048), dtype='<f4').tofile(root/'features.f32')
            (root/'provenance.json').write_text(json.dumps({'test_only':True}), encoding='utf-8')
            (root/'report.json').write_text(json.dumps({'schema':1,'feature_contract':'qwen-motion-raw-last-v1',
                'rows':2,'dimension':2048,'model_load_count':1,'context_count':2,
                'chat_interleave_equal':True,'repeat_max_abs':0}), encoding='utf-8')
            manifest = {'schema':1, 'probe_exe_sha256':'synthetic-test-only'}
            for filename, key in [('prompts.txt','prompts'),('features.f32','features'),
                                  ('report.json','report'),('provenance.json','provenance')]:
                manifest[key+'_sha256'] = hashlib.sha256((root/filename).read_bytes()).hexdigest()
            (root/'export-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
            import_export(root,root/'bundle.npz')
            (root/'dataset.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows[::-1]), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'dataset text/order'):
                import_export(root,root/'bundle.npz')
            (root/'features.f32').write_bytes(b'incomplete')
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                import_export(root,root/'bundle.npz')

    def test_semantic_split_isolation_and_determinism(self):
        rows = generate()
        self.assertEqual(rows, generate())
        self.assertEqual(len(rows), 2000)
        self.assertEqual(len({r['text'] for r in rows}), 2000)
        groups = {}
        for row in rows:
            groups.setdefault(row['semantic_group'], set()).add(row['split'])
        self.assertTrue(all(len(s) == 1 for s in groups.values()))
        self.assertEqual({r['split'] for r in rows}, {'train', 'val', 'test'})

    def test_bundle_rejects_reordering_and_text_change(self):
        rows = generate(5)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'features.npz'
            write_bundle(path, np.ones((5, 2048)), rows, {'kind':'qwen', 'feature_contract':'test-only'})
            read_bundle(path, rows, 'qwen')
            with self.assertRaises(ValueError):
                read_bundle(path, rows[::-1], 'qwen')
            modified = [dict(r) for r in rows]
            modified[0]['text'] += ' changed'
            with self.assertRaises(ValueError):
                read_bundle(path, modified, 'qwen')

    def test_rejects_nonfinite_features_and_split_leakage(self):
        rows = generate(2)
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                write_bundle(Path(directory)/'bad.npz', np.full((2,2048), np.nan), rows,
                             {'kind':'qwen', 'feature_contract':'test-only'})
            rows[1]['semantic_group'] = rows[0]['semantic_group']
            rows[1]['split'] = 'test' if rows[0]['split'] != 'test' else 'train'
            path = Path(directory)/'data.jsonl'
            path.write_text('\n'.join(json.dumps(r) for r in rows))
            with self.assertRaises(ValueError):
                read_records(path)
            rows = generate(2)
            rows[0]['id'] = '../outside-output'
            path.write_text('\n'.join(json.dumps(r) for r in rows))
            with self.assertRaisesRegex(ValueError, 'safe filenames'):
                read_records(path)

    def test_adapter_preserves_target_scale_and_contract(self):
        model = MotionAdapter('linear')
        torch.nn.init.zeros_(model.network.weight)
        torch.nn.init.ones_(model.network.bias)
        model.target_mean.fill_(5)
        model.target_std.fill_(2)
        self.assertTrue(torch.equal(model(torch.zeros(1,2048)), torch.full((1,4096),7.0)))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'adapter.pt'
            torch.save({'schema':1, 'architecture':'linear', 'state_dict':model.state_dict(),
                        'qwen_contract':'test-only'}, path)
            restored, _ = load_adapter(path, expected_contract='test-only')
            self.assertTrue(torch.equal(restored(torch.zeros(1,2048)), model(torch.zeros(1,2048))))
            with self.assertRaises(ValueError):
                load_adapter(path, expected_contract='different-quantization')

    def test_training_gradient_can_learn_a_synthetic_feature_pair(self):
        # Plumbing verification only; these synthetic values are not ARDY training evidence.
        torch.manual_seed(4)
        model = MotionAdapter('linear')
        q = torch.randn(4,2048)
        target = torch.randn(4,4096)
        optimizer = torch.optim.Adam(model.parameters(), lr=1e-3)
        initial = (model(q)-target).square().mean().item()
        for _ in range(15):
            optimizer.zero_grad()
            loss = (model(q)-target).square().mean()
            loss.backward()
            optimizer.step()
        self.assertLess((model(q)-target).square().mean().item(), initial*.2)

    def test_full_fit_does_not_use_held_out_target_statistics(self):
        rng = np.random.default_rng(4)
        q = rng.normal(size=(9,2048)).astype(np.float32)
        target = rng.normal(size=(9,4096)).astype(np.float32)
        target[5:] += 100
        rows = [{'split':s} for s in ['train']*5+['val']*2+['test']*2]
        model, report = fit(q,target,rows,architecture='linear',epochs=2,batch=5)
        np.testing.assert_allclose(model.target_mean.numpy(), target[:5].mean(0), atol=1e-6)
        self.assertEqual(report['counts'], {'train':5,'val':2,'test':2})
        self.assertTrue(np.isfinite(report['scores']['test']['standardized_mse']))


if __name__ == '__main__':
    unittest.main()
