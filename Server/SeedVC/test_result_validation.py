import importlib.util
from pathlib import Path
import tempfile
import unittest

import numpy as np
import soundfile as sf

spec = importlib.util.spec_from_file_location("seedvc_result_test_module", Path(__file__).with_name("seedvc_server.py"))
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class ConversionResultTests(unittest.TestCase):
    def check_audio(self, audio):
        with tempfile.TemporaryDirectory(prefix="neeeva_conversion_result_test_") as root:
            path = Path(root) / "output.wav"
            sf.write(path, audio, 16000, subtype="FLOAT")
            return server._normalise_result(path)

    def test_finite_audible_result_is_accepted(self):
        data, seconds = self.check_audio(.1 * np.sin(np.arange(16000) * .08))
        self.assertEqual(seconds, 1.)
        self.assertGreater(len(data), 32000)

    def test_silent_result_cannot_report_success(self):
        with self.assertRaisesRegex(RuntimeError, "silent"):
            self.check_audio(np.zeros(16000))

    def test_nan_result_cannot_report_success(self):
        with self.assertRaisesRegex(RuntimeError, "non-finite"):
            self.check_audio(np.full(16000, np.nan))

    def test_inf_result_cannot_report_success(self):
        with self.assertRaisesRegex(RuntimeError, "non-finite"):
            self.check_audio(np.full(16000, np.inf))

    def test_rvc_silence_has_explicit_http_failure_before_persistence(self):
        with tempfile.TemporaryDirectory(prefix="neeeva_conversion_result_test_") as root:
            path = Path(root) / "output.wav"
            sf.write(path, np.zeros(16000), 16000, subtype="PCM_16")
            with self.assertRaises(server.HTTPException) as raised:
                server._finish_rvc_result(path, path, path, {}, 1., 800,
                    True, 0, 1234, .85, .33, .75, "unit-silence", 0., False, True)
            self.assertEqual(raised.exception.status_code, 500)
            self.assertIn("silent", raised.exception.detail)


if __name__ == "__main__":
    unittest.main()
