import unittest
import numpy as np
from Server.ARDY.locomotion import make_route, native_targets, Route


class RouteTests(unittest.TestCase):
    def test_straight_brakes_and_holds_exact_destination(self):
        route = make_route()
        self.assertTrue(np.all(route.positions[:, 0] == 0))
        self.assertGreater(route.positions[-1, 2], 3.)
        np.testing.assert_array_equal(route.positions[160:], np.tile(route.positions[-1], (40, 1)))
        self.assertLess(np.max(np.linalg.norm(np.diff(route.positions, axis=0), axis=1)), .04)

    def test_turn_reflection_and_heading(self):
        route = make_route("turn-stop")
        self.assertGreater(route.positions[-1, 0], 1.)
        np.testing.assert_allclose(route.headings[-1], np.pi/2)
        p, h, indices, count = native_targets(route, 80, 16)
        np.testing.assert_array_equal(indices[:2], [16, 17])
        self.assertEqual(count, 136)
        np.testing.assert_allclose(p[:, 0], -route.positions[80:, 0])
        np.testing.assert_allclose(h[-1], -np.pi/2)

    def test_redirect_does_not_leak_future_change(self):
        before = make_route("redirect-stop")
        straight = make_route("straight-stop")
        np.testing.assert_array_equal(before.positions, straight.positions)
        anchor = np.array([.13, .92, 2.1])
        after = make_route("redirect-stop", changed=True, anchor=anchor, velocity=[.02, 0, .5])
        self.assertEqual(after.change_frame, 80)
        self.assertLess(np.linalg.norm(after.positions[80, [0, 2]]-anchor[[0, 2]]), .04)
        self.assertLess(after.positions[-1, 0], -.5)

    def test_only_complete_windows_and_valid_routes(self):
        for start in (-1, 1, 180, 200):
            with self.assertRaises(ValueError):
                native_targets(make_route(), start, 16)
        with self.assertRaises(ValueError):
            native_targets(make_route(), 0, 4)
        bad = Route(np.zeros((40, 3)), np.full(40, np.nan), 20)
        with self.assertRaises(ValueError):
            native_targets(bad, 0, 16)


if __name__ == "__main__":
    unittest.main()
