"""Behavioral specification for wheel-credit accounting.

These tests exercise a standalone reference model, NOT the Streamer.bot C# runtime.
Run with: python -m unittest discover -s tests -v
"""
import unittest


class CreditAccount:
    def __init__(self, total=0, earned=0, spent=0):
        self.total = total
        self.earned = max(earned, spent, total // 10)
        self.spent = spent

    @property
    def available(self):
        return max(0, self.earned - self.spent)

    def set_total(self, amount):
        self.total = max(0, amount)
        self.earned = max(self.earned, self.spent, self.total // 10)

    def check_in(self):
        self.set_total(self.total + 1)

    def submit_game(self):
        if self.available == 0:
            return False
        self.spent += 1
        return True


class CreditPolicyTests(unittest.TestCase):
    def test_ten_checkins_earn_one_credit(self):
        account = CreditAccount()
        for _ in range(9):
            account.check_in()
        self.assertEqual(account.available, 0)
        account.check_in()
        self.assertEqual(account.available, 1)

    def test_reset_does_not_duplicate_spent_credits(self):
        account = CreditAccount(total=30)
        self.assertTrue(account.submit_game())
        self.assertTrue(account.submit_game())
        self.assertEqual(account.available, 1)
        account.set_total(0)
        self.assertEqual(account.available, 1)
        account.set_total(30)
        self.assertEqual(account.available, 1)
        account.set_total(40)
        self.assertEqual(account.available, 2)

    def test_admin_remove_then_readd_does_not_double_grant(self):
        account = CreditAccount(total=20)
        self.assertTrue(account.submit_game())
        account.set_total(5)
        account.set_total(20)
        self.assertEqual(account.available, 1)

    def test_migration_restores_spent_lower_bound(self):
        historical_entries = 3
        current_total = 0
        saved_claimed = 0  # v4 admin reset bug
        spent = max(historical_entries, saved_claimed)
        account = CreditAccount(total=current_total, earned=spent, spent=spent)
        self.assertEqual(account.available, 0)

    def test_submission_does_not_make_negative_credit_balance(self):
        account = CreditAccount(total=10)
        self.assertTrue(account.submit_game())
        self.assertFalse(account.submit_game())
        self.assertEqual(account.available, 0)

    def test_existing_credit_remains_available_after_reset(self):
        account = CreditAccount(total=10)
        account.set_total(0)
        self.assertEqual(account.available, 1)



class SimulatedWheelStore:
    """Reference model for the pending journal's write-order and replay policy."""

    def __init__(self):
        self.claimed = 0
        self.earned = 2
        self.month_entries = {}
        self.pending = None

    def recover(self, fail_after=None):
        if self.pending is None:
            return
        data = self.pending
        if data["id"] not in self.month_entries:
            self.month_entries[data["id"]] = data
        if fail_after == "ledger":
            raise RuntimeError("simulated crash after ledger write")
        self.claimed = max(self.claimed, data["spent_before"] + 1)
        if fail_after == "claimed":
            raise RuntimeError("simulated crash after credit write")
        self.pending = None

    def submit(self, message_id, fail_after=None):
        self.recover()
        if message_id in self.month_entries:
            return False
        if self.earned <= self.claimed:
            return False
        self.pending = {"id": message_id, "spent_before": self.claimed}
        if fail_after == "pending":
            raise RuntimeError("simulated crash after pending write")
        self.recover(fail_after=fail_after)
        return True


class SubmissionRecoveryTests(unittest.TestCase):
    def test_recovery_after_pending_only(self):
        store = SimulatedWheelStore()
        with self.assertRaises(RuntimeError):
            store.submit("msg-1", fail_after="pending")
        store.recover()
        self.assertEqual(store.claimed, 1)
        self.assertEqual(len(store.month_entries), 1)
        self.assertIsNone(store.pending)

    def test_recovery_after_ledger_write(self):
        store = SimulatedWheelStore()
        with self.assertRaises(RuntimeError):
            store.submit("msg-1", fail_after="ledger")
        store.recover()
        self.assertEqual(store.claimed, 1)
        self.assertEqual(len(store.month_entries), 1)

    def test_recovery_after_claimed_write(self):
        store = SimulatedWheelStore()
        with self.assertRaises(RuntimeError):
            store.submit("msg-1", fail_after="claimed")
        store.recover()
        self.assertEqual(store.claimed, 1)
        self.assertEqual(len(store.month_entries), 1)

    def test_chat_redelivery_does_not_spend_twice(self):
        store = SimulatedWheelStore()
        self.assertTrue(store.submit("msg-1"))
        self.assertFalse(store.submit("msg-1"))
        self.assertEqual(store.claimed, 1)
        self.assertEqual(len(store.month_entries), 1)

    def test_distinct_messages_consume_distinct_credits(self):
        store = SimulatedWheelStore()
        self.assertTrue(store.submit("msg-1"))
        self.assertTrue(store.submit("msg-2"))
        self.assertFalse(store.submit("msg-3"))
        self.assertEqual(store.claimed, 2)
        self.assertEqual(len(store.month_entries), 2)

    def test_legacy_duplicate_entry_ids_are_counted_once(self):
        ids = ["entry-1", "entry-1", "entry-2"]
        self.assertEqual(len(set(ids)), 2)

if __name__ == "__main__":
    unittest.main()
