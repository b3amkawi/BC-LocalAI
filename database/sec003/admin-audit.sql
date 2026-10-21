-- SEC-003 audit table for the admin account script. Additive only: creates one table, changes and deletes nothing.
-- Run in the BC-LocalAI database (mysql --database=<name>) by a DBA after approval. Safe to run twice.
-- No column may hold a password, password hash or security stamp.
CREATE TABLE IF NOT EXISTS admin_audit_log (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    occurred_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    actor VARCHAR(128) NOT NULL,
    os_user VARCHAR(256) NOT NULL,
    db_user VARCHAR(288) NOT NULL,
    action ENUM('create', 'disable', 'enable', 'reset_password', 'set_role', 'revoke_sessions') NOT NULL,
    target_user_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    detail VARCHAR(64) NULL,
    KEY ix_admin_audit_target (target_user_id, occurred_at_utc),
    CONSTRAINT ck_admin_audit_actor CHECK (CHAR_LENGTH(actor) > 0)
);
