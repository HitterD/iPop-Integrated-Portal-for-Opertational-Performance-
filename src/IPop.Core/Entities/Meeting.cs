namespace IPop.Core.Entities;

/// Rapat/undangan meeting (Mail Outlook & Kalender). Dikirim sebagai surat internal
/// (SourceMailId) dan punya daftar peserta dengan RSVP.
public class Meeting
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string? TimeZone { get; set; }
    public string? Location { get; set; }
    public string? Link { get; set; }
    public string? Agenda { get; set; }
    public Guid OrganizerId { get; set; }
    /// aktif | diubah | dibatalkan
    public string Status { get; set; } = "aktif";
    /// Surat undangan yang membawa rapat ini.
    public Guid? SourceMailId { get; set; }
    /// Ruang obrolan asal (bila dibuat dari ruang).
    public Guid? RoomId { get; set; }
    /// Menit sebelum mulai untuk pengingat.
    public int? ReminderMinutes { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Organizer { get; set; } = default!;
    public ICollection<MeetingAttendee> Attendees { get; set; } = [];
}

/// Peserta rapat beserta jawaban RSVP-nya.
public class MeetingAttendee
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    /// Wajib hadir (true) atau opsional (false).
    public bool Required { get; set; } = true;
    /// pending | accept | maybe | decline
    public string RsvpStatus { get; set; } = "pending";
    public string? Note { get; set; }
    public DateTime? RespondedAt { get; set; }

    public Meeting Meeting { get; set; } = default!;
    public User User { get; set; } = default!;
}

/// Event kalender (bisa dari rapat atau berdiri sendiri, bisa terkait ruang obrolan).
public class CalendarEvent
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    /// rapat | kerja
    public string Type { get; set; } = "kerja";
    public Guid OwnerId { get; set; }
    public Guid? RoomId { get; set; }
    public Guid? MeetingId { get; set; }
    public DateTime CreatedAt { get; set; }

    public User Owner { get; set; } = default!;
    public Meeting? Meeting { get; set; }
}

/// Pengingat terjadwal untuk rapat/event — diproses worker, dikirim via notifikasi.
public class MeetingReminder
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    public DateTime SendAt { get; set; }
    public DateTime? SentAt { get; set; }

    public Meeting Meeting { get; set; } = default!;
    public User User { get; set; } = default!;
}
