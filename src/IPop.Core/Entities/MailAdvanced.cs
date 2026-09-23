namespace IPop.Core.Entities;

/// Label surat per pengguna (Surat Lanjutan, Fase 3): penanda manual maupun hasil aturan.
public class MailLabel
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; } // hex "#RRGGBB"
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
    public ICollection<MailLabelAssignment> Assignments { get; set; } = [];
}

/// Penempelan label ke surat (per label + surat).
public class MailLabelAssignment
{
    public Guid Id { get; set; }
    public Guid LabelId { get; set; }
    public Guid MailId { get; set; }
    public DateTime CreatedAt { get; set; }

    public MailLabel Label { get; set; } = default!;
    public Mail Mail { get; set; } = default!;
}

/// Aturan otomatis: bila kondisi cocok, tempel label ke surat masuk.
public class MailRule
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    /// Teks yang harus ada pada pengirim (nama/email), case-insensitive.
    public required string SenderContains { get; set; }
    public Guid LabelId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
    public MailLabel Label { get; set; } = default!;
}

/// Template surat per pengguna: format subjek+isi yang sering dipakai.
public class MailTemplate
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = default!;
}

/// Pengaturan balasan otomatis (out of office) per pengguna.
public class MailAutoReplySetting
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? UntilDate { get; set; }
    public string? Message { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
}
