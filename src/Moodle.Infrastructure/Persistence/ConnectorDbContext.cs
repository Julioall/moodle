using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using OpenIddict.EntityFrameworkCore.Models;
using System.Text.Json;
using Moodle.Domain;
using Moodle.Domain.Grading;

namespace Moodle.Infrastructure;

public sealed class ConnectorDbContext(DbContextOptions<ConnectorDbContext> options) : DbContext(options)
{
    public DbSet<ConnectorClientCredentialEntity> ConnectorClients => Set<ConnectorClientCredentialEntity>();
    public DbSet<UserAccountEntity> UserAccounts => Set<UserAccountEntity>();
    public DbSet<TeamEntity> Teams => Set<TeamEntity>();
    public DbSet<TeamMembershipEntity> TeamMemberships => Set<TeamMembershipEntity>();
    public DbSet<TeamInvitationEntity> TeamInvitations => Set<TeamInvitationEntity>();
    public DbSet<PermissionGroupEntity> PermissionGroups => Set<PermissionGroupEntity>();
    public DbSet<PermissionGroupPermissionEntity> PermissionGroupPermissions => Set<PermissionGroupPermissionEntity>();
    public DbSet<PermissionGroupMembershipEntity> PermissionGroupMemberships => Set<PermissionGroupMembershipEntity>();
    public DbSet<UserPermissionOverrideEntity> UserPermissionOverrides => Set<UserPermissionOverrideEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskParticipantEntity> TaskParticipants => Set<TaskParticipantEntity>();
    public DbSet<TaskReferenceEntity> TaskReferences => Set<TaskReferenceEntity>();
    public DbSet<TaskTagEntity> TaskTags => Set<TaskTagEntity>();
    public DbSet<TaskCommentEntity> TaskComments => Set<TaskCommentEntity>();
    public DbSet<TaskActivityEntity> TaskActivities => Set<TaskActivityEntity>();
    public DbSet<TaskDependencyEntity> TaskDependencies => Set<TaskDependencyEntity>();
    public DbSet<ReportJobEntity> ReportJobs => Set<ReportJobEntity>();
    public DbSet<Reports.Templates.ReportTemplateEntity> ReportTemplates => Set<Reports.Templates.ReportTemplateEntity>();
    public DbSet<Reports.Templates.ReportTemplateCatalogStateEntity> ReportTemplateCatalogStates => Set<Reports.Templates.ReportTemplateCatalogStateEntity>();
    public DbSet<ReportArtifactDeletionEntity> ReportArtifactDeletions => Set<ReportArtifactDeletionEntity>();
    public DbSet<CalendarEventEntity> CalendarEvents => Set<CalendarEventEntity>();
    public DbSet<EventRecurrenceEntity> EventRecurrences => Set<EventRecurrenceEntity>();
    public DbSet<EventRecurrenceDateEntity> EventRecurrenceDates => Set<EventRecurrenceDateEntity>();
    public DbSet<EventOccurrenceOverrideEntity> EventOccurrenceOverrides => Set<EventOccurrenceOverrideEntity>();
    public DbSet<EventReferenceEntity> EventReferences => Set<EventReferenceEntity>();
    public DbSet<EventTagEntity> EventTags => Set<EventTagEntity>();
    public DbSet<TaskEventLinkEntity> TaskEventLinks => Set<TaskEventLinkEntity>();
    public DbSet<FollowupEntity> Followups => Set<FollowupEntity>();
    public DbSet<PortalEvidenceEntity> PortalEvidence => Set<PortalEvidenceEntity>();
    public DbSet<DashboardAccessSnapshotEntity> DashboardAccessSnapshots => Set<DashboardAccessSnapshotEntity>();
    public DbSet<MoodleSnapshotEntity> MoodleSnapshots => Set<MoodleSnapshotEntity>();
    public DbSet<MoodleSyncStateEntity> MoodleSyncStates => Set<MoodleSyncStateEntity>();
    public DbSet<MoodleSnapshotRunEntity> MoodleSnapshotRuns => Set<MoodleSnapshotRunEntity>();
    public DbSet<MoodleSnapshotRunItemEntity> MoodleSnapshotRunItems => Set<MoodleSnapshotRunItemEntity>();
    public DbSet<MoodleCallDiagnosticEntity> MoodleCallDiagnostics => Set<MoodleCallDiagnosticEntity>();
    public DbSet<PlannerLinkEntity> PlannerLinks => Set<PlannerLinkEntity>();
    public DbSet<UserIgnoredCourseEntity> UserIgnoredCourses => Set<UserIgnoredCourseEntity>();
    public DbSet<UserTrackedCourseEntity> UserTrackedCourses => Set<UserTrackedCourseEntity>();
    public DbSet<PendingMoodleAction> PendingMoodleActions => Set<PendingMoodleAction>();
    public DbSet<ConfirmedMoodleAction> ConfirmedMoodleActions => Set<ConfirmedMoodleAction>();
    public DbSet<MoodleAuditLog> MoodleAuditLogs => Set<MoodleAuditLog>();
    public DbSet<MoodleResource> MoodleResources => Set<MoodleResource>();
    public DbSet<PlatformRequestMetricEntity> PlatformRequestMetrics => Set<PlatformRequestMetricEntity>();
    public DbSet<MoodleUserLink> MoodleUserLinks => Set<MoodleUserLink>();
    public DbSet<AssistedGradingBatch> GradingBatches => Set<AssistedGradingBatch>();
    public DbSet<GradingRun> GradingRuns => Set<GradingRun>();
    public DbSet<AssistedGradingItem> GradingItems => Set<AssistedGradingItem>();
    public DbSet<GradingArtifact> GradingArtifacts => Set<GradingArtifact>();
    public DbSet<GradingEvidence> GradingEvidence => Set<GradingEvidence>();
    public DbSet<GradingContextSnapshotDocument> GradingContextSnapshots => Set<GradingContextSnapshotDocument>();
    public DbSet<AiGradingProposalDocument> AiGradingProposals => Set<AiGradingProposalDocument>();
    public DbSet<GradingPublicationClaimEntity> GradingPublicationClaims => Set<GradingPublicationClaimEntity>();
    public DbSet<GradingPublicationAttemptEventEntity> GradingPublicationAttemptEvents => Set<GradingPublicationAttemptEventEntity>();
    public DbSet<GradingEvidencePackage> GradingEvidencePackages => Set<GradingEvidencePackage>();
    public DbSet<GradingEvidencePackageMember> GradingEvidencePackageMembers => Set<GradingEvidencePackageMember>();
    public DbSet<GradingEvidencePackageFile> GradingEvidencePackageFiles => Set<GradingEvidencePackageFile>();
    public DbSet<UserMemory> UserMemories => Set<UserMemory>();
    public DbSet<UserMemoryDocument> UserMemoryDocuments => Set<UserMemoryDocument>();
    public DbSet<OpenIddictEntityFrameworkCoreApplication> OAuthApplications => Set<OpenIddictEntityFrameworkCoreApplication>();
    public DbSet<OpenIddictEntityFrameworkCoreAuthorization> OAuthAuthorizations => Set<OpenIddictEntityFrameworkCoreAuthorization>();
    public DbSet<OpenIddictEntityFrameworkCoreScope> OAuthScopes => Set<OpenIddictEntityFrameworkCoreScope>();
    public DbSet<OpenIddictEntityFrameworkCoreToken> OAuthTokens => Set<OpenIddictEntityFrameworkCoreToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ConnectorClientCredentialEntity>();
        entity.ToTable("connector_clients");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Id).HasMaxLength(64);
        entity.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        entity.Property(x => x.ApiKeyHash).HasMaxLength(128);
        entity.Property(x => x.MoodleAlias).HasMaxLength(64).IsRequired();
        entity.Property(x => x.MoodleBaseUrl).HasMaxLength(512).IsRequired();
        entity.Property(x => x.MoodleUsernameEncrypted).IsRequired();
        entity.Property(x => x.MoodlePasswordEncrypted).IsRequired();
        entity.Property(x => x.MoodleTarget).HasMaxLength(32).IsRequired();
        entity.Property(x => x.IsDefault).IsRequired();
        entity.Property(x => x.IsActive).IsRequired();
        entity.Property(x => x.CanWrite).IsRequired();
        entity.Property(x => x.ValidationStatus).HasMaxLength(32).IsRequired();
        entity.Property(x => x.LastValidatedAtUtc);
        entity.Property(x => x.CreatedAtUtc).IsRequired();
        entity.Property(x => x.UpdatedAtUtc).IsRequired();

        entity.HasIndex(x => x.ApiKeyHash).IsUnique();
        entity.HasIndex(x => new { x.ClientId, x.MoodleAlias }).IsUnique();
        entity.HasIndex(x => new { x.ClientId, x.IsDefault });

        var userEntity = modelBuilder.Entity<UserAccountEntity>();
        userEntity.ToTable("user_accounts");
        userEntity.HasKey(x => x.Id);
        userEntity.Property(x => x.Name).HasMaxLength(200).IsRequired();
        userEntity.Property(x => x.Email).HasMaxLength(320).IsRequired();
        userEntity.Property(x => x.PasswordHash).IsRequired();
        userEntity.Property(x => x.ConnectorClientId).HasMaxLength(64);
        userEntity.Property(x => x.CreatedAtUtc).IsRequired();
        userEntity.Property(x => x.UpdatedAtUtc).IsRequired();
        userEntity.HasIndex(x => x.Email).IsUnique();

        var team = modelBuilder.Entity<TeamEntity>();
        team.ToTable("teams");
        team.HasKey(x => x.Id);
        team.Property(x => x.Name).HasMaxLength(200).IsRequired();
        team.Property(x => x.CreatedByUserId).IsRequired();
        team.Property(x => x.IsPersonal).IsRequired();
        team.Property(x => x.CreatedAtUtc).IsRequired();
        team.Property(x => x.UpdatedAtUtc).IsRequired();
        team.HasIndex(x => x.CreatedByUserId);

        var membership = modelBuilder.Entity<TeamMembershipEntity>();
        membership.ToTable("team_memberships");
        membership.HasKey(x => x.Id);
        membership.Property(x => x.Role).HasMaxLength(32).IsRequired();
        membership.Property(x => x.ScopesJson).HasColumnType("jsonb").IsRequired();
        membership.Property(x => x.IsActive).IsRequired();
        membership.Property(x => x.CreatedAtUtc).IsRequired();
        membership.Property(x => x.UpdatedAtUtc).IsRequired();
        membership.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
        membership.HasIndex(x => new { x.UserId, x.IsActive });

        var invitation = modelBuilder.Entity<TeamInvitationEntity>();
        invitation.ToTable("team_invitations");
        invitation.HasKey(x => x.Id);
        invitation.Property(x => x.InviteeEmail).HasMaxLength(320).IsRequired();
        invitation.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        invitation.Property(x => x.Role).HasMaxLength(32).IsRequired();
        invitation.Property(x => x.ScopesJson).HasColumnType("jsonb").IsRequired();
        invitation.Property(x => x.InvitedByUserId).IsRequired();
        invitation.Property(x => x.ExpiresAtUtc).IsRequired();
        invitation.Property(x => x.CreatedAtUtc).IsRequired();
        invitation.HasIndex(x => x.TokenHash).IsUnique();
        invitation.HasIndex(x => new { x.TeamId, x.InviteeEmail, x.AcceptedAtUtc });

        var permissionGroup = modelBuilder.Entity<PermissionGroupEntity>();
        permissionGroup.ToTable("permission_groups");
        permissionGroup.HasKey(x => x.Id);
        permissionGroup.Property(x => x.Name).HasMaxLength(120).IsRequired();
        permissionGroup.Property(x => x.Description).HasMaxLength(500).IsRequired();
        permissionGroup.Property(x => x.CommonRoleKey).HasMaxLength(64);
        permissionGroup.Property(x => x.CreatedByUserId).IsRequired();
        permissionGroup.Property(x => x.CreatedAtUtc).IsRequired();
        permissionGroup.Property(x => x.UpdatedAtUtc).IsRequired();

        var groupPermission = modelBuilder.Entity<PermissionGroupPermissionEntity>();
        groupPermission.ToTable("permission_group_permissions");
        groupPermission.HasKey(x => x.Id);
        groupPermission.Property(x => x.Permission).HasMaxLength(120).IsRequired();
        groupPermission.HasIndex(x => new { x.GroupId, x.Permission }).IsUnique();

        var groupMembership = modelBuilder.Entity<PermissionGroupMembershipEntity>();
        groupMembership.ToTable("permission_group_memberships");
        groupMembership.HasKey(x => x.Id);
        groupMembership.HasIndex(x => new { x.GroupId, x.UserId }).IsUnique();

        var userOverride = modelBuilder.Entity<UserPermissionOverrideEntity>();
        userOverride.ToTable("user_permission_overrides");
        userOverride.HasKey(x => x.Id);
        userOverride.Property(x => x.Permission).HasMaxLength(120).IsRequired();
        userOverride.Property(x => x.IsAllowed).IsRequired();
        userOverride.Property(x => x.ChangedByUserId).IsRequired();
        userOverride.HasIndex(x => new { x.UserId, x.Permission }).IsUnique();

        var appTask = modelBuilder.Entity<TaskEntity>();
        appTask.ToTable("app_tasks");
        appTask.HasKey(x => x.Id);
        appTask.Property(x => x.Title).HasMaxLength(240).IsRequired();
        appTask.Property(x => x.Description).HasMaxLength(4000);
        appTask.Property(x => x.Status).HasMaxLength(32).IsRequired();
        appTask.Property(x => x.Priority).HasMaxLength(32).IsRequired();
        appTask.Property(x => x.CreatedAt).IsRequired();
        appTask.Property(x => x.UpdatedAt).IsRequired();
        appTask.Property(x => x.ActionType).HasMaxLength(80);
        appTask.Property(x => x.ScheduleHint).HasMaxLength(240);
        appTask.Property(x => x.ExternalUid).HasMaxLength(240);
        appTask.Property(x => x.ExternalSource).HasMaxLength(80);
        appTask.Property(x => x.Version).IsConcurrencyToken();
        appTask.HasOne<TaskEntity>().WithMany().HasForeignKey(x => x.ParentTaskId).OnDelete(DeleteBehavior.Cascade);
        appTask.HasIndex(x => new { x.OwnerId, x.Status, x.DueAt });
        appTask.HasIndex(x => new { x.OwnerId, x.ExternalSource, x.ExternalUid }).IsUnique();

        var participant = modelBuilder.Entity<TaskParticipantEntity>();
        participant.ToTable("task_participants"); participant.HasKey(x => x.Id);
        participant.Property(x => x.Role).HasMaxLength(16).IsRequired();
        participant.HasIndex(x => new { x.TaskId, x.UserId }).IsUnique();
        participant.HasIndex(x => x.TaskId).IsUnique().HasFilter("\"Role\" = 'owner'");
        var taskReference = modelBuilder.Entity<TaskReferenceEntity>();
        taskReference.ToTable("task_references"); taskReference.HasKey(x => x.Id);
        taskReference.Property(x => x.ReferenceType).HasMaxLength(32).IsRequired(); taskReference.Property(x => x.ReferenceId).HasMaxLength(200).IsRequired(); taskReference.Property(x => x.ReferenceName).HasMaxLength(240); taskReference.Property(x => x.ConnectionRef).HasMaxLength(64); taskReference.Property(x => x.Relation).HasMaxLength(64);
        taskReference.HasIndex(x => new { x.TaskId, x.ReferenceType, x.ReferenceId, x.ConnectionRef }).IsUnique();
        var taskTag = modelBuilder.Entity<TaskTagEntity>();
        taskTag.ToTable("task_tags"); taskTag.HasKey(x => new { x.TaskId, x.NormalizedValue }); taskTag.Property(x => x.Value).HasMaxLength(64); taskTag.Property(x => x.NormalizedValue).HasMaxLength(64);
        var comment = modelBuilder.Entity<TaskCommentEntity>(); comment.ToTable("task_comments"); comment.HasKey(x => x.Id); comment.Property(x => x.Content).HasMaxLength(4000).IsRequired();
        var activity = modelBuilder.Entity<TaskActivityEntity>(); activity.ToTable("task_activities"); activity.HasKey(x => x.Id); activity.Property(x => x.EventType).HasMaxLength(80).IsRequired(); activity.Property(x => x.Data).HasColumnType("jsonb");
        var dependency = modelBuilder.Entity<TaskDependencyEntity>(); dependency.ToTable("task_dependencies"); dependency.HasKey(x => new { x.TaskId, x.DependsOnTaskId });

        var reportJob = modelBuilder.Entity<ReportJobEntity>();
        var reportTemplate = modelBuilder.Entity<Reports.Templates.ReportTemplateEntity>();
        reportTemplate.ToTable("report_templates");
        reportTemplate.HasKey(t => t.Id);
        reportTemplate.Property(t => t.Name).HasMaxLength(120).IsRequired();
        reportTemplate.Property(t => t.Description).HasMaxLength(500);
        reportTemplate.Property(t => t.SourceId).HasMaxLength(64).IsRequired();
        reportTemplate.Property(t => t.DefinitionJson).HasColumnType("jsonb").IsRequired();
        reportTemplate.Property(t => t.Revision).IsConcurrencyToken();
        reportTemplate.HasIndex(t => new { t.OwnerId, t.UpdatedAt, t.Id });
        reportTemplate.HasOne<UserAccountEntity>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
        var reportTemplateCatalogState = modelBuilder.Entity<Reports.Templates.ReportTemplateCatalogStateEntity>();
        reportTemplateCatalogState.ToTable("report_template_catalog_states");
        reportTemplateCatalogState.HasKey(state => state.OwnerId);
        reportTemplateCatalogState.Property(state => state.DefaultsSeededAt).IsRequired();
        reportTemplateCatalogState.HasOne<UserAccountEntity>().WithMany().HasForeignKey(state => state.OwnerId).OnDelete(DeleteBehavior.Cascade);
        reportJob.ToTable("report_jobs");
        reportJob.HasKey(x => x.Id);
        reportJob.Property(x => x.ClientId).HasMaxLength(200).IsRequired();
        reportJob.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        reportJob.Property(x => x.ReportType).HasMaxLength(64).IsRequired();
        reportJob.Property(x => x.ScopeType).HasMaxLength(32).IsRequired();
        reportJob.Property(x => x.CategoryPath).HasMaxLength(500);
        reportJob.Property(x => x.CourseId).HasMaxLength(64);
        reportJob.Property(x => x.CourseIdsJson);
        reportJob.Property(x => x.CourseNamesJson);
        reportJob.Property(x => x.TemplateNameSnapshot).HasMaxLength(120);
        reportJob.Property(x => x.DefinitionSnapshotJson).HasColumnType("jsonb");
        reportJob.Property(x => x.SelectionSnapshotJson).HasColumnType("jsonb");
        reportJob.Property(x => x.ResultMetadataJson).HasColumnType("jsonb");
        reportJob.Property(x => x.Status).HasMaxLength(32).IsRequired();
        reportJob.Property(x => x.FileName).HasMaxLength(240);
        reportJob.Property(x => x.ContentType).HasMaxLength(120);
        reportJob.Property(x => x.FileSizeBytes).IsRequired();
        reportJob.Property(x => x.ArtifactBlobKey).HasMaxLength(64);
        reportJob.Property(x => x.ContentText);
        reportJob.Property(x => x.ContentBase64);
        reportJob.Property(x => x.ErrorMessage).HasMaxLength(4000);
        reportJob.Property(x => x.WorkerLeaseId).IsConcurrencyToken();
        reportJob.Property(x => x.LeaseUntil);
        reportJob.Property(x => x.RequestedAt).IsRequired();
        reportJob.Property(x => x.UpdatedAt).IsRequired();
        reportJob.HasIndex(x => new { x.OwnerId, x.UpdatedAt });
        reportJob.HasIndex(x => new { x.Status, x.RequestedAt });
        reportJob.HasIndex(x => new { x.Status, x.LeaseUntil });
        reportJob.HasIndex(x => x.ArtifactBlobKey).IsUnique().HasFilter("\"ArtifactBlobKey\" IS NOT NULL");

        var reportArtifactDeletion = modelBuilder.Entity<ReportArtifactDeletionEntity>();
        reportArtifactDeletion.ToTable("report_artifact_deletions");
        reportArtifactDeletion.HasKey(x => x.Id);
        reportArtifactDeletion.Property(x => x.ArtifactBlobKey).HasMaxLength(64).IsRequired();
        reportArtifactDeletion.Property(x => x.RequestedAt).IsRequired();
        reportArtifactDeletion.Property(x => x.AttemptCount).IsRequired();
        reportArtifactDeletion.Property(x => x.NextAttemptAt).IsRequired();
        reportArtifactDeletion.Property(x => x.LastError).HasMaxLength(500);
        reportArtifactDeletion.HasIndex(x => new { x.NextAttemptAt, x.RequestedAt });

        var calendarEvent = modelBuilder.Entity<CalendarEventEntity>();
        calendarEvent.ToTable("app_calendar_events");
        calendarEvent.HasKey(x => x.Id);
        calendarEvent.Property(x => x.Title).HasMaxLength(240).IsRequired();
        calendarEvent.Property(x => x.Description).HasMaxLength(4000);
        calendarEvent.Property(x => x.Type).HasMaxLength(32).IsRequired();
        calendarEvent.Property(x => x.StartAt).IsRequired();
        calendarEvent.Property(x => x.CreatedAt).IsRequired();
        calendarEvent.Property(x => x.UpdatedAt).IsRequired();
        calendarEvent.Property(x => x.ExternalUid).HasMaxLength(240);
        calendarEvent.Property(x => x.ExternalSource).HasMaxLength(80);
        calendarEvent.Property(x => x.TimeZoneId).HasMaxLength(80).IsRequired(); calendarEvent.Property(x => x.Location).HasMaxLength(500); calendarEvent.Property(x => x.AvailabilityStatus).HasMaxLength(16).IsRequired(); calendarEvent.Property(x => x.Source).HasMaxLength(80).IsRequired(); calendarEvent.Property(x => x.Version).IsConcurrencyToken();
        calendarEvent.HasIndex(x => new { x.OwnerId, x.StartAt });
        calendarEvent.HasIndex(x => new { x.OwnerId, x.TimeZoneId });
        calendarEvent.HasIndex(x => new { x.OwnerId, x.ExternalSource, x.ExternalUid }).IsUnique();

        var recurrence = modelBuilder.Entity<EventRecurrenceEntity>(); recurrence.ToTable("event_recurrences"); recurrence.HasKey(x => x.EventId); recurrence.Property(x => x.RRule).HasMaxLength(1000).IsRequired();
        var recurrenceDate = modelBuilder.Entity<EventRecurrenceDateEntity>(); recurrenceDate.ToTable("event_recurrence_dates"); recurrenceDate.HasKey(x => new { x.EventId, x.OccurrenceStartAt, x.Kind }); recurrenceDate.Property(x => x.Kind).HasMaxLength(8);
        var occurrenceOverride = modelBuilder.Entity<EventOccurrenceOverrideEntity>(); occurrenceOverride.ToTable("event_occurrence_overrides"); occurrenceOverride.HasKey(x => new { x.EventId, x.OriginalStartAt }); occurrenceOverride.Property(x => x.Title).HasMaxLength(240); occurrenceOverride.Property(x => x.Description).HasMaxLength(4000);
        var eventReference = modelBuilder.Entity<EventReferenceEntity>(); eventReference.ToTable("event_references"); eventReference.HasKey(x => x.Id); eventReference.Property(x => x.ReferenceType).HasMaxLength(32); eventReference.Property(x => x.ReferenceId).HasMaxLength(200); eventReference.Property(x => x.ReferenceName).HasMaxLength(240); eventReference.Property(x => x.ConnectionRef).HasMaxLength(64); eventReference.Property(x => x.Relation).HasMaxLength(64); eventReference.HasIndex(x => new { x.EventId, x.ReferenceType, x.ReferenceId, x.ConnectionRef }).IsUnique();
        var eventTag = modelBuilder.Entity<EventTagEntity>(); eventTag.ToTable("event_tags"); eventTag.HasKey(x => new { x.EventId, x.NormalizedValue }); eventTag.Property(x => x.Value).HasMaxLength(64); eventTag.Property(x => x.NormalizedValue).HasMaxLength(64);
        var taskEvent = modelBuilder.Entity<TaskEventLinkEntity>(); taskEvent.ToTable("task_event_links"); taskEvent.HasKey(x => x.Id); taskEvent.Property(x => x.Relation).HasMaxLength(32); taskEvent.HasIndex(x => new { x.TaskId, x.EventId, x.OccurrenceStartAt }).IsUnique();

        var plannerLink = modelBuilder.Entity<PlannerLinkEntity>();
        plannerLink.ToTable("planner_links");
        plannerLink.HasKey(x => x.Id);
        plannerLink.Property(x => x.ReferenceType).HasMaxLength(32).IsRequired();
        plannerLink.Property(x => x.ReferenceId).HasMaxLength(200).IsRequired();
        plannerLink.Property(x => x.ReferenceName).HasMaxLength(240);
        plannerLink.Property(x => x.ConnectionRef).HasMaxLength(64);
        plannerLink.Property(x => x.ParentReferenceType).HasMaxLength(32);
        plannerLink.Property(x => x.ParentReferenceId).HasMaxLength(200);
        plannerLink.Property(x => x.ParentReferenceName).HasMaxLength(240);
        plannerLink.Property(x => x.CreatedAt).IsRequired();
        plannerLink.HasIndex(x => new { x.OwnerId, x.TaskId });
        plannerLink.HasIndex(x => new { x.OwnerId, x.CalendarEventId });
        plannerLink.HasIndex(x => new { x.OwnerId, x.ReferenceType, x.ReferenceId });

        var followup = modelBuilder.Entity<FollowupEntity>();
        followup.ToTable("app_followups");
        followup.HasKey(x => x.Id);
        followup.Property(x => x.StudentRef).HasMaxLength(200).IsRequired();
        followup.Property(x => x.StudentName).HasMaxLength(240);
        followup.Property(x => x.CourseRef).HasMaxLength(200);
        followup.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        followup.Property(x => x.Reason).HasMaxLength(64);
        followup.Property(x => x.Action).HasMaxLength(64);
        followup.Property(x => x.Status).HasMaxLength(64);
        followup.Property(x => x.Notes).HasMaxLength(4000).IsRequired();
        followup.Property(x => x.OccurredAt).IsRequired();
        followup.Property(x => x.CreatedAt).IsRequired();
        followup.HasIndex(x => new { x.OwnerId, x.OccurredAt });

        var evidence = modelBuilder.Entity<PortalEvidenceEntity>();
        evidence.ToTable("portal_evidence");
        evidence.HasKey(x => x.Id);
        evidence.Property(x => x.ConnectionAlias).HasMaxLength(64);
        evidence.Property(x => x.CourseId).HasMaxLength(64).IsRequired();
        evidence.Property(x => x.StudentId).HasMaxLength(64);
        evidence.Property(x => x.ActivityId).HasMaxLength(64);
        evidence.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        evidence.Property(x => x.Title).HasMaxLength(240).IsRequired();
        evidence.Property(x => x.Details).HasMaxLength(4000).IsRequired();
        evidence.Property(x => x.Source).HasMaxLength(64).IsRequired();
        evidence.Property(x => x.ObservedAt).IsRequired();
        evidence.Property(x => x.CreatedAt).IsRequired();
        evidence.HasIndex(x => new { x.OwnerId, x.CourseId, x.ObservedAt });
        evidence.HasIndex(x => new { x.OwnerId, x.StudentId, x.Kind, x.ActivityId });

        var dashboardAccessSnapshot = modelBuilder.Entity<DashboardAccessSnapshotEntity>();
        dashboardAccessSnapshot.ToTable("dashboard_access_snapshots");
        dashboardAccessSnapshot.HasKey(x => x.Id);
        dashboardAccessSnapshot.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        dashboardAccessSnapshot.Property(x => x.SnapshotDate).HasColumnType("date").IsRequired();
        dashboardAccessSnapshot.Property(x => x.CoursesInScope).IsRequired();
        dashboardAccessSnapshot.Property(x => x.TotalStudents).IsRequired();
        dashboardAccessSnapshot.Property(x => x.RecentStudents).IsRequired();
        dashboardAccessSnapshot.Property(x => x.LowAccessStudents).IsRequired();
        dashboardAccessSnapshot.Property(x => x.StaleStudents).IsRequired();
        dashboardAccessSnapshot.Property(x => x.NeverAccessedStudents).IsRequired();
        dashboardAccessSnapshot.Property(x => x.StudentsAtRisk).IsRequired();
        dashboardAccessSnapshot.Property(x => x.GeneratedAt).IsRequired();
        dashboardAccessSnapshot.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.SnapshotDate }).IsUnique();

        var moodleSnapshot = modelBuilder.Entity<MoodleSnapshotEntity>();
        moodleSnapshot.ToTable("moodle_snapshots");
        moodleSnapshot.HasKey(x => x.Id);
        moodleSnapshot.Property(x => x.ConnectionId).HasMaxLength(128).IsRequired();
        moodleSnapshot.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        moodleSnapshot.Property(x => x.SnapshotType).HasMaxLength(32).IsRequired();
        moodleSnapshot.Property(x => x.CourseId).HasMaxLength(64).IsRequired();
        moodleSnapshot.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        moodleSnapshot.Property(x => x.LastRunId);
        moodleSnapshot.Property(x => x.Tier).HasMaxLength(16).IsRequired();
        moodleSnapshot.Property(x => x.FrozenAt);
        moodleSnapshot.Property(x => x.UpdatedAt).IsRequired();
        moodleSnapshot.Property(x => x.LastError).HasMaxLength(4000);
        moodleSnapshot.Property(x => x.PayloadHash).HasMaxLength(64);
        moodleSnapshot.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.SnapshotType, x.CourseId }).IsUnique();
        moodleSnapshot.HasIndex(x => new { x.OwnerId, x.ConnectionId, x.SnapshotType, x.CourseId })
            .IsUnique()
            .HasFilter("\"ConnectionId\" <> ''");
        moodleSnapshot.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.UpdatedAt });

        var moodleSyncState = modelBuilder.Entity<MoodleSyncStateEntity>();
        moodleSyncState.ToTable("moodle_sync_states");
        moodleSyncState.HasKey(x => x.Id);
        moodleSyncState.Property(x => x.ConnectionId).HasMaxLength(128).IsRequired();
        moodleSyncState.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        moodleSyncState.Property(x => x.Dataset).HasMaxLength(32).IsRequired();
        moodleSyncState.Property(x => x.WorkPolicy).HasMaxLength(64).IsRequired();
        moodleSyncState.Property(x => x.CourseId).HasMaxLength(64).IsRequired();
        moodleSyncState.Property(x => x.Status).HasMaxLength(32).IsRequired();
        moodleSyncState.Property(x => x.LastError).HasMaxLength(4000);
        moodleSyncState.Property(x => x.LastAttemptAt);
        moodleSyncState.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        moodleSyncState.Property(x => x.UserExternalId).HasMaxLength(200).IsRequired();
        moodleSyncState.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.Dataset, x.CourseId }).IsUnique();
        moodleSyncState.HasIndex(x => new { x.OwnerId, x.ConnectionId, x.Dataset, x.CourseId })
            .IsUnique()
            .HasFilter("\"ConnectionId\" <> ''");
        moodleSyncState.HasIndex(x => new { x.Status, x.NextSyncAt, x.Priority });

        var moodleSnapshotRun = modelBuilder.Entity<MoodleSnapshotRunEntity>();
        moodleSnapshotRun.ToTable("moodle_snapshot_runs");
        moodleSnapshotRun.HasKey(x => x.Id);
        moodleSnapshotRun.Property(x => x.ConnectionId).HasMaxLength(128).IsRequired();
        moodleSnapshotRun.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        moodleSnapshotRun.Property(x => x.Status).HasMaxLength(32).IsRequired();
        moodleSnapshotRun.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
        moodleSnapshotRun.Property(x => x.WorkerId).HasMaxLength(128).IsRequired();
        moodleSnapshotRun.Property(x => x.SynchronizerVersion).HasMaxLength(128).IsRequired();
        moodleSnapshotRun.Property(x => x.Error).HasMaxLength(4000);
        moodleSnapshotRun.HasIndex(x => new { x.OwnerId, x.ConnectionId, x.StartedAt });
        moodleSnapshotRun.HasIndex(x => new { x.Status, x.StartedAt });

        var moodleSnapshotRunItem = modelBuilder.Entity<MoodleSnapshotRunItemEntity>();
        moodleSnapshotRunItem.ToTable("moodle_snapshot_run_items");
        moodleSnapshotRunItem.HasKey(x => x.Id);
        moodleSnapshotRunItem.Property(x => x.Dataset).HasMaxLength(32).IsRequired();
        moodleSnapshotRunItem.Property(x => x.ResourceId).HasMaxLength(128).IsRequired();
        moodleSnapshotRunItem.Property(x => x.Status).HasMaxLength(32).IsRequired();
        moodleSnapshotRunItem.Property(x => x.PayloadHash).HasMaxLength(64);
        moodleSnapshotRunItem.Property(x => x.Error).HasMaxLength(4000);
        moodleSnapshotRunItem.HasIndex(x => new { x.RunId, x.Dataset, x.ResourceId }).IsUnique();
        moodleSnapshotRunItem.HasIndex(x => new { x.Dataset, x.Status, x.StartedAt });

        var moodleCallDiagnostic = modelBuilder.Entity<MoodleCallDiagnosticEntity>();
        moodleCallDiagnostic.ToTable("moodle_call_diagnostics");
        moodleCallDiagnostic.HasKey(x => x.EventId);
        moodleCallDiagnostic.Property(x => x.EventId).HasMaxLength(32).IsRequired();
        moodleCallDiagnostic.Property(x => x.CallId).HasMaxLength(32).IsRequired();
        moodleCallDiagnostic.Property(x => x.RetryGroupId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.AttemptId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.EventType).HasMaxLength(24).IsRequired();
        moodleCallDiagnostic.Property(x => x.CorrelationId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.SnapshotRunId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.SnapshotRunItemId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.ConnectionKey).HasMaxLength(18).IsRequired();
        moodleCallDiagnostic.Property(x => x.MoodleFunction).HasMaxLength(64).IsRequired();
        moodleCallDiagnostic.Property(x => x.OperationKind).HasMaxLength(16).IsRequired();
        moodleCallDiagnostic.Property(x => x.ErrorCategory).HasMaxLength(64);
        moodleCallDiagnostic.Property(x => x.ClassificationReason).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.MoodleErrorCode).HasMaxLength(64);
        moodleCallDiagnostic.Property(x => x.MoodleExceptionType).HasMaxLength(96);
        moodleCallDiagnostic.Property(x => x.SafeMessage).HasMaxLength(256);
        moodleCallDiagnostic.Property(x => x.CodeState).HasMaxLength(16).IsRequired();
        moodleCallDiagnostic.Property(x => x.ExceptionState).HasMaxLength(16).IsRequired();
        moodleCallDiagnostic.Property(x => x.ResponseKind).HasMaxLength(16).IsRequired();
        moodleCallDiagnostic.Property(x => x.TransportErrorType).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.RetryDisposition).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.Outcome).HasMaxLength(40);
        moodleCallDiagnostic.Property(x => x.LastFailureEventId).HasMaxLength(32);
        moodleCallDiagnostic.Property(x => x.RawPayloadState).HasMaxLength(24).IsRequired();
        moodleCallDiagnostic.Property(x => x.RawPayloadSha256).HasMaxLength(64);
        moodleCallDiagnostic.Property(x => x.RawPayloadContentType).HasMaxLength(128);
        moodleCallDiagnostic.Property(x => x.CreatedAtUtc).IsRequired();
        moodleCallDiagnostic.HasIndex(x => new { x.CallId, x.CreatedAtUtc });
        moodleCallDiagnostic.HasIndex(x => new { x.RetryGroupId, x.CreatedAtUtc });
        moodleCallDiagnostic.HasIndex(x => new { x.ConnectionKey, x.CreatedAtUtc });
        moodleCallDiagnostic.HasIndex(x => new { x.SnapshotRunId, x.CreatedAtUtc });
        moodleCallDiagnostic.HasIndex(x => new { x.ErrorCategory, x.CreatedAtUtc });
        moodleCallDiagnostic.HasIndex(x => x.CreatedAtUtc);

        var ignoredCourse = modelBuilder.Entity<UserIgnoredCourseEntity>();
        ignoredCourse.ToTable("user_ignored_courses");
        ignoredCourse.HasKey(x => x.Id);
        ignoredCourse.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        ignoredCourse.Property(x => x.CourseId).HasMaxLength(64).IsRequired();
        ignoredCourse.Property(x => x.CreatedAt).IsRequired();
        ignoredCourse.Property(x => x.UpdatedAt).IsRequired();
        ignoredCourse.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.CourseId }).IsUnique();
        ignoredCourse.HasIndex(x => new { x.OwnerId, x.ConnectionAlias });

        var trackedCourse = modelBuilder.Entity<UserTrackedCourseEntity>();
        trackedCourse.ToTable("user_tracked_courses");
        trackedCourse.HasKey(x => x.Id);
        trackedCourse.Property(x => x.ConnectionAlias).HasMaxLength(64).IsRequired();
        trackedCourse.Property(x => x.CourseId).HasMaxLength(64).IsRequired();
        trackedCourse.Property(x => x.CreatedAt).IsRequired();
        trackedCourse.Property(x => x.UpdatedAt).IsRequired();
        trackedCourse.HasIndex(x => new { x.OwnerId, x.ConnectionAlias, x.CourseId }).IsUnique();
        trackedCourse.HasIndex(x => new { x.OwnerId, x.ConnectionAlias });

        var pendingAction = modelBuilder.Entity<PendingMoodleAction>();
        pendingAction.ToTable("moodle_pending_actions");
        pendingAction.HasKey(x => x.Id);
        pendingAction.Property(x => x.ToolName).HasMaxLength(120).IsRequired();
        pendingAction.Property(x => x.RiskLevel).HasConversion<int>().IsRequired();
        pendingAction.Property(x => x.CreatedBySubject).HasMaxLength(200).IsRequired();
        pendingAction.Property(x => x.CreatedByEmail).HasMaxLength(320);
        pendingAction.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        pendingAction.Property(x => x.PreviewJson).HasColumnType("jsonb").IsRequired();
        pendingAction.Property(x => x.ConfirmationText).HasMaxLength(500).IsRequired();
        pendingAction.Property(x => x.Status).HasConversion<int>().IsRequired();
        pendingAction.Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();
        pendingAction.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        pendingAction.Property(x => x.ExecutionOwner).HasMaxLength(200);
        pendingAction.Property(x => x.LastExecutionError).HasMaxLength(4000);
        pendingAction.HasIndex(x => new { x.Status, x.ExecutionLeaseUntil });
        pendingAction.HasIndex(x => new { x.Status, x.NextExecutionAttemptAt });

        var publicationClaim = modelBuilder.Entity<GradingPublicationClaimEntity>();
        publicationClaim.ToTable("grading_publication_claim");
        publicationClaim.HasKey(x => x.Id);
        publicationClaim.Property(x => x.PendingActionId);
        publicationClaim.Property(x => x.ConnectionKey).HasMaxLength(256).IsRequired();
        publicationClaim.Property(x => x.WorkerId).HasMaxLength(200);
        publicationClaim.Property(x => x.FencingToken);
        publicationClaim.Property(x => x.Status).HasMaxLength(40).IsRequired();
        publicationClaim.HasIndex(x => new
        {
            x.ConnectionKey,
            x.AssignmentId,
            x.MoodleUserId,
            x.AttemptNumber,
            x.Status
        });
        publicationClaim.HasIndex(x => new { x.Status, x.ExpiresAt });
        publicationClaim.HasIndex(x => x.PublicationId);
        publicationClaim.HasIndex(x => x.PendingActionId);
        publicationClaim.HasIndex(x => new
        {
            x.ConnectionKey,
            x.AssignmentId,
            x.MoodleUserId,
            x.AttemptNumber
        })
            .IsUnique()
            .HasFilter("\"Status\" IN ('AwaitingConfirmation', 'Authorized', 'Executing', 'ExecutionUnknown')");

        var publicationAttemptEvent = modelBuilder.Entity<GradingPublicationAttemptEventEntity>();
        publicationAttemptEvent.ToTable("grading_publication_attempt_events");
        publicationAttemptEvent.HasKey(x => x.Id);
        publicationAttemptEvent.Property(x => x.EventType).HasMaxLength(48).IsRequired();
        publicationAttemptEvent.Property(x => x.ItemState).HasMaxLength(40).IsRequired();
        publicationAttemptEvent.Property(x => x.DispatchGroupId);
        publicationAttemptEvent.Property(x => x.WorkerId).HasMaxLength(200);
        publicationAttemptEvent.Property(x => x.ErrorCode).HasMaxLength(120);
        publicationAttemptEvent.Property(x => x.EvidenceKind).HasMaxLength(48);
        publicationAttemptEvent.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Cascade);
        publicationAttemptEvent.HasIndex(x => new
        {
            x.PublicationId,
            x.GradingItemId,
            x.DispatchAttemptNumber,
            x.CreatedAtUtc
        });
        publicationAttemptEvent.HasIndex(x => new { x.PendingActionId, x.CreatedAtUtc });
        publicationAttemptEvent.HasIndex(x => new { x.DispatchGroupId, x.GradingItemId });
        pendingAction.HasIndex(x => x.CorrelationId);
        pendingAction.HasIndex(x => new { x.CreatedBySubject, x.Status });

        var confirmedAction = modelBuilder.Entity<ConfirmedMoodleAction>();
        confirmedAction.ToTable("moodle_confirmed_actions");
        confirmedAction.HasKey(x => x.Id);
        confirmedAction.Property(x => x.ToolName).HasMaxLength(120).IsRequired();
        confirmedAction.Property(x => x.ConfirmedBySubject).HasMaxLength(200).IsRequired();
        confirmedAction.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        confirmedAction.HasIndex(x => x.PendingActionId).IsUnique();

        var auditLog = modelBuilder.Entity<MoodleAuditLog>();
        auditLog.ToTable("moodle_audit_logs");
        auditLog.HasKey(x => x.Id);
        auditLog.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        auditLog.Property(x => x.BatchJobId);
        auditLog.Property(x => x.ToolName).HasMaxLength(120).IsRequired();
        auditLog.Property(x => x.RiskLevel).HasConversion<int>().IsRequired();
        auditLog.Property(x => x.ActorSubject).HasMaxLength(200).IsRequired();
        auditLog.Property(x => x.ActorEmail).HasMaxLength(320);
        auditLog.Property(x => x.MoodleConnectionId).HasMaxLength(128);
        auditLog.Property(x => x.MoodleConnectionAlias).HasMaxLength(128);
        auditLog.Property(x => x.MoodleFunction).HasMaxLength(120);
        auditLog.Property(x => x.PendingActionId);
        auditLog.Property(x => x.StartedAt);
        auditLog.Property(x => x.FinishedAt);
        auditLog.Property(x => x.DurationMs);
        auditLog.Property(x => x.RequestSanitizedJson).HasColumnType("jsonb").IsRequired();
        auditLog.Property(x => x.ResponseSummaryJson).HasColumnType("jsonb").IsRequired();
        auditLog.Property(x => x.Status).HasMaxLength(80).IsRequired();
        auditLog.Property(x => x.ErrorCode).HasMaxLength(120);
        auditLog.HasIndex(x => x.CorrelationId);
        auditLog.HasIndex(x => new { x.BatchJobId, x.CreatedAt });
        auditLog.HasIndex(x => new { x.ActorSubject, x.CreatedAt });
        auditLog.HasIndex(x => new { x.MoodleConnectionId, x.CreatedAt });

        var platformRequestMetric = modelBuilder.Entity<PlatformRequestMetricEntity>();
        platformRequestMetric.ToTable("platform_request_metrics");
        platformRequestMetric.HasKey(x => x.Id);
        platformRequestMetric.Property(x => x.RecordedAtUtc).IsRequired();
        platformRequestMetric.Property(x => x.Method).HasMaxLength(12).IsRequired();
        platformRequestMetric.Property(x => x.Endpoint).HasMaxLength(180).IsRequired();
        platformRequestMetric.Property(x => x.StatusCode).IsRequired();
        platformRequestMetric.Property(x => x.DurationMs).IsRequired();
        platformRequestMetric.Property(x => x.FailureKind).HasMaxLength(120);
        platformRequestMetric.HasIndex(x => x.RecordedAtUtc);
        platformRequestMetric.HasIndex(x => new { x.Endpoint, x.RecordedAtUtc });

        var moodleUserLink = modelBuilder.Entity<MoodleUserLink>();
        moodleUserLink.ToTable("moodle_user_links");
        moodleUserLink.HasKey(x => x.Id);
        moodleUserLink.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        moodleUserLink.Property(x => x.Email).HasMaxLength(320);
        moodleUserLink.Property(x => x.MoodleAlias).HasMaxLength(64).IsRequired();
        moodleUserLink.HasIndex(x => new { x.Subject, x.MoodleAlias }).IsUnique();

        ConfigureGrading(modelBuilder);
        ConfigureGradingPackages(modelBuilder);
        ConfigureUserMemories(modelBuilder);
    }

    private static void ConfigureUserMemories(ModelBuilder modelBuilder)
    {
        var memory = modelBuilder.Entity<UserMemory>();
        memory.ToTable("user_memories");
        memory.HasKey(x => x.Id);
        memory.Property(x => x.OwnerSubject).HasMaxLength(200).IsRequired();
        memory.Property(x => x.Category).HasMaxLength(32).IsRequired();
        memory.Property(x => x.NormalizedKey).HasMaxLength(120).IsRequired();
        memory.Property(x => x.Content).HasMaxLength(1000).IsRequired();
        memory.Property(x => x.Origin).HasMaxLength(32).IsRequired();
        memory.Property(x => x.MoodleAlias).HasMaxLength(64);
        memory.Property(x => x.CourseId).HasMaxLength(64);
        memory.Property(x => x.LinkedDocumentId);
        memory.Property(x => x.CreatedAtUtc).IsRequired();
        memory.Property(x => x.UpdatedAtUtc).IsRequired();
        memory.HasIndex(x => new { x.OwnerSubject, x.MoodleAlias, x.CourseId, x.UpdatedAtUtc })
            .IsDescending(false, false, false, true);
        memory.HasIndex(x => new { x.OwnerSubject, x.Category, x.MoodleAlias, x.CourseId, x.NormalizedKey })
            .IsUnique()
            .AreNullsDistinct(false);

        var document = modelBuilder.Entity<UserMemoryDocument>();
        document.ToTable("user_memory_documents");
        document.HasKey(x => x.Id);
        document.Property(x => x.OwnerSubject).HasMaxLength(200).IsRequired();
        document.Property(x => x.NormalizedKey).HasMaxLength(120).IsRequired();
        document.Property(x => x.Title).HasMaxLength(200).IsRequired();
        document.Property(x => x.Content).HasMaxLength(200000).IsRequired();
        document.Property(x => x.Format).HasMaxLength(32).IsRequired();
        document.Property(x => x.Origin).HasMaxLength(32).IsRequired();
        document.Property(x => x.MoodleAlias).HasMaxLength(64);
        document.Property(x => x.CourseId).HasMaxLength(64);
        document.Property(x => x.CreatedAtUtc).IsRequired();
        document.Property(x => x.UpdatedAtUtc).IsRequired();
        document.HasIndex(x => new { x.OwnerSubject, x.MoodleAlias, x.CourseId, x.UpdatedAtUtc })
            .IsDescending(false, false, false, true);
        document.HasIndex(x => new { x.OwnerSubject, x.MoodleAlias, x.CourseId, x.NormalizedKey })
            .IsUnique()
            .AreNullsDistinct(false);
    }

    private static void ConfigureGrading(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<GradingRun>();
        run.ToTable("grading_run");
        run.HasKey(x => x.Id);
        run.Property(x => x.CreatedBySubject).HasMaxLength(200).IsRequired();
        run.Property(x => x.CreatedByMoodleUserId);
        run.Property(x => x.MoodleConnectionId).HasMaxLength(64);
        run.Property(x => x.ConnectorClientId).HasMaxLength(64);
        run.Property(x => x.ConnectionAlias).HasMaxLength(64);
        run.Property(x => x.CourseIdScope).HasMaxLength(64);
        run.Property(x => x.IdempotencyKey).HasMaxLength(128);
        run.Property(x => x.RequestDigest).HasMaxLength(64);
        run.Property(x => x.ExpectedItemCount).IsRequired();
        run.Property(x => x.ExpectedBatchCount).IsRequired();
        run.Property(x => x.Destination).HasMaxLength(32).IsRequired();
        run.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        run.Property(x => x.CreatedAt).IsRequired();
        run.Property(x => x.UpdatedAt).IsRequired();
        run.HasIndex(x => new { x.CreatedBySubject, x.Status, x.CreatedAt });
        run.HasIndex(x => new { x.CreatedBySubject, x.ConnectorClientId, x.MoodleConnectionId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        var batch = modelBuilder.Entity<AssistedGradingBatch>();
        batch.ToTable("grading_batch");
        batch.HasKey(x => x.Id);
        batch.Property(x => x.AssignmentIds)
            .HasColumnName("AssignmentIdsJson")
            .HasColumnType("jsonb")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<long[]>(json, (JsonSerializerOptions?)null) ?? Array.Empty<long>())
            .Metadata.SetValueComparer(new ValueComparer<IReadOnlyList<long>>(
                (left, right) => left != null && right != null && left.SequenceEqual(right),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                value => value.ToArray()));
        batch.Property(x => x.CreatedBySubject).HasMaxLength(200).IsRequired();
        batch.Property(x => x.CourseDisplayName).HasMaxLength(240);
        batch.Property(x => x.ConnectorClientId).HasMaxLength(64);
        batch.Property(x => x.ConnectionAlias).HasMaxLength(64);
        batch.Property(x => x.MoodleConnectionId).HasMaxLength(64);
        batch.Property(x => x.GradingRunId);
        batch.Property(x => x.IdempotencyKey).HasMaxLength(128);
        batch.Property(x => x.TeacherInstructions).HasMaxLength(8000);
        batch.Property(x => x.Priority).HasMaxLength(16).IsRequired();
        batch.Property(x => x.IncludeRubric).IsRequired();
        batch.Property(x => x.IncludeSubmissionFiles).IsRequired();
        batch.Property(x => x.IncludeCourseMaterials).IsRequired();
        batch.Property(x => x.LeaseOwner).HasMaxLength(200);
        batch.Property(x => x.LastErrorCode).HasMaxLength(120);
        batch.Property(x => x.Status).HasConversion<string>().HasMaxLength(80).IsRequired();
        batch.HasIndex(x => new { x.CreatedBySubject, x.Status });
        batch.HasIndex(x => new { x.CreatedBySubject, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        batch.HasIndex(x => new { x.CourseId, x.Status });
        batch.HasIndex(x => x.GradingRunId);
        batch.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseUntil, x.Priority });

        var item = modelBuilder.Entity<AssistedGradingItem>();
        item.ToTable("grading_item");
        item.HasKey(x => x.Id);
        item.Property(x => x.Status).HasConversion<string>().HasMaxLength(80).IsRequired();
        item.Property(x => x.StudentDisplayName).HasMaxLength(240);
        item.Property(x => x.ProcessingStage).HasMaxLength(32).IsRequired();
        item.Property(x => x.ProcessingStageUpdatedAt);
        item.Property(x => x.ReviewStatus).HasConversion<string>().HasMaxLength(80).IsRequired();
        item.Property(x => x.ReviewedAt).IsConcurrencyToken();
        item.Property(x => x.CommitWriteIntentAt).IsConcurrencyToken();
        item.Property(x => x.CommitStatus).HasConversion<string>().HasMaxLength(80).IsRequired();
        item.Property(x => x.TeacherDecision).HasMaxLength(80);
        item.Property(x => x.PrivateNotesToTeacher);
        item.Property(x => x.ReviewedBySubject).HasMaxLength(200);
        item.Property(x => x.IdempotencyKey).HasMaxLength(64);
        item.Property(x => x.ContextHash).HasMaxLength(64).IsConcurrencyToken();
        item.Property(x => x.ContextStatus).HasMaxLength(32);
        item.Property(x => x.SubmissionContentHash).HasMaxLength(64);
        item.Property(x => x.SubmissionResourceIdsJson).HasColumnType("jsonb");
        item.Property(x => x.SubmissionRevisionHash).HasMaxLength(64);
        item.Property(x => x.LeaseOwner).HasMaxLength(200);
        item.Property(x => x.LastErrorCode).HasMaxLength(120);
        item.Property(x => x.LastErrorPhase).HasMaxLength(32);
        item.Property(x => x.CommitRetryCount).IsRequired();
        item.HasIndex(x => new { x.CommitStatus, x.CommitNextAttemptAt });
        item.HasOne<AssistedGradingBatch>()
            .WithMany()
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
        item.HasIndex(x => new { x.BatchId, x.Status });
        item.HasIndex(x => new { x.AssignmentId, x.MoodleUserId });
        item.HasIndex(x => x.ReviewStatus);
        item.HasIndex(x => x.CommitStatus);
        item.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        item.HasIndex(x => new { x.ContextHash, x.ContextVersion });
        item.HasIndex(x => new { x.BatchId, x.Status, x.NextAttemptAt, x.LeaseUntil });

        var artifact = modelBuilder.Entity<GradingArtifact>();
        artifact.ToTable("grading_artifact");
        artifact.HasKey(x => x.Id);
        artifact.Property(x => x.ArtifactType).HasMaxLength(80).IsRequired();
        artifact.Property(x => x.Filename).HasMaxLength(512);
        artifact.Property(x => x.MimeType).HasMaxLength(160);
        artifact.Property(x => x.Sha256).HasMaxLength(64);
        artifact.Property(x => x.ExtractionStatus).HasMaxLength(80).IsRequired();
        artifact.Property(x => x.SourceUrl).HasMaxLength(2000);
        artifact.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Cascade);
        artifact.HasIndex(x => x.GradingItemId);
        artifact.HasIndex(x => x.Sha256).HasFilter("\"Sha256\" IS NOT NULL");

        var moodleResource = modelBuilder.Entity<MoodleResource>();
        moodleResource.ToTable("moodle_resource");
        moodleResource.HasKey(x => x.ResourceId);
        moodleResource.Property(x => x.ResourceId).HasMaxLength(32);
        moodleResource.Property(x => x.ClientId).HasMaxLength(64).IsRequired();
        moodleResource.Property(x => x.ConnectionId).HasMaxLength(64).IsRequired();
        moodleResource.Property(x => x.MoodleAlias).HasMaxLength(64).IsRequired();
        moodleResource.Property(x => x.OwnerSubject).HasMaxLength(200).IsRequired();
        moodleResource.Property(x => x.ResourceType).HasMaxLength(80).IsRequired();
        moodleResource.Property(x => x.ContextId).HasMaxLength(128);
        moodleResource.Property(x => x.Component).HasMaxLength(128);
        moodleResource.Property(x => x.FileArea).HasMaxLength(128);
        moodleResource.Property(x => x.ItemId).HasMaxLength(128);
        moodleResource.Property(x => x.Filename).HasMaxLength(512).IsRequired();
        moodleResource.Property(x => x.MimeType).HasMaxLength(160).IsRequired();
        moodleResource.Property(x => x.Sha256).HasMaxLength(64);
        moodleResource.Property(x => x.RemoteFileReference).HasMaxLength(2000).IsRequired();
        moodleResource.Property(x => x.ParentResourceId).HasMaxLength(32);
        moodleResource.Property(x => x.InlineContent).HasColumnType("bytea");
        moodleResource.HasIndex(x => new { x.ClientId, x.ConnectionId, x.ExpiresAt });
        moodleResource.HasIndex(x => new { x.ClientId, x.ConnectionId, x.OwnerSubject, x.ExpiresAt });
        moodleResource.HasIndex(x => new
        {
            x.ClientId,
            x.ConnectionId,
            x.OwnerSubject,
            x.RemoteFileReference,
            x.ExpiresAt
        });
        moodleResource.HasIndex(x => x.ExpiresAt);
        moodleResource.HasIndex(x => x.ParentResourceId).HasFilter("\"ParentResourceId\" IS NOT NULL");

        var evidence = modelBuilder.Entity<GradingEvidence>();
        evidence.ToTable("grading_evidence");
        evidence.HasKey(x => x.Id);
        evidence.Property(x => x.CriterionId).HasMaxLength(120);
        evidence.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Cascade);
        evidence.HasIndex(x => x.GradingItemId);

        var contextSnapshot = modelBuilder.Entity<GradingContextSnapshotDocument>();
        contextSnapshot.ToTable("grading_context_snapshot");
        contextSnapshot.HasKey(x => x.Id);
        contextSnapshot.Property(x => x.ContextHash).HasMaxLength(64).IsRequired();
        contextSnapshot.Property(x => x.ContextStatus).HasMaxLength(32).IsRequired();
        contextSnapshot.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        contextSnapshot.Property(x => x.CoverageJson).HasColumnType("jsonb");
        contextSnapshot.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Cascade);
        contextSnapshot.HasIndex(x => new { x.GradingItemId, x.Version }).IsUnique();
        contextSnapshot.HasIndex(x => new { x.GradingItemId, x.ContextHash }).IsUnique();

        var proposal = modelBuilder.Entity<AiGradingProposalDocument>();
        proposal.ToTable("grading_ai_proposal");
        proposal.HasKey(x => x.Id);
        proposal.Property(x => x.SchemaVersion).HasMaxLength(32).IsRequired();
        proposal.Property(x => x.ContextHash).HasMaxLength(64);
        proposal.Property(x => x.SubmissionContentHash).HasMaxLength(64);
        proposal.Property(x => x.ProposalHash).HasMaxLength(64).IsRequired();
        proposal.Property(x => x.Status).HasMaxLength(80).IsRequired();
        proposal.Property(x => x.Confidence).HasPrecision(5, 4).IsRequired();
        proposal.Property(x => x.ReviewRequired).IsRequired();
        proposal.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        proposal.Property(x => x.CreatedAt).IsRequired();
        proposal.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Cascade);
        proposal.HasIndex(x => new { x.GradingItemId, x.Version }).IsUnique();
        proposal.HasIndex(x => new { x.GradingItemId, x.ProposalHash }).IsUnique();
    }

    private static void ConfigureGradingPackages(ModelBuilder modelBuilder)
    {
        var package = modelBuilder.Entity<GradingEvidencePackage>();
        package.ToTable("grading_evidence_package");
        package.HasKey(x => x.Id);
        package.Property(x => x.OwnerSubject).HasMaxLength(200).IsRequired();
        package.Property(x => x.ConnectorClientId).HasMaxLength(64);
        package.Property(x => x.MoodleConnectionId).HasMaxLength(64);
        package.Property(x => x.ConnectionAlias).HasMaxLength(64);
        package.Property(x => x.Cmid).HasMaxLength(64);
        package.Property(x => x.ActivityKey).HasMaxLength(64).IsRequired();
        package.Property(x => x.PreparationKey).HasMaxLength(64).IsRequired();
        package.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        package.Property(x => x.ManifestJson).HasColumnType("jsonb");
        package.Property(x => x.ManifestHash).HasMaxLength(64);
        package.Property(x => x.BlobKey).HasMaxLength(500);
        package.Property(x => x.ZipSha256).HasMaxLength(64);
        package.Property(x => x.LeaseOwner).HasMaxLength(200);
        package.Property(x => x.LastErrorCode).HasMaxLength(120);
        package.Property(x => x.LastErrorMessage).HasMaxLength(4000);
        package.Property(x => x.UpdatedAt).IsConcurrencyToken();
        package.HasIndex(x => new { x.OwnerSubject, x.PreparationKey, x.PackageVersion }).IsUnique();
        package.HasIndex(x => new { x.OwnerSubject, x.ActivityKey, x.PackageVersion }).IsUnique();
        package.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseUntil });
        package.HasIndex(x => new { x.OwnerSubject, x.CourseId, x.AssignmentId, x.UpdatedAt });

        var member = modelBuilder.Entity<GradingEvidencePackageMember>();
        member.ToTable("grading_evidence_package_member");
        member.HasKey(x => x.Id);
        member.Property(x => x.StudentDisplayName).HasMaxLength(240);
        member.Property(x => x.ContextHash).HasMaxLength(64);
        member.Property(x => x.SubmissionContentHash).HasMaxLength(64);
        member.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        member.Property(x => x.IssueCode).HasMaxLength(120);
        member.Property(x => x.IssueMessage).HasMaxLength(4000);
        member.HasOne<GradingEvidencePackage>()
            .WithMany()
            .HasForeignKey(x => x.PackageId)
            .OnDelete(DeleteBehavior.Cascade);
        member.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Restrict);
        member.HasIndex(x => new { x.PackageId, x.GradingItemId }).IsUnique();
        member.HasIndex(x => x.GradingItemId);

        var file = modelBuilder.Entity<GradingEvidencePackageFile>();
        file.ToTable("grading_evidence_package_file");
        file.HasKey(x => x.Id);
        file.Property(x => x.RelativePath).HasMaxLength(1000).IsRequired();
        file.Property(x => x.OriginalName).HasMaxLength(512);
        file.Property(x => x.MimeType).HasMaxLength(160);
        file.Property(x => x.Sha256).HasMaxLength(64);
        file.Property(x => x.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        file.Property(x => x.IssueCode).HasMaxLength(120);
        file.Property(x => x.IssueMessage).HasMaxLength(4000);
        file.HasOne<GradingEvidencePackage>()
            .WithMany()
            .HasForeignKey(x => x.PackageId)
            .OnDelete(DeleteBehavior.Cascade);
        file.HasOne<AssistedGradingItem>()
            .WithMany()
            .HasForeignKey(x => x.GradingItemId)
            .OnDelete(DeleteBehavior.Restrict);
        file.HasIndex(x => new { x.PackageId, x.RelativePath }).IsUnique();
        file.HasIndex(x => new { x.PackageId, x.Status });
        file.HasIndex(x => x.ArtifactId);
    }
}
