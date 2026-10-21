-- NEW LOCAL DEVELOPMENT SCHEMA, NOT THE COMPANY'S LEGACY SCHEMA.
-- Run only against the isolated SEC-001 instance. No production migration.
CREATE DATABASE bc_localai_sec001 CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
USE bc_localai_sec001;
CREATE TABLE users (
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
    username VARCHAR(128) NOT NULL,
    normalized_username VARCHAR(128) NOT NULL,
    display_name VARCHAR(200) NOT NULL,
    password_hash VARCHAR(512) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT FALSE,
    role ENUM('User', 'Admin') NOT NULL DEFAULT 'User',
    security_stamp CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    access_failed_count INT UNSIGNED NOT NULL DEFAULT 0,
    lockout_end_utc DATETIME(6) NULL,
    updated_at_utc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    UNIQUE KEY ux_users_normalized_username (normalized_username),
    CONSTRAINT ck_users_active CHECK (is_active IN (0, 1)),
    CONSTRAINT ck_users_username CHECK (CHAR_LENGTH(normalized_username) > 0)
);
