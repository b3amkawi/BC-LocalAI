-- BE-007 rollback of migration 001. EMPTY pre-go-live schema only; never automatic or a production rollback.
-- Operator must verify database identity, backup/restore readiness and approve this exact target first.
-- Then, in the SAME connection: SET @BE007_ALLOW_EMPTY_ROLLBACK = 1;
-- Requires CREATE ROUTINE for this temporary guard; runtime app account must not have it.
DELIMITER $$
CREATE PROCEDURE BE007_001_RollbackEmptyOnly()
BEGIN
    IF COALESCE(@BE007_ALLOW_EMPTY_ROLLBACK, 0) <> 1 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Explicit empty-schema rollback approval required';
    END IF;
    IF EXISTS (SELECT 1 FROM Messages LIMIT 1) OR EXISTS (SELECT 1 FROM Conversations LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Non-empty chat history must not be dropped by this script';
    END IF;
    DROP TABLE Messages;
    DROP TABLE Conversations;
END$$
DELIMITER ;
CALL BE007_001_RollbackEmptyOnly();
DROP PROCEDURE BE007_001_RollbackEmptyOnly;
