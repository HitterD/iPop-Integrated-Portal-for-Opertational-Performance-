package id.co.santos.ipop

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.LocusIdCompat
import androidx.core.content.pm.ShortcutInfoCompat
import androidx.core.content.pm.ShortcutManagerCompat
import androidx.core.graphics.drawable.IconCompat
import androidx.core.app.Person
import androidx.core.app.RemoteInput

/**
 * Builds every notification from an FCM data payload (all values are strings):
 * kind (chat|notif|call|close|test), title, body, url, tag, callId, actionToken, apiBase, roomId, sender, channel.
 */
object Notifier {
    const val ID = 1
    const val KEY_REPLY = "reply_text"
    private const val CALL_TIMEOUT_MS = 45_000L

    private fun nm(c: Context) = c.getSystemService(NotificationManager::class.java)
    private fun accent(c: Context) = c.getColor(R.color.accent)

    /** What happened to one push; reported as a delivery receipt. */
    data class Result(val outcome: String, val channel: String?, val detail: String = "")

    fun handle(c: Context, d: Map<String, String>): Result = when (d["kind"]) {
        "call" -> call(c, d)
        "close" -> { close(c, d["tag"]); Result("closed", null, "tag=${d["tag"]}") }
        "chat" -> chat(c, d)
        else -> general(c, d)
    }

