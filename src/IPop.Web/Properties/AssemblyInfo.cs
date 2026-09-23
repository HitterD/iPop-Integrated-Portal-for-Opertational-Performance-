using System.Runtime.CompilerServices;

// Buka akses internal ke test project agar logika preferensi (NotificationPrefs,
// IsInQuietHours) bisa diuji langsung tanpa mengeksposnya sebagai API publik.
[assembly: InternalsVisibleTo("IPop.Tests")]
