package id.co.santos.ipop

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat

/**
 * Mockup frame 5: "Sedang menelepon" with Tutup / Bisukan. Runs as a foreground service of type microphone
 * so the WebView keeps capturing audio while the app is minimised (Android 11+ silences background mic
 * otherwise). If the service cannot start (mic not granted yet), the same notification is posted plainly.
 */
class OngoingCallService : Service() {
    companion object {
        private const val TAG = "ongoing_call"
        private const val ID = 2

        fun start(c: Context, name: String) {
            val i = Intent(c, OngoingCallService::class.java).putExtra("name", name)
            try {
                if (!Perms.granted(c, Manifest.permission.RECORD_AUDIO)) throw IllegalStateException("mic not granted")
                c.startForegroundService(i)
            } catch (e: Exception) {
                Log.w(Ipop.TAG, "ongoing call FGS unavailable: ${e.message}")
                try { c.getSystemService(NotificationManager::class.java).notify(TAG, ID, build(c, name)) } catch (_: SecurityException) { }
            }
        }

        fun stop(c: Context) {
            c.stopService(Intent(c, OngoingCallService::class.java))
            c.getSystemService(NotificationManager::class.java).cancel(TAG, ID)
        }

        fun build(c: Context, name: String): Notification {
            fun action(a: String) = PendingIntent.getBroadcast(c, a.hashCode(),
                Intent(c, ActionReceiver::class.java).setAction(a), PendingIntent.FLAG_IMMUTABLE)
            val open = PendingIntent.getActivity(c, 2, Intent(c, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_IMMUTABLE)
            return NotificationCompat.Builder(c, Channels.ONGOING)
                .setSmallIcon(R.drawable.ic_stat_ipop)
                .setColor(c.getColor(R.color.call_green))
                .setColorized(true)
                .setContentTitle(c.getString(R.string.ongoing_title))
                .setContentText(c.getString(R.string.ongoing_body, name.ifBlank { "iPop" }))
                .setUsesChronometer(true)
                .setWhen(System.currentTimeMillis())
                .setOngoing(true)
                .setCategory(NotificationCompat.CATEGORY_CALL)
                .setContentIntent(open)
                .addAction(R.drawable.ic_phone, c.getString(R.string.ongoing_hangup), action(ActionReceiver.CALL_HANGUP))
                .addAction(R.drawable.ic_mic, c.getString(R.string.ongoing_mute), action(ActionReceiver.CALL_MUTE))
                .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
                .build()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val n = build(this, intent?.getStringExtra("name").orEmpty())
        try {
            if (Build.VERSION.SDK_INT >= 30) {
                var type = ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
                if (Perms.granted(this, Manifest.permission.CAMERA)) type = type or ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
                startForeground(ID, n, type)
            } else {
                startForeground(ID, n)
            }
        } catch (e: Exception) {
            Log.w(Ipop.TAG, "startForeground failed: ${e.message}")
            try { getSystemService(NotificationManager::class.java).notify(TAG, ID, n) } catch (_: SecurityException) { }
            stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        // App swiped away: the WebView (and the call) is gone too.
        stop(this)
        super.onTaskRemoved(rootIntent)
    }
}
