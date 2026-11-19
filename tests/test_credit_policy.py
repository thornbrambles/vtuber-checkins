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


if __name__ == "__main__":
    unittest.main()
