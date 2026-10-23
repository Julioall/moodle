import { createAppClient } from '../../integrations/http/api-client';

export type Forum = { forumId: string; courseId: string; type?: string; name?: string; numDiscussions?: number };
export type ForumPost = { postId: string; discussionId: string; userId?: string; userFullName?: string; subject: string; messageText?: string; createdAt?: string; modifiedAt?: string; attachments: { fileName?: string; mimeType?: string; sizeBytes?: number; fileUrl?: string }[] };
export type ForumDiscussion = { discussionId: string; subject: string; messageText?: string; authorFullName?: string; createdAt?: string; lastModifiedAt?: string; replyCount: number; locked?: boolean; canReply?: boolean; posts: ForumPost[] };
export type ForumRead = { courseId: string; forumId: string; forumModuleId: string; forumName: string; page: number; pageSize: number; returnedCount: number; hasMore: boolean; discussions: ForumDiscussion[] };
type ListResponse<T> = { data: T[]; meta: { generatedAt: string; connectionRef?: string } };
type ItemResponse<T> = { data: T; meta: { generatedAt: string; connectionRef?: string } };

export const createForumsGateway = (client = createAppClient()) => ({
  list: (connectionRef: string, courseId: string) => client.get<ListResponse<Forum>>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/forums`, { admissionFamily: 'course_details' }),
  read: (connectionRef: string, courseId: string, forumId: string) => client.get<ItemResponse<ForumRead>>(`/api/courses/${encodeURIComponent(connectionRef)}/${encodeURIComponent(courseId)}/forums/${encodeURIComponent(forumId)}?includePosts=true&page=1&pageSize=10`, { admissionFamily: 'course_details' }),
});

export const forumsGateway = createForumsGateway();
