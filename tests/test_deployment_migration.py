from pathlib import Path
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / "scripts" / "deployment" / "migrate.ps1"
ACCOUNTS = ROOT / "database" / "deployment" / "001-accounts.up.sql"
RUNBOOK = ROOT / "docs" / "deployment" / "SHARED-SERVER-MIGRATION.md"


class SharedDeploymentMigrationTests(unittest.TestCase):
    def test_runner_has_explicit_target_and_no_destructive_action(self):
        text = RUNNER.read_text(encoding="utf-8")
        self.assertIn("[ValidateSet('Status', 'Up')]", text)
        self.assertIn("$ApprovedTarget", text)
        self.assertIn("-cne $ApprovedTarget", text)
        self.assertNotRegex(text, re.compile(r"\bDROP\s+(DATABASE|TABLE)\b", re.IGNORECASE))
        self.assertNotIn("--force", text)

    def test_runner_uses_tls_journal_checksums_and_three_ordered_migrations(self):
        text = RUNNER.read_text(encoding="utf-8")
        self.assertIn("'VERIFY_IDENTITY'", text)
        self.assertIn("-AllowLocalDevelopmentDatabase is restricted to a loopback host", text)
        self.assertIn("bc_schema_migrations", text)
        self.assertIn("Get-FileHash -Algorithm SHA256", text)
        versions = re.findall(r"Version = '(\d{3}-[^']+)'", text)
        self.assertEqual(versions, ["001-accounts", "002-chat-history", "003-admin-audit"])
        self.assertIn("GET_LOCK", text)
        self.assertIn("unknown migration", text)
        self.assertIn("Checksum mismatch", text)
        self.assertIn("not a contiguous prefix", text)

    def test_account_migration_is_target_scoped_and_contains_no_seed_credentials(self):
        text = ACCOUNTS.read_text(encoding="utf-8")
        self.assertNotRegex(text, re.compile(r"\b(CREATE|DROP)\s+DATABASE\b", re.IGNORECASE))
        self.assertNotRegex(text, re.compile(r"\b(CREATE|ALTER)\s+USER\b|\bGRANT\b", re.IGNORECASE))
        self.assertNotRegex(text, re.compile(r"\bINSERT\s+INTO\s+users\b", re.IGNORECASE))
        for column in ("password_hash", "security_stamp", "access_failed_count", "lockout_end_utc"):
            self.assertIn(column, text)

    def test_runbook_keeps_mysql_and_ollama_away_from_browser_clients(self):
        text = RUNBOOK.read_text(encoding="utf-8")
        self.assertIn("browser -> HTTPS BC-LocalAI server -> MySQL over TLS", text)
        self.assertIn("Do not open ports 3306 or 11434", text)
        self.assertIn("127.0.0.1:11434", text)
        self.assertIn("their own", text)


if __name__ == "__main__":
    unittest.main()
