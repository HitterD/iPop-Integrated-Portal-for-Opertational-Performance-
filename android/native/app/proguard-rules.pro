# JS bridge: R8 must keep the @JavascriptInterface methods by name.
-keepclassmembers class id.co.santos.ipop.Bridge {
    @android.webkit.JavascriptInterface <methods>;
}
-keepattributes JavascriptInterface
-keepattributes *Annotation*

# Firebase Messaging (consumer rules ship with the AAR; these are belt-and-braces for manual init).
-keep class com.google.firebase.messaging.FirebaseMessagingService { *; }
-keep class id.co.santos.ipop.PushService { *; }
-keep class com.google.firebase.components.ComponentRegistrar { *; }
-keep class * implements com.google.firebase.components.ComponentRegistrar { <init>(); }
-keep class com.google.firebase.FirebaseOptions { *; }
-dontwarn com.google.firebase.**
-dontwarn com.google.android.gms.**
