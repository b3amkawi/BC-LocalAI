-- SEC-003 admin writer account. Run in the BC-LocalAI database (mysql --database=<name>) by a DBA after approval,
-- AFTER admin-audit.sql. Replace <admin-host> with the fixed IP or netmask of the Admin machine (never '%').
-- Same statements as `bc-admin print-grants --writer-host <admin-host>`; a unit test keeps the two identical.
--
-- 1. Create the account with a newly generated secret (never this placeholder, never a reused password):
--    CREATE USER 'bc_admin_writer'@'<admin-host>' IDENTIFIED WITH caching_sha2_password BY '<secret>';
-- 2. Then run the statements below. No privilege on admin_audit_log: the triggers write the audit.
ALTER USER 'bc_admin_writer'@'<admin-host>' REQUIRE SSL WITH MAX_USER_CONNECTIONS 4 FAILED_LOGIN_ATTEMPTS 5 PASSWORD_LOCK_TIME 1;
GRANT SELECT (id, normalized_username) ON users TO 'bc_admin_writer'@'<admin-host>';
GRANT INSERT (id, username, normalized_username, display_name, password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO 'bc_admin_writer'@'<admin-host>';
GRANT UPDATE (password_hash, is_active, role, security_stamp, updated_at_utc) ON users TO 'bc_admin_writer'@'<admin-host>';
