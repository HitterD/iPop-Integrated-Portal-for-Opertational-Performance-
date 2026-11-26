using System.Linq.Expressions;
using IPop.Core.Entities;

namespace IPop.Application.Notifications;

/// <see cref="NotificationCategory.Of"/> as an expression the database runs (one CASE), so the inbox filters and
/// counts by category in SQL instead of loading every delivery into memory (review M2). Same rules, same order;
/// tests/IPop.Tests/NotificationCategorySqlTests.cs checks both against one table of cases.
/// A path-prefix test on the whole link equals the one on the link without "?…"/"#…", since no prefix holds those.
public static class NotificationCategorySql
{
    public static readonly Expression<Func<NotificationDelivery, string>> Of = d =>
        d.Request.EventType.Trim().ToLower().Contains("mention") ? NotificationCategory.Mention
        : d.Request.EventType.Trim().ToLower().StartsWith("chat.") ? NotificationCategory.Chat
        : d.Request.EventType.Trim().ToLower().StartsWith("call.") || d.Request.EventType.Trim().ToLower().StartsWith("calls.")
            || d.Request.EventType.Trim().ToLower().StartsWith("meeting.") || d.Request.EventType.Trim().ToLower().StartsWith("calendar.") ? NotificationCategory.Meeting
        : d.Request.EventType.Trim().ToLower().StartsWith("ticket.") || d.Request.EventType.Trim().ToLower().StartsWith("tickets.")
            || d.Request.EventType.Trim().ToLower().StartsWith("idesk.") ? NotificationCategory.Tickets
        : d.Request.EventType.Trim().ToLower().StartsWith("mail.") ? NotificationCategory.Mail
        : d.Request.EventType.Trim().ToLower().StartsWith("project.") || d.Request.EventType.Trim().ToLower().StartsWith("task.") ? NotificationCategory.Task
        : d.Request.EventType.Trim().ToLower().StartsWith("news.") || d.Request.EventType.Trim().ToLower().StartsWith("memo.") ? NotificationCategory.News
        : d.Request.EventType.Trim().ToLower().StartsWith("system.") || d.Request.EventType.Trim().ToLower().StartsWith("approval.")
            || d.Request.EventType.Trim().ToLower().StartsWith("admin.") ? NotificationCategory.System
        // Any other dotted event: only the link decides. A bare (undotted) coarse type names its module.
        : d.Request.EventType.Trim().ToLower() == "chat" ? NotificationCategory.Chat
        : d.Request.EventType.Trim().ToLower() == "mail" ? NotificationCategory.Mail
        : d.Request.EventType.Trim().ToLower() == "news" ? NotificationCategory.News
        : d.Request.EventType.Trim().ToLower() == "project" ? NotificationCategory.Task
        // Then the link.
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/tickets") || (d.Request.TargetUrl ?? "").ToLower().StartsWith("/idesk/tickets") ? NotificationCategory.Tickets
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/calls") || (d.Request.TargetUrl ?? "").ToLower().StartsWith("/calendar")
            || (d.Request.TargetUrl ?? "").ToLower().StartsWith("/meeting") ? NotificationCategory.Meeting
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/chat") ? NotificationCategory.Chat
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/mail") ? NotificationCategory.Mail
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/projects") ? NotificationCategory.Task
        : (d.Request.TargetUrl ?? "").ToLower().StartsWith("/news") ? NotificationCategory.News
        : NotificationCategory.System;

    /// d => Of(d) == category.
    public static Expression<Func<NotificationDelivery, bool>> Is(string category) =>
        Expression.Lambda<Func<NotificationDelivery, bool>>(Expression.Equal(Of.Body, Expression.Constant(category)), Of.Parameters);
}
