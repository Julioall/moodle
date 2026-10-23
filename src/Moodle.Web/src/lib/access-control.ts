export const APP_PERMISSIONS = {
  DASHBOARD_VIEW: 'dashboard.view',
  COURSES_CATALOG_VIEW: 'courses.view',
  SCHOOLS_VIEW: 'schools.view',
  STUDENTS_VIEW: 'students.view',
  TASKS_VIEW: 'tasks.manage',
  AGENDA_VIEW: 'agenda.manage',
  MESSAGES_VIEW: 'messages.prepare',
  GRADING_VIEW: 'grading.view',
  GRADING_MANAGE: 'grading.manage',
  WHATSAPP_VIEW: 'whatsapp.view',
  MESSAGES_BULK_SEND: 'messages.bulk.send',
  SERVICES_VIEW: 'connections.manage',
  REPORTS_VIEW: 'reports.view',
  SETTINGS_VIEW: 'settings.view',
  ADMIN_VIEW: 'admin.view',
} as const;

export type AppPermissionKey = typeof APP_PERMISSIONS[keyof typeof APP_PERMISSIONS];
