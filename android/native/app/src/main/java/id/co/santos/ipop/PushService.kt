package id.co.santos.ipop

import android.content.Context
import android.util.Log
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

/** Data-only FCM messages: the app builds every notification itself (Notifier), then reports a receipt. */
class PushService : FirebaseMessagingService() {
    override fun onMessageReceived(message: RemoteMessage) {
        val d = message.data
        if (d.isEmpty()) return
        deliver(applicationContext, d, "fcm priority=${message.priority}/${message.originalPriority}")
    }

    override fun onNewToken(token: String) {
        Push.onNewToken(applicationContext, token)
    }

    companion object {
        /**
         * Show first, report after. Runs on the FCM worker thread (or a debug receiver thread); the receipt
         * POST blocks up to ~5 s, well inside onMessageReceived's ~10 s budget.
         */
        fun deliver(c: Context, d: Map<String, String>, source: String) {
            val cold = Receipts.coldStartFlag()
            val result = try {
                Notifier.handle(c, d)
            } catch (e: Exception) {
                Log.e(Ipop.TAG, "notify crashed", e)
                Notifier.Result("error", null, "${e.javaClass.simpleName}: ${e.message}")
            }
            val detail = buildString {
                if (result.detail.isNotBlank()) append(result.detail).append(' ')
                append(Receipts.environment(c, result.channel))
                append(" coldStart=").append(cold)
                append(" src=").append(source)
            }
            try {
                Receipts.report(c, d["deliveryId"], d["kind"].orEmpty(), result.outcome, detail)
            } catch (e: Exception) {
                Log.w(Ipop.TAG, "receipt failed: ${e.message}")
            }
        }
    }
}
