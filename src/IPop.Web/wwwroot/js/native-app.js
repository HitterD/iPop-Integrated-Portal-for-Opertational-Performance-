// Bridge to the native Android app (android/native, window.iPopAndroid). In a normal browser every
// call here is a no-op. The native app owns notifications, calls and ringtone; the page tells it
// who is signed in and hands it a short-lived access token so it can register its FCM token and
// answer "Balas" / "Tandai dibaca" from a notification without opening the app.
(function (root) {
  "use strict";
  const bridge = () => (root.iPopAndroid && typeof root.iPopAndroid.getAppVersion === "function") ? root.iPopAndroid : null;
  let token = null;

  const N = {
    isNative: () => !!bridge(),
    appVersion: () => { try { return bridge() ? String(bridge().getAppVersion()) : null; } catch { return null; } },

    /// After sign-in and every token refresh. expiresAt: ISO string or epoch ms.
    session(accessToken, expiresAt) {
      token = accessToken || null;
      const b = bridge(); if (!b) return;
      const ms = typeof expiresAt === "number" ? expiresAt : Date.parse(expiresAt || "") || (Date.now() + 15 * 60 * 1000);
      try { if (typeof b.setSession === "function") b.setSession(token || "", ms); } catch { }
      try { if (token && typeof b.onSignedIn === "function") b.onSignedIn(); } catch { }
    },
    signedOut() {
      token = null;
      const b = bridge(); if (!b) return;
      try { if (typeof b.onSignedOut === "function") b.onSignedOut(); } catch { }
    },
    callStarted(name) { const b = bridge(); try { b && b.callStarted && b.callStarted(String(name || "")); } catch { } },
    callEnded() { const b = bridge(); try { b && b.callEnded && b.callEnded(); } catch { } },
    setBadge(n) { const b = bridge(); try { b && b.setBadge && b.setBadge(n | 0); } catch { } },
    permissions() { const b = bridge(); try { return b && b.getPermissionState ? JSON.parse(b.getPermissionState()) : null; } catch { return null; } },
    openSettings(kind) { const b = bridge(); try { b && b.openSettings && b.openSettings(String(kind)); } catch { } },
  };

  // "Tutup" / "Bisukan" on the native ongoing-call notification arrive as an 'ipop-native' event.
  let calls = null;
  N.bindCalls = ref => { calls = ref; };
  if (root.addEventListener) root.addEventListener("ipop-native", e => {
    const action = e && e.detail && e.detail.action;
    if (calls && (action === "hangup" || action === "mute")) { try { calls.invokeMethodAsync("OnNativeCallAction", action); } catch { } }
  });

  // The native app asks for the token (evaluateJavascript) when it needs to call the API itself.
  root.iPopNativeAuth = () => token;
  root.ipopNative = N;
})(typeof window !== "undefined" ? window : globalThis);
