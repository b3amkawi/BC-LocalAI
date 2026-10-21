-- BE-007 migration 001: chat history tables. MySQL >= 8.0.17, InnoDB.
-- Applied by an operator with scripts/chat-history/migrate.ps1 (migrator account), never by application startup.
-- No USE/CREATE DATABASE/GRANT: the migrator selects the approved schema explicitly; runtime grants are separate.
-- No account table or user foreign key is changed. DDL implicitly commits; a partial failure needs operator reconciliation.
CREATE TABLE Conversations (
    Id BINARY(16) NOT NULL,
    UserId VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
    Title VARCHAR(200) NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    DeletedAt DATETIME(6) NULL,
    NextSequence BIGINT NOT NULL DEFAULT 1,
    PRIMARY KEY (Id),
    KEY IX_Conversations_User_Updated (UserId, DeletedAt, UpdatedAt DESC, Id DESC),
    CONSTRAINT CK_Conversations_User CHECK (CHAR_LENGTH(UserId) BETWEEN 1 AND 64),
    CONSTRAINT CK_Conversations_Title CHECK (CHAR_LENGTH(Title) BETWEEN 1 AND 200),
    CONSTRAINT CK_Conversations_Sequence CHECK (NextSequence > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin;

CREATE TABLE Messages (
    Id BINARY(16) NOT NULL,
    ConversationId BINARY(16) NOT NULL,
    Sequence BIGINT NOT NULL,
    Role VARCHAR(16) NOT NULL,
    Content MEDIUMTEXT NOT NULL,
    Status VARCHAR(16) NOT NULL,
    ErrorCode VARCHAR(64) NULL,
    CreatedAt DATETIME(6) NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL,
    PRIMARY KEY (Id),
    UNIQUE KEY UX_Messages_Conversation_Sequence (ConversationId, Sequence),
    CONSTRAINT FK_Messages_Conversations FOREIGN KEY (ConversationId) REFERENCES Conversations (Id) ON DELETE RESTRICT,
    CONSTRAINT CK_Messages_Sequence CHECK (Sequence > 0),
    CONSTRAINT CK_Messages_Role CHECK (Role IN ('user', 'assistant')),
    CONSTRAINT CK_Messages_Status CHECK (Status IN ('pending', 'streaming', 'completed', 'failed', 'cancelled')),
    CONSTRAINT CK_Messages_Size CHECK (OCTET_LENGTH(Content) <= 1048576),
    CONSTRAINT CK_Messages_UserState CHECK (Role <> 'user' OR Status = 'completed'),
    CONSTRAINT CK_Messages_CompletedContent CHECK (Status <> 'completed' OR CHAR_LENGTH(TRIM(Content)) > 0),
    CONSTRAINT CK_Messages_ErrorCode CHECK (
        (Status = 'failed' AND ErrorCode IS NOT NULL AND CHAR_LENGTH(TRIM(ErrorCode)) > 0)
        OR (Status <> 'failed' AND ErrorCode IS NULL)
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_bin;
