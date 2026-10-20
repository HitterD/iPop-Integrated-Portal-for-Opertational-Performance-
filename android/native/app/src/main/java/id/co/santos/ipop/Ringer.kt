package id.co.santos.ipop

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

/**
 * Incoming-call ringtone + vibration, looping until answered / declined / 45 s / close push.
 * Started from the push (so it rings even if the full-screen activity is suppressed) and stopped
 * by whichever of IncomingCallActivity, ActionReceiver or a "close" push comes first.
 */
object Ringer {
    private val main = Handler(Looper.getMainLooper())
    private var ringtone: Ringtone? = null
    private var vibrator: Vibrator? = null
    @Volatile var activeTag: String? = null; private set
    private val timeout = Runnable { activeTag?.let { stop(it) } }

    fun start(c: Context, tag: String, timeoutMs: Long) = main.post {
        if (activeTag == tag) return@post
        stopNow()
        activeTag = tag
        val am = c.getSystemService(AudioManager::class.java)
        val mode = am.ringerMode
        if (mode == AudioManager.RINGER_MODE_NORMAL) {
            try {
                val uri = RingtoneManager.getActualDefaultRingtoneUri(c, RingtoneManager.TYPE_RINGTONE)
                    ?: RingtoneManager.getDefaultUri(RingtoneManager.TYPE_RINGTONE)
                ringtone = RingtoneManager.getRingtone(c, uri)?.apply {
                    audioAttributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
                    isLooping = true
                    play()
                }
            } catch (_: Exception) { }
        }
        if (mode != AudioManager.RINGER_MODE_SILENT) {
            vibrator = (if (android.os.Build.VERSION.SDK_INT >= 31)
                c.getSystemService(VibratorManager::class.java).defaultVibrator
            else @Suppress("DEPRECATION") c.getSystemService(Vibrator::class.java))
            val attrs = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_NOTIFICATION_RINGTONE).build()
            @Suppress("DEPRECATION")
            vibrator?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 800, 400, 800), 0), attrs)
        }
        main.removeCallbacks(timeout)
        main.postDelayed(timeout, timeoutMs)
    }

    /** Stop if [tag] is the ringing call (or always when tag is null). */
    fun stop(tag: String?) = main.post {
        if (tag == null || tag == activeTag) stopNow()
    }

    private fun stopNow() {
        main.removeCallbacks(timeout)
        try { ringtone?.stop() } catch (_: Exception) { }
        try { vibrator?.cancel() } catch (_: Exception) { }
        ringtone = null; vibrator = null; activeTag = null
    }
}
