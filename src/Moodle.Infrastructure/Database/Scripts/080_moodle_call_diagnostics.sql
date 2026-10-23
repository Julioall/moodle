DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM "MOODLE_schema_versions"
               WHERE "Version" = 80
                 AND "Description" <> 'sanitized Moodle call diagnostics') THEN
        RAISE EXCEPTION 'Moodle call diagnostics schema ledger conflict at version 80; reconcile before upgrade';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS moodle_call_diagnostics (
    "EventId" character varying(32) PRIMARY KEY CHECK ("EventId" ~ '^[a-f0-9]{{32}}$'),
    "CallId" character varying(32) NOT NULL CHECK ("CallId" ~ '^[a-f0-9]{{32}}$'),
    "RetryGroupId" character varying(32) CHECK ("RetryGroupId" IS NULL OR "RetryGroupId" ~ '^[a-f0-9]{{32}}$'),
    "AttemptId" character varying(32) CHECK ("AttemptId" IS NULL OR "AttemptId" ~ '^[a-f0-9]{{32}}$'),
    "EventType" character varying(24) NOT NULL CHECK ("EventType" IN ('attempt_failed', 'call_completed')),
    "SchemaVersion" integer NOT NULL,
    "CatalogVersion" integer NOT NULL,
    "CorrelationId" character varying(32) CHECK ("CorrelationId" IS NULL OR "CorrelationId" ~ '^[a-f0-9]{{32}}$'),
    "SnapshotRunId" character varying(32) CHECK ("SnapshotRunId" IS NULL OR "SnapshotRunId" ~ '^[a-f0-9]{{32}}$'),
    "SnapshotRunItemId" character varying(32) CHECK ("SnapshotRunItemId" IS NULL OR "SnapshotRunItemId" ~ '^[a-f0-9]{{32}}$'),
    "ConnectionKey" character varying(18) NOT NULL CHECK ("ConnectionKey" = 'unknown' OR "ConnectionKey" ~ '^c_[a-f0-9]{{16}}$'),
    "MoodleFunction" character varying(64) NOT NULL CHECK ("MoodleFunction" IN (
        'unknown', 'core_calendar_create_calendar_events', 'core_course_get_categories', 'core_course_get_contents',
        'core_course_get_courses', 'core_course_get_courses_by_field',
        'core_course_get_enrolled_courses_by_timeline_classification', 'core_course_search_courses',
        'core_enrol_get_enrolled_users', 'core_enrol_get_users_courses', 'core_files_get_files',
        'core_grading_get_definitions', 'core_group_get_course_groups',
        'core_message_get_conversation_between_users', 'core_message_get_conversations',
        'core_message_send_instant_messages', 'core_message_send_messages_to_conversation',
        'core_reportbuilder_filters_reset', 'core_reportbuilder_list_reports',
        'core_reportbuilder_retrieve_report', 'core_reportbuilder_set_filters',
        'core_user_get_course_user_profiles', 'core_user_get_users_by_field', 'core_webservice_get_site_info',
        'gradereport_user_get_grade_items', 'gradereport_user_get_grades_table',
        'mod_assign_get_assignments', 'mod_assign_get_grades', 'mod_assign_get_participant',
        'mod_assign_get_submission_status', 'mod_assign_get_submissions', 'mod_assign_get_user_flags',
        'mod_assign_save_grade', 'mod_assign_save_grades', 'mod_forum_add_discussion',
        'mod_forum_add_discussion_post', 'mod_forum_can_add_discussion', 'mod_forum_get_discussion_posts',
        'mod_forum_get_forum_discussions', 'mod_forum_get_forum_discussions_paginated',
        'mod_forum_get_forums_by_courses', 'mod_quiz_get_quizzes_by_courses', 'mod_scorm_get_scorms_by_courses')),
    "OperationKind" character varying(16) NOT NULL CHECK ("OperationKind" IN ('read', 'write', 'unknown')),
    "ErrorCategory" character varying(64) CHECK ("ErrorCategory" IS NULL OR "ErrorCategory" IN (
        'transport_timeout', 'transport_connection_reset', 'transport_dns', 'transport_tls', 'transport_other',
        'http_rate_limited', 'http_server_error', 'http_client_error', 'moodle_permission_denied',
        'moodle_authentication_error', 'moodle_invalid_function', 'moodle_invalid_parameter',
        'moodle_api_error_unknown', 'unexpected_response', 'internal_error')),
    "ClassificationReason" character varying(32) CHECK ("ClassificationReason" IS NULL OR "ClassificationReason" IN (
        'endpoint_validation', 'timeout', 'token_acquisition', 'local_admission', 'local_authentication', 'local_permission',
        'local_function_policy', 'local_failure', 'circuit_open', 'diagnostic_limit')),
    "MoodleErrorCode" character varying(64) CHECK ("MoodleErrorCode" IS NULL OR "MoodleErrorCode" IN (
        'invalidtoken', 'invalid_token', 'wstokeninvalid', 'accessexception', 'access_exception',
        'webservice_access_exception', 'nopermissions', 'not_enrolled', 'invalidparameter', 'invalid_parameter',
        'invalidfunction', 'unknownfunction', 'functionnotavailable', 'invalidcourseid', 'course_not_found',
        'invalidactivityid', 'invalidcoursemodule')),
    "MoodleExceptionType" character varying(96) CHECK ("MoodleExceptionType" IS NULL OR "MoodleExceptionType" IN (
        'moodle_exception', 'moodle_access_exception', 'required_capability_exception',
        'webservice_access_exception', 'invalid_parameter_exception', 'moodle_invalid_parameter_exception',
        'invalid_function_exception')),
    "SafeMessage" character varying(256),
    "CodeState" character varying(16) NOT NULL CHECK ("CodeState" IN ('known', 'unknown', 'invalid', 'absent')),
    "ExceptionState" character varying(16) NOT NULL CHECK ("ExceptionState" IN ('known', 'unknown', 'invalid', 'absent')),
    "HttpStatus" integer CHECK ("HttpStatus" IS NULL OR "HttpStatus" BETWEEN 100 AND 599),
    "ResponseKind" character varying(16) NOT NULL CHECK ("ResponseKind" IN ('json', 'html', 'empty', 'other', 'unknown')),
    "TransportErrorType" character varying(32) CHECK ("TransportErrorType" IS NULL OR "TransportErrorType" IN (
        'timeout', 'connection_reset', 'dns', 'tls', 'network')),
    "SocketErrorCode" integer,
    "DurationMs" bigint NOT NULL CHECK ("DurationMs" >= 0),
    "AttemptNumber" integer,
    "AttemptCount" integer NOT NULL CHECK ("AttemptCount" >= 0),
    "RetryDisposition" character varying(32) CHECK ("RetryDisposition" IS NULL OR "RetryDisposition" IN ('scheduled', 'not_scheduled')),
    "RetryDelayMs" bigint CHECK ("RetryDelayMs" IS NULL OR "RetryDelayMs" >= 0),
    "Outcome" character varying(40) CHECK ("Outcome" IS NULL OR "Outcome" IN ('succeeded_after_retry', 'succeeded_after_external_retry', 'failed', 'circuit_open', 'cancelled')),
    "LastFailureEventId" character varying(32) CHECK ("LastFailureEventId" IS NULL OR "LastFailureEventId" ~ '^[a-f0-9]{{32}}$'),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    CONSTRAINT "CK_moodle_call_diagnostics_event_shape" CHECK (
        ("EventType" = 'attempt_failed' AND "AttemptId" IS NOT NULL AND "AttemptNumber" IS NOT NULL AND "AttemptNumber" > 0 AND "AttemptCount" >= "AttemptNumber" AND "ErrorCategory" IS NOT NULL AND "RetryDisposition" IS NOT NULL)
        OR
        ("EventType" = 'call_completed' AND "AttemptId" IS NULL AND "AttemptNumber" IS NULL AND "Outcome" IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_call_created"
    ON moodle_call_diagnostics ("CallId", "CreatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_retry_group_created"
    ON moodle_call_diagnostics ("RetryGroupId", "CreatedAtUtc") WHERE "RetryGroupId" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_connection_created"
    ON moodle_call_diagnostics ("ConnectionKey", "CreatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_snapshot_run_created"
    ON moodle_call_diagnostics ("SnapshotRunId", "CreatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_category_created"
    ON moodle_call_diagnostics ("ErrorCategory", "CreatedAtUtc");
CREATE INDEX IF NOT EXISTS "IX_moodle_call_diagnostics_created"
    ON moodle_call_diagnostics ("CreatedAtUtc");

INSERT INTO "MOODLE_schema_versions" ("Version", "Description", "AppliedAt")
VALUES (80, 'sanitized Moodle call diagnostics', now())
ON CONFLICT ("Version") DO NOTHING;
