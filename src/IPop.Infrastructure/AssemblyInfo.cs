using System.Runtime.CompilerServices;

// Buka akses internal ke test project agar logika jam senyap backend
// (NotificationGatewayService.IsInQuietHours) dapat diuji langsung.
[assembly: InternalsVisibleTo("IPop.Tests")]
