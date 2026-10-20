package id.co.santos.ipop

import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

/** Data-only FCM messages: the app builds every notification itself (Notifier). */
class PushService : FirebaseMessagingService() {
    override fun onMessageReceived(message: RemoteMessage) {
        val d = message.data
        if (d.isEmpty()) return
        Notifier.handle(applicationContext, d)
    }

    override fun onNewToken(token: String) {
        Push.onNewToken(applicationContext, token)
    }
}
