package id.co.santos.ipop

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.media.AudioAttributes
import android.media.RingtoneManager

object Channels {
    const val CHAT = "chat"
    const val MAIL = "mail"
    const val CALLS = "calls"
    const val MEETINGS = "meetings"
    const val TICKETS = "tickets"
    const val NEWS = "news"
    const val SYSTEM = "system"
    const val ONGOING = "ongoing_call"

    private val known = setOf(CHAT, MAIL, CALLS, MEETINGS, TICKETS, NEWS, SYSTEM)

    fun of(channel: String?): String = if (channel != null && channel in known) channel else SYSTEM

    fun ensure(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        val sound = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_NOTIFICATION)
        val attrs = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_COMMUNICATION_INSTANT)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
        fun ch(id: String, name: Int, importance: Int) = NotificationChannel(id, c.getString(name), importance).apply {
            setSound(sound, attrs)
            enableVibration(true)
            setShowBadge(true)
        }
        val list = listOf(
            ch(CHAT, R.string.ch_chat, NotificationManager.IMPORTANCE_HIGH),
            ch(MAIL, R.string.ch_mail, NotificationManager.IMPORTANCE_DEFAULT),
            // The ringtone is played by IncomingCallActivity (loop + vibrate), so the channel is silent.
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
    }
}
