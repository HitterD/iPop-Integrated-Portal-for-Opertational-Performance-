using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Notifications;

/// Thin broadcast shell — same contract as PresenceHub: the transport is a detail;
/// all sends go through NotificationBroadcaster so endpoints/services never touch
/// Hub types.
public class NotificationHub : Microsoft.AspNetCore.SignalR.Hub;
