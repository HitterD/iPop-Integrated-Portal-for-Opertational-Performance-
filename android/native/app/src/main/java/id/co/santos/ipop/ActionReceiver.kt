package id.co.santos.ipop

import android.app.AlarmManager
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.SystemClock
import androidx.core.app.RemoteInput
import org.json.JSONObject
import java.util.UUID

/** Notification actions that run without opening the app: Balas, Tandai dibaca, Tolak, Ingatkan, Tutup/Bisukan. */
class ActionReceiver : BroadcastReceiver() {
    companion object {
        const val REPLY = "id.co.santos.ipop.REPLY"
        const val MARK_READ = "id.co.santos.ipop.MARK_READ"
        const val DECLINE = "id.co.santos.ipop.DECLINE"
        const val REMIND = "id.co.santos.ipop.REMIND"
        const val CALL_HANGUP = "id.co.santos.ipop.CALL_HANGUP"
        const val CALL_MUTE = "id.co.santos.ipop.CALL_MUTE"

        private val keys = listOf("kind", "title", "body", "url", "tag", "callId", "actionToken", "apiBase", "roomId", "sender", "channel")

        fun extrasOf(i: Intent): Map<String, String> =
            keys.mapNotNull { k -> i.getStringExtra(k)?.let { k to it } }.toMap()

        /** POST {apiBase}/api/push/call-action — the signed actionToken is the only credential. */
        fun declineCall(c: Context, d: Map<String, String>) {
            val tag = d["tag"]
            Ringer.stop(tag)
            if (tag != null) c.getSystemService(NotificationManager::class.java).cancel(tag, Notifier.ID)
            val token = d["actionToken"] ?: return
            val base = d["apiBase"]?.trimEnd('/')?.takeIf { it.startsWith("https://") } ?: Ipop.BASE
            Http.bg("call-decline") {
                Http.request("POST", "$base/api/push/call-action",
                    JSONObject().put("token", token).put("action", "decline").toString())
            }
        }

        fun scheduleReminder(c: Context, d: Map<String, String>) {
            val i = Intent(c, ActionReceiver::class.java).setAction(REMIND)
            d.forEach { (k, v) -> i.putExtra(k, v) }
            val pi = PendingIntent.getBroadcast(c, ("remind" + d["tag"]).hashCode(), i,
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
            // Inexact is fine for "remind me in ~5 minutes" and needs no exact-alarm permission.
            c.getSystemService(AlarmManager::class.java)
                .setAndAllowWhileIdle(AlarmManager.ELAPSED_REALTIME_WAKEUP, SystemClock.elapsedRealtime() + 5 * 60_000L, pi)
        }
    }

    override fun onReceive(c: Context, intent: Intent) {
        val d = extrasOf(intent)
        when (intent.action) {
            REPLY -> reply(c, intent, d)
            MARK_READ -> markRead(c, d)
            DECLINE -> declineCall(c, d)
            REMIND -> Notifier.remindLater(c, d)
            CALL_HANGUP -> MainActivity.dispatchNative(c, "hangup")
            CALL_MUTE -> MainActivity.dispatchNative(c, "mute")
        }
    }

    private fun reply(c: Context, intent: Intent, d: Map<String, String>) {
        val text = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(Notifier.KEY_REPLY)?.toString()?.trim()
        val tag = d["tag"] ?: return
        val roomId = d["roomId"] ?: return
        if (text.isNullOrEmpty()) return
        val token = Session.token(c)
        if (token == null) { openApp(c, d["url"]); return }
        val pending = goAsync()
        Http.bg("chat-reply") {
            try {
                val body = JSONObject().put("content", text).put("idempotencyKey", UUID.randomUUID().toString())
                val r = Http.request("POST", "${Ipop.BASE}/api/chat/rooms/$roomId/messages", body.toString(), token)
                if (r.ok) {
                    ChatStore.add(c, tag, ChatStore.Msg(c.getString(R.string.me), text, System.currentTimeMillis(), true))
                    Notifier.postChat(c, d, tag, silent = true)
                } else {
                    Notifier.postChat(c, d, tag, silent = true, error = c.getString(R.string.reply_failed))
                }
            } catch (e: Exception) {
                Notifier.postChat(c, d, tag, silent = true, error = c.getString(R.string.reply_failed))
            } finally { pending.finish() }
        }
    }

    private fun markRead(c: Context, d: Map<String, String>) {
        val tag = d["tag"]
        val roomId = d["roomId"] ?: return
        if (tag != null) {
            c.getSystemService(NotificationManager::class.java).cancel(tag, Notifier.ID)
            ChatStore.clear(c, tag)
        }
        val token = Session.token(c) ?: return
        val pending = goAsync()
        Http.bg("chat-read") {
            try { Http.request("POST", "${Ipop.BASE}/api/chat/rooms/$roomId/read", "{}", token) }
            finally { pending.finish() }
        }
    }

    private fun openApp(c: Context, url: String?) {
        c.startActivity(Intent(c, MainActivity::class.java).apply {
            if (url != null) putExtra("url", url)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        })
    }
}
