import unittest

from test_review_workflow import dashboard, fixture


class DashboardRegressionTests(unittest.TestCase):
    def test_snapshot_shows_records_and_points_to_off_stream_admin_commands(self):
        data = fixture()
        page = dashboard.page(data, "secret", "Loaded backup", "alice")
        self.assertIn("alice<br>", page)
        self.assertNotIn("bob<br>", page)
        self.assertIn("entry-1", page)
        self.assertIn("!checkinadmin", page)
        self.assertIn("!wheeladmin", page)
        self.assertIn("/exportwheel", page)
        for route in ("/edit", "/review", "/save"):
            self.assertNotIn("action='" + route + "'", page)
            self.assertNotIn('action="' + route + '"', page)