    // ── open-the-app intents ──
    fun openIntent(c: Context, url: String?, requestKey: String): PendingIntent {
        val i = Intent(c, MainActivity::class.java).apply {
            action = "id.co.santos.ipop.OPEN"
            if (!url.isNullOrBlank()) putExtra("url", url)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        }
        return PendingIntent.getActivity(c, requestKey.hashCode(), i, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    private fun actionIntent(c: Context, action: String, d: Map<String, String>, mutable: Boolean = false): PendingIntent {
        val i = Intent(c, ActionReceiver::class.java).apply {
            this.action = action
            d.forEach { (k, v) -> putExtra(k, v) }
        }
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (mutable) PendingIntent.FLAG_MUTABLE else PendingIntent.FLAG_IMMUTABLE)
        return PendingIntent.getBroadcast(c, (action + (d["tag"] ?: "")).hashCode(), i, flags)
    }

    private fun base(c: Context, channel: String) = NotificationCompat.Builder(c, channel)
        .setSmallIcon(R.drawable.ic_stat_ipop)
        .setColor(accent(c))
        .setShowWhen(true)
        .setWhen(System.currentTimeMillis())

    private fun tagOf(d: Map<String, String>): String =
        d["tag"]?.takeIf { it.isNotBlank() } ?: "n:${System.currentTimeMillis()}"

    // ── kind = notif | test | anything else ──
    private fun general(c: Context, d: Map<String, String>): Result {
        val tag = tagOf(d)
        val title = d["title"].orEmpty().ifBlank { "iPop" }
        val body = d["body"].orEmpty()
        val n = base(c, Channels.of(d["channel"]))
            .setContentTitle(title)
            .setContentText(body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(body))
            .setAutoCancel(true)
            .setContentIntent(openIntent(c, d["url"], tag))
            .setPriority(if (d["urgent"] == "1") NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
            .setCategory(if (d["channel"] == "mail") NotificationCompat.CATEGORY_EMAIL else NotificationCompat.CATEGORY_MESSAGE)
            .setOnlyAlertOnce(false)
            .build()
        return notify(c, tag, n)
    }

    // ── kind = chat: one MessagingStyle notification per conversation tag ──
    private fun chat(c: Context, d: Map<String, String>): Result {
        val tag = tagOf(d)
        val sender = d["sender"].orEmpty().ifBlank { d["title"].orEmpty().ifBlank { "iPop" } }
        ChatStore.add(c, tag, ChatStore.Msg(sender, d["body"].orEmpty(), System.currentTimeMillis(), false))
        return postChat(c, d, tag, silent = false)
    }

    /** (Re)builds the conversation notification from ChatStore. Also used after "Balas" with silent = true. */
    fun postChat(c: Context, d: Map<String, String>, tag: String, silent: Boolean, error: String? = null): Result {
        val me = Person.Builder().setName(c.getString(R.string.me)).setKey("me").build()
        val title = d["title"].orEmpty()
        val sender = d["sender"].orEmpty()
        val style = NotificationCompat.MessagingStyle(me)
        if (sender.isNotBlank() && title.isNotBlank() && title != sender) {
            style.conversationTitle = title
            style.isGroupConversation = true
        }
        val people = HashMap<String, Person>()
        for (m in ChatStore.list(c, tag)) {
            val p = if (m.me) null else people.getOrPut(m.sender) { Person.Builder().setName(m.sender).setKey(m.sender).build() }
            style.addMessage(m.text, m.time, p)
        }
        if (error != null) style.addMessage(error, System.currentTimeMillis(), null as Person?)

        val url = d["url"]
        val roomId = d["roomId"].orEmpty()
        val shortcutId = conversationShortcut(c, d, tag, title.ifBlank { sender }, people.values.firstOrNull())
        val b = base(c, Channels.CHAT)
            .setStyle(style)
            .setContentTitle(title.ifBlank { sender })
            .setContentText(d["body"].orEmpty())
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            // New messages always alert (heads-up + sound); only our own reply updates are silent.
            .setOnlyAlertOnce(silent)
            .setSilent(silent)
            .setContentIntent(openIntent(c, url, tag))
        // One notification per conversation tag; no group and no summary that could swallow it.
        if (shortcutId != null) b.setShortcutId(shortcutId).setLocusId(LocusIdCompat(shortcutId))
        if (roomId.isNotBlank()) {
            if (Session.token(c) != null) {
                val input = RemoteInput.Builder(KEY_REPLY).setLabel(c.getString(R.string.act_reply_hint)).build()
                b.addAction(
                    NotificationCompat.Action.Builder(R.drawable.ic_send, c.getString(R.string.act_reply),
                        actionIntent(c, ActionReceiver.REPLY, d, mutable = true))
                        .addRemoteInput(input)
                        .setAllowGeneratedReplies(true)
                        .setSemanticAction(NotificationCompat.Action.SEMANTIC_ACTION_REPLY)
                        .setShowsUserInterface(false)
                        .build()
                )
            } else {
                // No session yet: "Balas" opens the conversation in the app instead.
                b.addAction(NotificationCompat.Action.Builder(R.drawable.ic_send, c.getString(R.string.act_reply),
                    openIntent(c, url, "reply:$tag")).build())
            }
            b.addAction(
                NotificationCompat.Action.Builder(R.drawable.ic_check, c.getString(R.string.act_mark_read),
                    actionIntent(c, ActionReceiver.MARK_READ, d))
                    .setSemanticAction(NotificationCompat.Action.SEMANTIC_ACTION_MARK_AS_READ)
                    .setShowsUserInterface(false)
                    .build()
            )
        }
        return notify(c, tag, b.build())
    }

    private const val CONVERSATION_CATEGORY = "id.co.santos.ipop.category.CONVERSATION"

    /**
     * Android 11+ treats MessagingStyle as a conversation only when it is linked to a long-lived
     * shortcut; some OEM builds otherwise minimise it. One dynamic shortcut per conversation.
     */
    private fun conversationShortcut(c: Context, d: Map<String, String>, tag: String, label: String, person: Person?): String? {
        return try {
            val id = "conv:" + (d["roomId"]?.takeIf { it.isNotBlank() } ?: tag)
            val name = label.ifBlank { "iPop" }
            val open = Intent(c, MainActivity::class.java).apply {
                action = Intent.ACTION_VIEW
                d["url"]?.let { putExtra("url", it) }
            }
            val p = person ?: Person.Builder().setName(name).setKey(id).build()
            val sc = ShortcutInfoCompat.Builder(c, id)
                .setShortLabel(name.take(25))
                .setLongLabel(name)
                .setIcon(IconCompat.createWithResource(c, R.mipmap.ic_launcher))
                .setIntent(open)
                .setLongLived(true)
                .setPerson(p)
                .setCategories(setOf(CONVERSATION_CATEGORY))
                .setLocusId(LocusIdCompat(id))
                .build()
            ShortcutManagerCompat.pushDynamicShortcut(c, sc)
            id
        } catch (e: Exception) {
            Log.w(Ipop.TAG, "conversation shortcut failed: ${e.message}")
            null
        }
    }

    // ── kind = call: full-screen incoming call ──
    private fun call(c: Context, d: Map<String, String>): Result {
        val tag = d["tag"]?.takeIf { it.isNotBlank() } ?: "call:${d["callId"].orEmpty()}"
        val dd = d + ("tag" to tag)
        val name = callerName(dd)
        val full = Intent(c, IncomingCallActivity::class.java).apply {
            dd.forEach { (k, v) -> putExtra(k, v) }
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_USER_ACTION)
        }
        val fullPi = PendingIntent.getActivity(c, tag.hashCode(), full, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val answer = Intent(full).setAction(IncomingCallActivity.ACTION_ANSWER)
        val answerPi = PendingIntent.getActivity(c, ("a" + tag).hashCode(), answer, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val declinePi = actionIntent(c, ActionReceiver.DECLINE, dd)
        val caller = Person.Builder().setName(name).setKey(name).setImportant(true).build()

        val n = base(c, Channels.CALLS)
            .setContentTitle(name)
            .setContentText(c.getString(R.string.call_sub))
            .setStyle(NotificationCompat.CallStyle.forIncomingCall(caller, declinePi, answerPi))
            .setCategory(NotificationCompat.CATEGORY_CALL)
            .setPriority(NotificationCompat.PRIORITY_MAX)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setFullScreenIntent(fullPi, true)
            .setContentIntent(fullPi)
            .setOngoing(true)
            .setAutoCancel(false)
            .setTimeoutAfter(CALL_TIMEOUT_MS)
            .build()
        val posted = notify(c, tag, n)
        Ringer.start(c, tag, CALL_TIMEOUT_MS)
        if (posted.outcome != "shown") return posted.copy(detail = "call ${posted.detail}")
        // 34+: without full-screen permission the system only shows the CallStyle heads-up.
        if (Build.VERSION.SDK_INT >= 34 && !nm(c).canUseFullScreenIntent())
            return Result("call-fullscreen-denied", Channels.CALLS, "heads-up only; ${posted.detail}")
        // The system launches IncomingCallActivity itself (screen off/locked) or shows the heads-up (phone in
        // use). IncomingCallActivity reports call-activity-started separately when it really starts.
        return posted.copy(detail = "fullscreen-intent posted; ${posted.detail}")
    }

    /** "Incoming call · RM Bagastyo" -> "RM Bagastyo"; sender wins when the server sends it. */
    fun callerName(d: Map<String, String>): String {
        d["sender"]?.takeIf { it.isNotBlank() }?.let { return it }
        val t = d["title"].orEmpty()
        val i = t.indexOf("· ")
        return (if (i >= 0) t.substring(i + 2) else t).trim().ifBlank { "iPop" }
    }

    // ── kind = close ──
    fun close(c: Context, tag: String?) {
        if (tag.isNullOrBlank()) return
        nm(c).cancel(tag, ID)
        ChatStore.clear(c, tag)
        if (tag.startsWith("call:")) {
            Ringer.stop(tag)
            IncomingCallActivity.dismiss(tag)
        }
    }

    fun remindLater(c: Context, d: Map<String, String>) {
        val name = callerName(d)
        val n = base(c, Channels.SYSTEM)
            .setContentTitle(c.getString(R.string.remind_title, name))
            .setContentText(c.getString(R.string.remind_body))
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(openIntent(c, "/calls", "remind:${d["tag"]}"))
            .build()
        notify(c, "remind:${d["tag"]}", n)
    }

    /** Post and verify: the notification must be in getActiveNotifications() right after notify(). */
    private fun notify(c: Context, tag: String, n: Notification): Result {
        val channelId = n.channelId
        val nm = nm(c)
        if (!NotificationManagerCompat.from(c).areNotificationsEnabled() ||
            (Build.VERSION.SDK_INT >= 33 && !Perms.granted(c, android.Manifest.permission.POST_NOTIFICATIONS)))
            return Result("blocked-permission", channelId)
        val ch = nm.getNotificationChannel(channelId)
        if (ch != null && ch.importance == NotificationManager.IMPORTANCE_NONE)
            return Result("blocked-channel", channelId, "$channelId importance=${ch.importance}")
        try {
            nm.notify(tag, ID, n)
        } catch (e: SecurityException) {
            return Result("security-exception", channelId, e.message ?: "")
        }
        val active = try { nm.activeNotifications.any { it.tag == tag && it.id == ID } } catch (_: Exception) { false }
        val info = "priority=${n.priority} category=${n.category} fsi=${n.fullScreenIntent != null}" +
            (if (Build.VERSION.SDK_INT >= 30) " shortcut=${n.shortcutId ?: "-"}" else "")
        return Result(if (active) "shown" else "not-active-after-notify", channelId, info)
    }

}
