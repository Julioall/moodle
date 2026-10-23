-- Reconcilia duplicatas históricas de correção assistida sem apagar dados.
-- Uso: psql ... -v ON_ERROR_STOP=1 -f scripts/reconcile-course-grading-duplicates.sql
-- O curso é intencionalmente explícito para limitar o raio da manutenção.

\set course_id 33447

BEGIN;

CREATE TEMP TABLE grading_duplicate_reconciliation ON COMMIT DROP AS
WITH ranked AS (
    SELECT
        i."Id",
        i."BatchId",
        i."CourseId",
        i."AssignmentId",
        i."SubmissionId",
        i."Status",
        ROW_NUMBER() OVER (
            PARTITION BY i."CourseId", i."AssignmentId", i."SubmissionId"
            ORDER BY CASE i."Status"
                WHEN 'Committed' THEN 0
                WHEN 'ReadyToCommit' THEN 1
                WHEN 'DraftReady' THEN 2
                WHEN 'AwaitingAiAnalysis' THEN 3
                WHEN 'Analyzing' THEN 4
                WHEN 'Pending' THEN 5
                WHEN 'Blocked' THEN 6
                WHEN 'Failed' THEN 7
                ELSE 99
            END,
            i."CreatedAt",
            i."Id"
        ) AS row_number
    FROM grading_item i
    WHERE i."CourseId" = :course_id
      AND i."SubmissionId" IS NOT NULL
)
SELECT
    r."Id",
    r."BatchId",
    r."AssignmentId",
    r."SubmissionId",
    r."Status",
    c."Id" AS "CanonicalId",
    c."Status" AS "CanonicalStatus"
FROM ranked r
JOIN ranked c
  ON c."CourseId" = :course_id
 AND c."AssignmentId" = r."AssignmentId"
 AND c."SubmissionId" = r."SubmissionId"
 AND c.row_number = 1
WHERE r.row_number > 1;

-- Nunca bloqueia automaticamente uma entrega que já foi revisada ou publicada.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM grading_duplicate_reconciliation
        WHERE "Status" IN ('Committed', 'ReadyToCommit')
    ) THEN
        RAISE EXCEPTION 'Reconciliação abortada: há duplicata protegida (Committed/ReadyToCommit).';
    END IF;
END $$;

SELECT
    COUNT(*) AS duplicate_rows,
    COUNT(DISTINCT "SubmissionId") AS affected_submissions,
    COUNT(DISTINCT "AssignmentId") AS affected_assignments
FROM grading_duplicate_reconciliation;

UPDATE grading_item i
SET
    "Status" = 'Blocked',
    "SuggestedGrade" = NULL,
    "Confidence" = 0,
    "DraftFeedback" = format(
        'Duplicata histórica da entrega %s; item canônico preservado (%s).',
        r."SubmissionId",
        r."CanonicalId"
    ),
    "PrivateNotesToTeacher" = format(
        'Reconciliação automática de duplicata histórica. Item canônico: %s.',
        r."CanonicalId"
    ),
    "CommitStatus" = 'NotReady',
    "CommitError" = NULL,
    "LeaseOwner" = NULL,
    "LeaseUntil" = NULL,
    "NextAttemptAt" = NULL,
    "LastErrorCode" = 'duplicate_submission',
    "ProcessingStage" = 'completed',
    "ProcessingStageUpdatedAt" = now(),
    "UpdatedAt" = now()
FROM grading_duplicate_reconciliation r
WHERE i."Id" = r."Id"
  AND i."LastErrorCode" IS DISTINCT FROM 'duplicate_submission';

-- Mantém o progresso dos lotes coerente com os estados reais dos itens.
WITH counters AS (
    SELECT
        b."Id",
        COUNT(i."Id") FILTER (
            WHERE i."Status" IN ('DraftReady', 'ReadyToCommit', 'Committed', 'Blocked', 'Failed')
        )::integer AS processed_items,
        COUNT(i."Id") FILTER (WHERE i."Status" = 'DraftReady')::integer AS ready_items,
        COUNT(i."Id") FILTER (WHERE i."Status" = 'Blocked')::integer AS blocked_items,
        COUNT(i."Id") FILTER (WHERE i."Status" = 'Failed')::integer AS failed_items
    FROM grading_batch b
    LEFT JOIN grading_item i ON i."BatchId" = b."Id"
    WHERE b."CourseId" = :course_id
    GROUP BY b."Id"
)
UPDATE grading_batch b
SET
    "ProcessedItems" = c.processed_items,
    "ReadyItems" = c.ready_items,
    "BlockedItems" = c.blocked_items,
    "FailedItems" = c.failed_items,
    "Status" = CASE
        WHEN b."Status" IN ('Processing', 'ReadyForReview')
            THEN CASE WHEN c.processed_items >= b."TotalItems"
                      THEN 'ReadyForReview'
                      ELSE 'Processing'
                 END
        ELSE b."Status"
    END,
    "UpdatedAt" = now()
FROM counters c
WHERE b."Id" = c."Id";

SELECT
    COALESCE(SUM(group_count - 1), 0) AS duplicate_rows_retained_for_audit,
    COALESCE(SUM(blocked_count), 0) AS duplicate_rows_blocked
FROM (
    SELECT
        COUNT(*) AS group_count,
        COUNT(*) FILTER (WHERE "Status" = 'Blocked') AS blocked_count
    FROM grading_item
    WHERE "CourseId" = :course_id
      AND "SubmissionId" IS NOT NULL
    GROUP BY "CourseId", "AssignmentId", "SubmissionId"
    HAVING COUNT(*) > 1
) duplicate_groups;

COMMIT;
