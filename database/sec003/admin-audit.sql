-- SEC-003 audit for account administration. Additive only: one table and two triggers; changes and deletes no data.
-- Run with the mysql client in the BC-LocalAI database after approval: mysql --database=<name> -e "source admin-audit.sql"
-- Needs MySQL 8.0.29+ (CREATE TRIGGER IF NOT EXISTS). The account running it becomes the triggers' DEFINER and must keep,
-- for as long as the triggers exist: INSERT on admin_audit_log, SELECT and TRIGGER on users. If that account is dropped or
-- loses them, EVERY update of users fails (error 1449/1142) - BE-005's failed-login bookkeeping included, so logins fail too.
-- Use a dedicated, locked definer account (guide section 3). The admin writer itself gets no privilege on this table.
-- IF NOT EXISTS keeps an existing trigger unchanged: to deploy a changed body, a DBA drops and recreates it deliberately.
-- No column may hold a password, password hash or security stamp.
--
-- Why triggers: the database writes the audit row for every change to an account's password, status, role or stamp,
-- whoever makes it (the bc-admin script, the mysql client, a DBA). The script cannot be bypassed to change an account
-- without a record, and db_user comes from USER() on the server, not from the client.

-- Triggers keep the sql_mode in force when they are created: strict, so an audit value that does not fit fails the
-- change instead of being cut.
SET SESSION sql_mode = 'STRICT_ALL_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';

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
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_bin;

DELIMITER $$

CREATE TRIGGER IF NOT EXISTS trg_users_admin_audit_insert AFTER INSERT ON users FOR EACH ROW
BEGIN
    -- @bc_admin_actor / @bc_admin_os_user are set by the bc-admin script; a change made any other way is still recorded.
    INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
    VALUES (COALESCE(@bc_admin_actor, '(direct SQL)'), COALESCE(@bc_admin_os_user, '(unknown)'), USER(), 'create', NEW.id,
            CONCAT('role=', NEW.role));
    SET @bc_admin_audit_id = LAST_INSERT_ID();
END$$

CREATE TRIGGER IF NOT EXISTS trg_users_admin_audit_update AFTER UPDATE ON users FOR EACH ROW
BEGIN
    -- One row per kind of change, decided from the data itself (OLD vs NEW), never from what the client claims.
    -- Failed-attempt and lockout updates (BE-005) change none of these columns and are not recorded.
    -- utf8mb4 explicitly: local variables otherwise take the database default charset and could refuse Thai names.
    DECLARE who_actor VARCHAR(128) CHARACTER SET utf8mb4 DEFAULT COALESCE(@bc_admin_actor, '(direct SQL)');
    DECLARE who_os VARCHAR(256) CHARACTER SET utf8mb4 DEFAULT COALESCE(@bc_admin_os_user, '(unknown)');
    IF NOT (NEW.password_hash <=> OLD.password_hash) THEN
        INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
        VALUES (who_actor, who_os, USER(), 'reset_password', NEW.id, NULL);
        SET @bc_admin_audit_id = LAST_INSERT_ID();
    END IF;
    IF NOT (NEW.is_active <=> OLD.is_active) THEN
        INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
        VALUES (who_actor, who_os, USER(), IF(NEW.is_active, 'enable', 'disable'), NEW.id, NULL);
        SET @bc_admin_audit_id = LAST_INSERT_ID();
    END IF;
    IF NOT (NEW.role <=> OLD.role) THEN
        INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
        VALUES (who_actor, who_os, USER(), 'set_role', NEW.id, CONCAT('role=', NEW.role));
        SET @bc_admin_audit_id = LAST_INSERT_ID();
    END IF;
    IF NOT (NEW.security_stamp <=> OLD.security_stamp) AND (NEW.password_hash <=> OLD.password_hash)
       AND (NEW.is_active <=> OLD.is_active) AND (NEW.role <=> OLD.role) THEN
        INSERT INTO admin_audit_log (actor, os_user, db_user, action, target_user_id, detail)
        VALUES (who_actor, who_os, USER(), 'revoke_sessions', NEW.id, NULL);
        SET @bc_admin_audit_id = LAST_INSERT_ID();
    END IF;
END$$

DELIMITER ;
