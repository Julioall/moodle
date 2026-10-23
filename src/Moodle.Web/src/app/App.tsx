import { lazy, Suspense } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';

import { NotFound } from './NotFound';
import { AppLayout } from '../components/layout/AppLayout';
import { Skeleton } from '../components/ui/skeleton';
import { AuthGate } from '../features/auth/AuthGate';

const MyCoursesPage = lazy(() => import('../features/courses/MyCoursesPage').then(({ MyCoursesPage }) => ({ default: MyCoursesPage })));
const StudentProfilePage = lazy(() => import('../features/students/StudentProfilePage').then(({ StudentProfilePage }) => ({ default: StudentProfilePage })));
const SettingsPage = lazy(() => import('../features/settings/SettingsPage').then(({ SettingsPage }) => ({ default: SettingsPage })));
const SchoolsPage = lazy(() => import('../features/schools/SchoolsPage').then(({ SchoolsPage }) => ({ default: SchoolsPage })));
const CourseOverviewRoute = lazy(() => import('../features/courses/CourseOverviewRoute').then(({ CourseOverviewRoute }) => ({ default: CourseOverviewRoute })));
const ReportTemplatesPage = lazy(() => import('../features/reports/templates/ReportTemplatesPage').then(({ ReportTemplatesPage }) => ({ default: ReportTemplatesPage })));
const ReportTemplateEditorPage = lazy(() => import('../features/reports/templates/ReportTemplateEditorPage').then(({ ReportTemplateEditorPage }) => ({ default: ReportTemplateEditorPage })));
const ReportGenerationPage = lazy(() => import('../features/reports/ReportGenerationPage').then(({ ReportGenerationPage }) => ({ default: ReportGenerationPage })));
const ReportsPage = lazy(() => import('../features/reports/ReportsPage').then(({ ReportsPage }) => ({ default: ReportsPage })));

export function App() {
  return (
    <BrowserRouter basename="">
      <AuthGate>
        <Suspense fallback={<main className="mx-auto min-h-screen w-full max-w-7xl space-y-6 p-6" aria-busy="true"><p role="status" className="sr-only">Carregando tela...</p><Skeleton className="h-8 w-1/3" /><div className="grid gap-4 md:grid-cols-2"><Skeleton className="h-24" /><Skeleton className="h-24" /></div><Skeleton className="h-80 w-full" /></main>}>
          <Routes>
            <Route element={<AppLayout />}>
              <Route path="/" element={<Navigate to="/meus-cursos" replace />} />
              {/* Keep the former standalone URL readable after connections moved into Settings. */}
              <Route path="/conexoes" element={<Navigate to="/configuracoes?tab=conexoes" replace />} />
              <Route path="/meus-cursos" element={<MyCoursesPage />} />
              {/* Preserve previously shared onboarding links after automatic course selection. */}
              <Route path="/selecionar-cursos" element={<Navigate to="/meus-cursos" replace />} />
              <Route path="/escolas" element={<SchoolsPage />} />
              <Route path="/cursos/:connectionRef/:courseId" element={<CourseOverviewRoute />} />
              <Route path="/cursos/:connectionRef/:courseId/alunos/:studentId" element={<StudentProfilePage />} />
              {/* Keep old profile URLs readable for bookmarks; the roster itself is now only a course tab. */}
              <Route path="/alunos/:connectionRef/:courseId/:studentId" element={<StudentProfilePage />} />
              <Route path="/followup" element={<Navigate to="/meus-cursos" replace />} />
              {/* The message API remains available for future campaigns. */}
              <Route path="/mensagens" element={<Navigate to="/" replace />} />
              <Route path="/pendencias" element={<Navigate to="/meus-cursos" replace />} />
              <Route path="/foruns" element={<Navigate to="/meus-cursos" replace />} />
              <Route path="/relatorios/gerar" element={<ReportGenerationPage />} />
              <Route path="/relatorios" element={<ReportsPage />} />
              <Route path="/relatorios/modelos" element={<ReportTemplatesPage />} />
              <Route path="/relatorios/modelos/novo" element={<ReportTemplateEditorPage />} />
              <Route path="/relatorios/modelos/:id/editar" element={<ReportTemplateEditorPage />} />
              <Route path="/configuracoes" element={<SettingsPage />} />
              <Route path="*" element={<NotFound />} />
            </Route>
          </Routes>
        </Suspense>
      </AuthGate>
    </BrowserRouter>
  );
}
