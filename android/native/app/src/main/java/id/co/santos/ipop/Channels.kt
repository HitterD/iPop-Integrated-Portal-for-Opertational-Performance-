package id.co.santos.ipop

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.media.AudioAttributes
import android.media.RingtoneManager

/**
 * Notification channels. Android never raises the importance of an existing channel (and keeps any user
 * change), so when the settings below must reach existing installs the ids get a new version suffix and
 * the old ids are deleted. 2.0.0 used unsuffixed ids; 2.0.1 moves everything to `_v2`.
 */
object Channels {
    private const val V = "_v2"
    const val CHAT = "chat$V"
    const val MAIL = "mail$V"
    const val CALLS = "calls$V"
    const val MEETINGS = "meetings$V"
    const val TICKETS = "tickets$V"
    const val NEWS = "news$V"
    const val SYSTEM = "system$V"
    const val ONGOING = "ongoing_call$V"

    /** Ids used by earlier versions; deleted on start. */
    private val legacy = listOf("chat", "mail", "calls", "meetings", "tickets", "news", "system", "ongoing_call")

    /** Server `channel` field (chat|mail|calls|meetings|tickets|news|system) -> current channel id. */
    fun of(channel: String?): String = when (channel) {
        "chat" -> CHAT
        "mail" -> MAIL
        "calls" -> CALLS
        "meetings" -> MEETINGS
        "tickets" -> TICKETS
        "news" -> NEWS
        else -> SYSTEM
    }

    fun ensure(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        val sound = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_NOTIFICATION)
        val attrs = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_COMMUNICATION_INSTANT)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
        fun ch(id: String, name: Int, importance: Int) = NotificationChannel(id, c.getString(name), importance).apply {
            setSound(sound, attrs)
            enableVibration(true)
            setShowBadge(true)
            lockscreenVisibility = android.app.Notification.VISIBILITY_PRIVATE
        }
        val list = listOf(
            // Chat is a heads-up conversation channel: IMPORTANCE_HIGH.
            ch(CHAT, R.string.ch_chat, NotificationManager.IMPORTANCE_HIGH),
            ch(MAIL, R.string.ch_mail, NotificationManager.IMPORTANCE_DEFAULT),
            // The ringtone is played by Ringer / IncomingCallActivity (loop + vibrate), so the channel is silent.
            NotificationChannel(CALLS, c.getString(R.string.ch_calls), NotificationManager.IMPORTANCE_HIGH).apply {
                setSound(null, null)
                enableVibration(false)
                lockscreenVisibility = android.app.Notification.VISIBILITY_PUBLIC
            },
            ch(MEETINGS, R.string.ch_meetings, NotificationManager.IMPORTANCE_HIGH),
            ch(TICKETS, R.string.ch_tickets, NotificationManager.IMPORTANCE_DEFAULT),
            ch(NEWS, R.string.ch_news, NotificationManager.IMPORTANCE_DEFAULT),
            ch(SYSTEM, R.string.ch_system, NotificationManager.IMPORTANCE_DEFAULT),
            NotificationChannel(ONGOING, c.getString(R.string.ch_ongoing), NotificationManager.IMPORTANCE_LOW).apply {
                setSound(null, null)
                enableVibration(false)
                setShowBadge(false)
            },
        )
        nm.createNotificationChannels(list)
        for (old in legacy) if (nm.getNotificationChannel(old) != null) nm.deleteNotificationChannel(old)
    }
}
