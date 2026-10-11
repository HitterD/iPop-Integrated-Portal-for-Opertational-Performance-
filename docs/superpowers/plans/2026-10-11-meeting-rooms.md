# Meeting Rooms (Calls & Meetings) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a "Meeting Rooms" tab to /calls (renamed "Calls & Meetings") with the five SJA Hall rooms — up to 20 people, mic/camera/screen share, per-room admin video switch, and recording either to the user's PC or to the server (LiveKit Egress → NAS).

**Architecture:** LiveKit is the single source of truth for who is in a room: the API mints a token for LiveKit room `hall-{key}` (same name the 3D Hall uses on `sja-hall`) after checking occupancy through the LiveKit RoomService (Twirp). Admin settings live in `AppSettings`. Server recordings are `MeetingRecording` rows driven by a worker that polls Egress, uploads the finished MP4 from a local spool folder to the NAS with the service account, notifies the viewers and purges after the retention days. The browser side is one JS module (`meeting-rooms.js`) owned by a scoped `MeetingRoomState` and one overlay component mounted in `MainLayout`, so the meeting survives navigation and minimizes to a pill.

**Tech Stack:** .NET 10 (ASP.NET minimal APIs, EF Core Npgsql, Blazor WASM), xunit + bUnit + SQLite `TestDb`, livekit-client 2.22.3 UMD (already in `wwwroot/js`), LiveKit server + `livekit/egress` + Redis in Docker on 10.10.6.13.

**Spec:** `docs/superpowers/specs/2026-10-11-meeting-rooms-design.md` · mockup `docs/mockups/meeting-rooms.html` (v2, Apple style — UI tasks must match it: roast-tone room tiles with frosted panel, segmented control, iOS grouped lists, FaceTime-style dark room with floating glass control dock and labelled round buttons, popover record menu, island notice, iOS Settings admin with switches and stepper; light + dark).

## Global Constraints

- Branch `meeting-rooms` (worktree `C:/Users/IT12/orca/workspaces/meeting-rooms`) off `login-brand` d35e249. Nothing goes to prod until the user says "deploy ke prod".
- Room keys/names exactly: `robusta` Robusta, `arabika` Arabika, `kalosi` Kalosi, `kapal-api` Kapal Api, `toraja` Toraja. LiveKit room name `hall-{key}`. Capacity 20.
- Keep the `sja-hall` names (`HallRoom`, `HallBooking`, `HallBookings`, `HallLimits`, `ILiveKitRoomAdmin`) and file paths so `sja-hall` merges later with minimal conflict.
- LiveKit identity = user Id (Guid "D" string), display name = user Name. Never send or show NIK to other users.
- Camera off on join; camera publish capped 640x360 @ 15 fps; screen share 1280x720 @ 10 fps.
- Server recording: MP4 H.264 720p, layout `speaker`; NAS folder `/sja/SJA SPJ/ICT/recordings/{room}/{yyyy-MM-dd}/`; retention default 90 days.
- Endpoints for users: `/api/meeting-rooms` with `.RequireAuthorization().RequirePermission("calls.use")`. Admin: `/api/admin/meeting-rooms` with `.RequireAuthorization("AdminOnly")`.
- UI copy in English, as in the mockup. Labels: "Calls & Meetings", "Meeting Rooms", "Record to this PC", "Record to server".
- Tests: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~<Class>"`. Full suite before each commit that touches shared code.
- Back up nothing needed: the worktree is fresh and clean. Never `taskkill /IM`; kill only by own PID.

## Review Focus

1. Same user joins twice (second tab or rejoin after a drop) while the room has 20 → must not be refused as "full"; their own identity does not count.
2. LiveKit not configured / unreachable → Rooms tab still renders (cards show "Unavailable"), Join shows a clear error, no 500.
3. Admin turns video off while cameras are on → cameras stop within seconds for everyone, camera button disabled; turning it back on re-enables without rejoin.
4. Server recording started twice at once for the same room → exactly one Egress; the second caller gets `already_recording`.
5. A user who was never in the room while it recorded → cannot list, play or download that recording (403/404), even with the id.

---

## File map

**Backend (new)**
- `src/IPop.Core/Entities/Hall.cs` — `HallRoom` (subset of sja-hall file).
- `src/IPop.Core/Entities/MeetingRecording.cs` — `MeetingRecording`, `MeetingRecordingViewer`.
- `src/IPop.Application/Hall/Hall.cs` — `HallLimits`, `HallBooking`, `HallRoomBooking` (subset of sja-hall file).
- `src/IPop.Application/MeetingRooms/MeetingRooms.cs` — DTOs, `MeetingRoomKeys`, `IMeetingRoomService`, `IMeetingRecordingService`.
- `src/IPop.Infrastructure/Hall/HallBookings.cs` — copied from sja-hall + `NextAsync`.
- `src/IPop.Infrastructure/Calls/LiveKitRoomAdmin.cs` — from sja-hall + `CreateRoomAsync`, `UpdateRoomMetadataAsync`, `SetSourcesAsync`.
- `src/IPop.Infrastructure/Calls/LiveKitEgress.cs` — `ILiveKitEgress` Twirp client.
- `src/IPop.Infrastructure/MeetingRooms/MeetingRoomService.cs`
- `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingService.cs`
- `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingWorker.cs`
- `src/IPop.Infrastructure/MeetingRooms/RecordingFileCache.cs`
- `src/IPop.Api/Endpoints/MeetingRoomEndpoints.cs`
- Migration `AddMeetingRooms`.

**Backend (modify)**
- `src/IPop.Application/Calls/ILiveKitTokenService.cs`, `src/IPop.Infrastructure/Calls/LiveKitTokenService.cs` — `sources`, `canUpdateOwnMetadata`, `GenerateAdminToken(room, ttl, record)`.
- `src/IPop.Application/Settings/ISettings.cs` — two keys.
- `src/IPop.Infrastructure/Persistence/IPopDbContext.cs` — DbSets, config, seed.
- `src/IPop.Api/Endpoints/MeetingEndpoints.cs` — room double-booking refusal + Normalize.
- `src/IPop.Infrastructure/Meetings/MeetingReminderWorker.cs` — reminder for a room meeting opens the room.
- `src/IPop.Api/Program.cs` — DI, worker, `MapMeetingRooms()`.

**Web (new)**
- `src/IPop.Web/Services/MeetingRoomState.cs`
- `src/IPop.Web/Components/Calls/MeetingRoomsTab.razor`
- `src/IPop.Web/Components/Calls/MeetingRoomOverlay.razor`
- `src/IPop.Web/Components/Admin/AdminMeetingRooms.razor`
- `src/IPop.Web/wwwroot/js/meeting-rooms.js`
- `src/IPop.Web/wwwroot/css/modules/meeting-rooms.css`

**Web (modify)**
- `Layout/MainLayout.razor` (label + mount overlay), `Pages/Calls.razor` (title, tab, deep link), `Pages/Admin.razor` (section), `Pages/Calendar.razor` + `Calendar.Compose.cs` (Join room, `location` prefill), `Services/CalendarLinks.cs` (`location`), `Program.cs` (DI), `wwwroot/index.html` (script + css), `TutorialRegistry.cs`, `Notifications.razor`, `NotificationSettingsDialog.razor`, `MeetingRoom.razor` (label strings).

**Infra**
- `deploy/livekit/docker-compose.yml`, `deploy/livekit/setup.ps1`, `deploy/livekit/egress.yaml.template`, `deploy/livekit/README.md`.

---

### Task 1: Hall room model, booking helpers and double-booking refusal

**Files:**
- Create: `src/IPop.Core/Entities/Hall.cs`, `src/IPop.Application/Hall/Hall.cs`, `src/IPop.Infrastructure/Hall/HallBookings.cs`
- Modify: `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`, `src/IPop.Api/Endpoints/MeetingEndpoints.cs`
- Test: `tests/IPop.Tests/HallBookingTests.cs` (copy from sja-hall + one new test)

**Interfaces:**
- Produces: `HallRoom { Guid Id; string Key; string Name; int Capacity; int SortOrder }`, `db.HallRooms`, `HallLimits.RoomCapacity = 20`, `HallBooking.Rooms/Prefix/Location(name)/RoomKey(location)/Normalize(location)`, `HallRoomBooking(Guid MeetingId, string Title, DateTime EndAt, bool Allowed)`, `HallBookings.CurrentAsync(db, key, userId, nowUtc, ct)`, `HallBookings.ConflictAsync(db, location, startUtc, endUtc, exceptId, ct)`, `HallBookings.NextAsync(db, key, nowUtc, ct) → HallNextBooking?`, `record HallNextBooking(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string OrganizerName)`, `MeetingEndpoints.CreateResult.RoomBooked`, `UpdateResult.RoomBooked`.

- [ ] **Step 1: Copy the tests from sja-hall and add the "next booking" test**

```bash
cp ../sja-hall/tests/IPop.Tests/HallBookingTests.cs tests/IPop.Tests/HallBookingTests.cs
```

Append inside the class:

```csharp
    [Fact]
    public async Task Next_booking_is_the_earliest_future_meeting_of_that_room_with_its_organizer()
    {
        using var t = new TestDb(); var org = t.AddUser("O2");
        var now = DateTime.UtcNow;
        await MeetingEndpoints.ApplyCreateAsync(t.Db, org.Id, Req(now.AddHours(5), 30, "SJA Hall · Toraja"), default);
        await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id, Req(now.AddHours(2), 30, "SJA Hall · Toraja"), default);
        await MeetingEndpoints.ApplyCreateAsync(t.Verify(), org.Id, Req(now.AddHours(1), 30, "SJA Hall · Robusta"), default);

        var next = await HallBookings.NextAsync(t.Verify(), "toraja", now, default);
        Assert.NotNull(next);
        Assert.Equal(now.AddHours(2), next!.StartAt, TimeSpan.FromSeconds(1));
        Assert.Equal("User O2", next.OrganizerName);
        Assert.Null(await HallBookings.NextAsync(t.Verify(), "kalosi", now, default));
    }

    [Fact]
    public void Five_rooms_are_seeded_with_capacity_20()
    {
        using var t = new TestDb();
        var rooms = t.Db.HallRooms.OrderBy(r => r.SortOrder).ToList();
        Assert.Equal(["robusta", "arabika", "kalosi", "kapal-api", "toraja"], rooms.Select(r => r.Key));
        Assert.All(rooms, r => Assert.Equal(20, r.Capacity));
    }
```

- [ ] **Step 2: Run, expect compile failure**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~HallBookingTests"`
Expected: FAIL — `HallBooking`, `HallBookings`, `HallRooms` not found.

- [ ] **Step 3: Entity** — `src/IPop.Core/Entities/Hall.cs`

```csharp
namespace IPop.Core.Entities;

/// One of the five SJA Hall meeting rooms. The same rooms are used by Calls & Meetings → Meeting Rooms and
/// (on sja-hall) by the 3D hall; LiveKit room "hall-{Key}".
public class HallRoom
{
    public Guid Id { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public int Capacity { get; set; }
    public int SortOrder { get; set; }
}
```

- [ ] **Step 4: Application helpers** — `src/IPop.Application/Hall/Hall.cs`

```csharp
namespace IPop.Application.Hall;

public static class HallLimits
{
    public const int RoomCapacity = 20;
}

/// Meeting rooms are booked through ordinary meetings whose Location is "SJA Hall · {Room name}". One helper
/// writes and reads that form so the meeting forms, the rooms tab and the conflict check agree.
public static class HallBooking
{
    public const string Prefix = "SJA Hall · ";
    public static readonly (string Key, string Name)[] Rooms =
        [("robusta", "Robusta"), ("arabika", "Arabika"), ("kalosi", "Kalosi"), ("kapal-api", "Kapal Api"), ("toraja", "Toraja")];

    public static string Location(string roomName) => Prefix + roomName;

    /// The hall room key for a meeting location, or null when it is not a hall room. Case and spacing are forgiven.
    public static string? RoomKey(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        var l = string.Join(' ', location.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!l.StartsWith("SJA Hall", StringComparison.OrdinalIgnoreCase)) return null;
        var name = l["SJA Hall".Length..].TrimStart(' ', '·', '-', ':', '•').Trim();
        return Rooms.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).Key;
    }

    /// Canonical location string for a hall room location typed in any of the forgiven forms; other text unchanged.
    public static string? Normalize(string? location) =>
        RoomKey(location) is { } key ? Location(Rooms.First(r => r.Key == key).Name) : location;
}

/// The booking holding a hall room right now. Allowed = the asking user is the organizer or an attendee.
public record HallRoomBooking(Guid MeetingId, string Title, DateTime EndAt, bool Allowed);

/// The next booking of a room (for the room card).
public record HallNextBooking(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string OrganizerName);
```

- [ ] **Step 5: Infrastructure queries** — copy `../sja-hall/src/IPop.Infrastructure/Hall/HallBookings.cs` verbatim, then add inside the class:

```csharp
    /// The earliest active meeting of this room that starts after now (null = nothing booked).
    public static async Task<HallNextBooking?> NextAsync(IPopDbContext db, string roomKey, DateTime nowUtc, CancellationToken ct)
    {
        var name = HallBooking.Rooms.FirstOrDefault(r => r.Key == roomKey).Name;
        if (name is null) return null;
        var loc = HallBooking.Location(name);
        return await db.Meetings.AsNoTracking()
            .Where(m => m.Status != MeetingStatus.Cancelled && m.Location == loc && m.StartAt > nowUtc)
            .OrderBy(m => m.StartAt)
            .Select(m => new HallNextBooking(m.Id, m.Title, m.StartAt, m.EndAt,
                db.Users.Where(u => u.Id == m.OrganizerId).Select(u => u.Name).FirstOrDefault() ?? ""))
            .FirstOrDefaultAsync(ct);
    }
```

- [ ] **Step 6: DbContext** — in `IPopDbContext.cs` add next to `AppSettings`:

```csharp
    public DbSet<HallRoom> HallRooms => Set<HallRoom>();
```

In `OnModelCreating` (next to the other entity blocks):

```csharp
        modelBuilder.Entity<HallRoom>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(64);
            e.HasData(HallRooms_Seed);
        });
```

And the static seed (same ids as sja-hall so its data lines up later):

```csharp
    private static readonly HallRoom[] HallRooms_Seed =
    [
        new() { Id = Guid.Parse("5a11a000-0000-4000-8000-000000000001"), Key = "robusta", Name = "Robusta", Capacity = 20, SortOrder = 1 },
        new() { Id = Guid.Parse("5a11a000-0000-4000-8000-000000000002"), Key = "arabika", Name = "Arabika", Capacity = 20, SortOrder = 2 },
        new() { Id = Guid.Parse("5a11a000-0000-4000-8000-000000000003"), Key = "kalosi", Name = "Kalosi", Capacity = 20, SortOrder = 3 },
        new() { Id = Guid.Parse("5a11a000-0000-4000-8000-000000000004"), Key = "kapal-api", Name = "Kapal Api", Capacity = 20, SortOrder = 4 },
        new() { Id = Guid.Parse("5a11a000-0000-4000-8000-000000000005"), Key = "toraja", Name = "Toraja", Capacity = 20, SortOrder = 5 },
    ];
```

- [ ] **Step 7: MeetingEndpoints** — apply exactly the sja-hall diff (seen with `git -C ../sja-hall diff 634ac86 -- src/IPop.Api/Endpoints/MeetingEndpoints.cs`): add `RoomBooked` to both enums, the two 409 `room_booked` cases ("That meeting room is already booked at this time."), the `HallBookings.ConflictAsync` checks in `ApplyCreateAsync`/`ApplyUpdateAsync`, and `HallBooking.Normalize(...)` on `Location` in both.

- [ ] **Step 8: Run tests, expect PASS**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~HallBookingTests|FullyQualifiedName~MeetingEndpointsTests|FullyQualifiedName~MeetingSchedulingTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "feat(rooms): SJA Hall rooms, room bookings and double-booking refusal"
```

---

### Task 2: LiveKit token sources, admin token and RoomService client

**Files:**
- Modify: `src/IPop.Application/Calls/ILiveKitTokenService.cs`, `src/IPop.Infrastructure/Calls/LiveKitTokenService.cs`
- Create: `src/IPop.Infrastructure/Calls/LiveKitRoomAdmin.cs`
- Test: `tests/IPop.Tests/LiveKitTokenTests.cs`, `tests/IPop.Tests/LiveKitRoomAdminTests.cs`

**Interfaces:**
- Produces:
  - `string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null, IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false)`
  - `string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false)`
  - `ILiveKitRoomAdmin { bool IsConfigured; Task<IReadOnlyList<LiveKitParticipant>> ListAsync(room, ct); Task<bool> MuteAsync(room, identity, ct); Task<bool> RemoveAsync(room, identity, ct); Task CreateRoomAsync(room, int maxParticipants, ct); Task UpdateRoomMetadataAsync(room, string metadata, ct); Task<bool> SetSourcesAsync(room, identity, IReadOnlyList<string> sources, ct); }`
  - `record LiveKitParticipant(string Identity, string Name, IReadOnlyList<LiveKitTrack> Tracks, IReadOnlyDictionary<string,string> Attributes)`, `record LiveKitTrack(string Sid, string Source, bool Muted)`; `LiveKitTrack.IsSource(string upper)` helper matching `"CAMERA"`/`"1"`, `"MICROPHONE"`/`"2"`, `"SCREEN_SHARE"`/`"3"`.

- [ ] **Step 1: Failing token tests** — append to `LiveKitTokenTests`:

```csharp
    [Fact]
    public void Sources_and_own_metadata_are_granted_only_when_asked()
    {
        var svc = CreateService();
        var plain = Payload(svc.GenerateToken("r", "u", "U")).GetProperty("video");
        Assert.False(plain.TryGetProperty("canPublishSources", out _));
        Assert.False(plain.TryGetProperty("canUpdateOwnMetadata", out _));

        var v = Payload(svc.GenerateToken("r", "u", "U", sources: ["microphone", "screen_share"], canUpdateOwnMetadata: true)).GetProperty("video");
        Assert.Equal(["microphone", "screen_share"], v.GetProperty("canPublishSources").EnumerateArray().Select(x => x.GetString()));
        Assert.True(v.GetProperty("canUpdateOwnMetadata").GetBoolean());
    }

    [Fact]
    public void Admin_token_grants_room_admin_and_optionally_record()
    {
        var svc = CreateService();
        var v = Payload(svc.GenerateAdminToken("hall-kalosi", record: true)).GetProperty("video");
        Assert.True(v.GetProperty("roomAdmin").GetBoolean());
        Assert.True(v.GetProperty("roomCreate").GetBoolean());
        Assert.True(v.GetProperty("roomRecord").GetBoolean());
        Assert.Equal("hall-kalosi", v.GetProperty("room").GetString());
        Assert.False(Payload(svc.GenerateAdminToken(null)).GetProperty("video").TryGetProperty("roomRecord", out _));
    }

    private static JsonElement Payload(string token) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(token.Split('.')[1]))).RootElement;
```

(`Base64UrlDecode` already exists in that test class.)

- [ ] **Step 2: Run, expect FAIL** (`GenerateAdminToken` / named args missing).

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~LiveKitTokenTests"`

- [ ] **Step 3: Interface**

```csharp
namespace IPop.Application.Calls;

public interface ILiveKitTokenService
{
    /// sources limits what the holder may publish (LiveKit source names: "microphone", "camera", "screen_share",
    /// "screen_share_audio"); null = anything. canUpdateOwnMetadata lets the client set its own attributes.
    string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null,
        IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false);
    string? ServerUrl { get; }
    /// Short-lived server token for RoomService/Egress (roomList always; roomAdmin+roomCreate for one room; roomRecord when record).
    string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false);
    bool IsConfigured { get; }
}
```

- [ ] **Step 4: Implementation** — in `LiveKitTokenService.GenerateToken` change the signature to match and after the `videoGrants` dictionary add:

```csharp
        if (sources is not null) videoGrants["canPublishSources"] = sources;
        if (canUpdateOwnMetadata) videoGrants["canUpdateOwnMetadata"] = true;
```

Add:

```csharp
    public string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false)
    {
        var key = ApiKey;
        var secret = ApiSecret;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("LiveKit ApiKey/ApiSecret are not configured.");
        var now = DateTimeOffset.UtcNow;
        var grants = new Dictionary<string, object> { ["roomList"] = true };
        if (room is not null) { grants["roomAdmin"] = true; grants["roomCreate"] = true; grants["room"] = room; }
        if (record) grants["roomRecord"] = true;
        var payload = new Dictionary<string, object>
        {
            ["iss"] = key, ["sub"] = "ipop-server", ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl ?? TimeSpan.FromMinutes(1)).ToUnixTimeSeconds(), ["video"] = grants,
        };
        var head = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" })));
        var body = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return $"{head}.{body}.{Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{head}.{body}")))}";
    }
```

Fix any other implementers of `ILiveKitTokenService` in tests (grep `: ILiveKitTokenService`) by adding the new members.

- [ ] **Step 5: Failing RoomService tests** — `tests/IPop.Tests/LiveKitRoomAdminTests.cs`

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using IPop.Infrastructure.Calls;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class LiveKitRoomAdminTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r.RequestUri!.AbsolutePath, body));
            return respond(r, body);
        }
    }

    private static (LiveKitRoomAdmin Admin, Stub Http) Make(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Calls:LiveKit:ApiKey"] = "k", ["Calls:LiveKit:ApiSecret"] = "s-123456789012345678901234567890",
            ["Calls:LiveKit:Url"] = "ws://lk.test:7880",
        }).Build();
        var stub = new Stub(respond);
        return (new LiveKitRoomAdmin(new HttpClient(stub), new LiveKitTokenService(cfg)), stub);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task List_parses_participants_tracks_and_attributes()
    {
        var (admin, http) = Make((_, _) => Json("""
            {"participants":[{"identity":"u1","name":"Sari","attributes":{"rec":"pc"},
              "tracks":[{"sid":"T1","source":"CAMERA","muted":false},{"sid":"T2","source":"SCREEN_SHARE"}]}]}
            """));
        var p = Assert.Single(await admin.ListAsync("hall-kalosi", default));
        Assert.Equal(("u1", "Sari", "pc"), (p.Identity, p.Name, p.Attributes["rec"]));
        Assert.True(p.Tracks[0].IsSource("CAMERA"));
        Assert.True(p.Tracks[1].IsSource("SCREEN_SHARE"));
        Assert.Equal("/twirp/livekit.RoomService/ListParticipants", http.Calls[0].Path);
    }

    [Fact]
    public async Task Missing_room_lists_nobody()
    {
        var (admin, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await admin.ListAsync("hall-x", default));
    }

    [Fact]
    public async Task Set_sources_updates_permission_and_mutes_camera_tracks_no_longer_allowed()
    {
        var (admin, http) = Make((r, _) => r.RequestUri!.AbsolutePath.EndsWith("ListParticipants")
            ? Json("""{"participants":[{"identity":"u1","name":"A","tracks":[{"sid":"C1","source":"CAMERA","muted":false},{"sid":"M1","source":"MICROPHONE","muted":false}]}]}""")
            : Json("{}"));
        Assert.True(await admin.SetSourcesAsync("hall-kalosi", "u1", ["microphone", "screen_share"], default));

        var update = http.Calls.Single(c => c.Path.EndsWith("UpdateParticipant"));
        Assert.Contains("\"can_publish_sources\":[\"MICROPHONE\",\"SCREEN_SHARE\"]", update.Body);
        var mute = http.Calls.Single(c => c.Path.EndsWith("MutePublishedTrack"));
        Assert.Contains("\"track_sid\":\"C1\"", mute.Body);
    }

    [Fact]
    public async Task Create_room_sends_max_participants()
    {
        var (admin, http) = Make((_, _) => Json("{}"));
        await admin.CreateRoomAsync("hall-toraja", 21, default);
        Assert.Contains("\"max_participants\":21", http.Calls.Single().Body);
    }
}
```

- [ ] **Step 6: Run, expect FAIL** (`LiveKitRoomAdmin` missing).

- [ ] **Step 7: Implement** `src/IPop.Infrastructure/Calls/LiveKitRoomAdmin.cs` — start from `../sja-hall/src/IPop.Infrastructure/Calls/LiveKitRoomAdmin.cs` and change to:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;

namespace IPop.Infrastructure.Calls;

/// Server-side LiveKit RoomService (Twirp over HTTP): who is in a room, create it with a cap, change what a
/// participant may publish, mute or remove someone. The http(s) base comes from the same Calls:LiveKit:Url as the clients.
public interface ILiveKitRoomAdmin
{
    bool IsConfigured { get; }
    /// Participants in a room (empty when the room does not exist).
    Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct);
    /// Mutes every published microphone track of the participant. False when they are not in the room.
    Task<bool> MuteAsync(string room, string identity, CancellationToken ct);
    Task<bool> RemoveAsync(string room, string identity, CancellationToken ct);
    /// Creates the room (or updates an existing one) with a hard participant cap.
    Task CreateRoomAsync(string room, int maxParticipants, CancellationToken ct);
    Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct);
    /// Replaces what the participant may publish; tracks of sources no longer allowed are muted at once.
    Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct);
}

public record LiveKitParticipant(string Identity, string Name, IReadOnlyList<LiveKitTrack> Tracks, IReadOnlyDictionary<string, string> Attributes);

public record LiveKitTrack(string Sid, string Source, bool Muted)
{
    private static readonly Dictionary<string, string> Numbers = new() { ["CAMERA"] = "1", ["MICROPHONE"] = "2", ["SCREEN_SHARE"] = "3", ["SCREEN_SHARE_AUDIO"] = "4" };
    /// The server may send the enum as its name or its number.
    public bool IsSource(string upperName) => Source == upperName || (Numbers.TryGetValue(upperName, out var n) && Source == n);
}

public sealed class LiveKitRoomAdmin(HttpClient http, ILiveKitTokenService tokens) : ILiveKitRoomAdmin
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => tokens.IsConfigured;

    private Uri Base => new(tokens.ServerUrl!.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/') + "/twirp/livekit.RoomService/");

    private async Task<JsonElement?> CallAsync(string method, string room, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, method)) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new("Bearer", tokens.GenerateAdminToken(room));
        using var res = await http.SendAsync(req, ct);
        // Twirp "not_found" (unknown room/participant) comes back as 404.
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct)
    {
        var r = await CallAsync("ListParticipants", room, new { room }, ct);
        if (r is not { } doc || !doc.TryGetProperty("participants", out var list)) return [];
        return list.EnumerateArray().Select(p => new LiveKitParticipant(
            p.GetProperty("identity").GetString() ?? "",
            p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            p.TryGetProperty("tracks", out var ts)
                ? ts.EnumerateArray().Select(t => new LiveKitTrack(
                    t.GetProperty("sid").GetString() ?? "",
                    t.TryGetProperty("source", out var s) ? s.ToString() : "",
                    t.TryGetProperty("muted", out var m) && m.GetBoolean())).ToList()
                : [],
            p.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "")
                : new Dictionary<string, string>())).ToList();
    }

    public async Task<bool> MuteAsync(string room, string identity, CancellationToken ct)
    {
        var p = (await ListAsync(room, ct)).FirstOrDefault(x => x.Identity == identity);
        if (p is null) return false;
        foreach (var t in p.Tracks.Where(t => !t.Muted && t.IsSource("MICROPHONE")))
            await CallAsync("MutePublishedTrack", room, new { room, identity, track_sid = t.Sid, muted = true }, ct);
        return true;
    }

    public async Task<bool> RemoveAsync(string room, string identity, CancellationToken ct) =>
        await CallAsync("RemoveParticipant", room, new { room, identity }, ct) is not null;

    public async Task CreateRoomAsync(string room, int maxParticipants, CancellationToken ct) =>
        await CallAsync("CreateRoom", room, new { name = room, max_participants = maxParticipants, empty_timeout = 300 }, ct);

    public async Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct) =>
        await CallAsync("UpdateRoomMetadata", room, new { room, metadata }, ct);

    public async Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct)
    {
        var p = (await ListAsync(room, ct)).FirstOrDefault(x => x.Identity == identity);
        if (p is null) return false;
        var upper = sources.Select(s => s.ToUpperInvariant()).ToList();
        await CallAsync("UpdateParticipant", room, new
        {
            room, identity,
            permission = new { can_subscribe = true, can_publish = true, can_publish_data = true, can_update_metadata = true, can_publish_sources = upper },
        }, ct);
        foreach (var t in p.Tracks.Where(t => !t.Muted && !upper.Any(t.IsSource)))
            await CallAsync("MutePublishedTrack", room, new { room, identity, track_sid = t.Sid, muted = true }, ct);
        return true;
    }
}
```

Note: `JsonSerializerDefaults.Web` camel-cases property names but leaves `snake_case` names as written — the anonymous members are already snake_case.

- [ ] **Step 8: Register in `src/IPop.Api/Program.cs`** near the other `AddHttpClient` lines:

```csharp
builder.Services.AddHttpClient<IPop.Infrastructure.Calls.ILiveKitRoomAdmin, IPop.Infrastructure.Calls.LiveKitRoomAdmin>(c => c.Timeout = TimeSpan.FromSeconds(5));
```

- [ ] **Step 9: Run tests, expect PASS**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~LiveKitTokenTests|FullyQualifiedName~LiveKitRoomAdminTests"`

- [ ] **Step 10: Commit** — `git commit -am "feat(rooms): LiveKit publish sources, admin token and RoomService client"` (add new files first).

---

### Task 3: Settings keys + MeetingRoomService (list, join, video switch)

**Files:**
- Modify: `src/IPop.Application/Settings/ISettings.cs`
- Create: `src/IPop.Application/MeetingRooms/MeetingRooms.cs`, `src/IPop.Infrastructure/MeetingRooms/MeetingRoomService.cs`
- Test: `tests/IPop.Tests/MeetingRoomServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 `db.HallRooms`, `HallBookings.CurrentAsync/NextAsync`; Task 2 `ILiveKitRoomAdmin`, `ILiveKitTokenService`.
- Produces (Application/MeetingRooms/MeetingRooms.cs):

```csharp
namespace IPop.Application.MeetingRooms;

public static class MeetingRoomKeys
{
    public static string LiveKitRoom(string key) => $"hall-{key}";
    public static string? KeyOf(string liveKitRoom) => liveKitRoom.StartsWith("hall-", StringComparison.Ordinal) ? liveKitRoom[5..] : null;
    public static readonly string[] AudioSources = ["microphone", "screen_share", "screen_share_audio"];
    public static readonly string[] VideoSources = ["microphone", "camera", "screen_share", "screen_share_audio"];
    /// LiveKit hard cap: 20 people + 1 hidden Egress recorder.
    public const int LiveKitMaxParticipants = 21;
}

public record MeetingRoomPerson(Guid UserId, string Name, bool Sharing, bool RecordingToPc);
public record MeetingRoomBookingDto(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, string? OrganizerName);
public record MeetingRoomRecordingState(Guid Id, string StartedByName, DateTime StartedAt);
public record MeetingRoomDto(string Key, string Name, int Capacity, bool VideoAllowed, bool Available,
    IReadOnlyList<MeetingRoomPerson> People, MeetingRoomBookingDto? Now, MeetingRoomBookingDto? Next, MeetingRoomRecordingState? Recording);
public record MeetingRoomJoinDto(string Key, string Name, string LiveKitRoom, string Url, string Token, bool VideoAllowed, string Identity);
public record MeetingRoomJoinResult(MeetingRoomJoinDto? Join, string? Error, IReadOnlyList<string> Suggestions);

public interface IMeetingRoomService
{
    Task<IReadOnlyList<MeetingRoomDto>> ListAsync(CancellationToken ct);
    /// Errors: "unknown_room", "media_unavailable", "room_full" (Suggestions = keys of rooms with space).
    Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, CancellationToken ct);
    Task<IReadOnlyDictionary<string, bool>> VideoAsync(CancellationToken ct);
    /// Saves the switch and, when turned off, stops every camera in that room now.
    Task SetVideoAsync(string key, bool allowed, string? actorNik, CancellationToken ct);
}
```

- Settings keys: `SettingKeys.MeetingRoomsVideo = "meetingRooms.video"` (default `new Dictionary<string, bool>()` — missing = allowed), `SettingKeys.MeetingRoomsRetentionDays = "meetingRooms.retentionDays"` (default `90`).

- [ ] **Step 1: Failing tests** — `tests/IPop.Tests/MeetingRoomServiceTests.cs`

```csharp
using IPop.Application.Calls;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomServiceTests
{
    internal sealed class FakeRoomAdmin : ILiveKitRoomAdmin
    {
        public bool IsConfigured { get; set; } = true;
        public bool Throws { get; set; }
        public Dictionary<string, List<LiveKitParticipant>> Rooms { get; } = new();
        public List<(string Room, int Max)> Created { get; } = [];
        public List<(string Room, string Identity, string Sources)> SourceChanges { get; } = [];
        public Task<IReadOnlyList<LiveKitParticipant>> ListAsync(string room, CancellationToken ct) =>
            Throws ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<LiveKitParticipant>>(Rooms.GetValueOrDefault(room) ?? []);
        public Task<bool> MuteAsync(string room, string identity, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> RemoveAsync(string room, string identity, CancellationToken ct) => Task.FromResult(true);
        public Task CreateRoomAsync(string room, int max, CancellationToken ct) { Created.Add((room, max)); return Task.CompletedTask; }
        public Task UpdateRoomMetadataAsync(string room, string metadata, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> SetSourcesAsync(string room, string identity, IReadOnlyList<string> sources, CancellationToken ct)
        { SourceChanges.Add((room, identity, string.Join(",", sources))); return Task.FromResult(true); }
        public void Fill(string key, int count, string? plus = null)
        {
            var list = Enumerable.Range(0, count).Select(i => new LiveKitParticipant(Guid.NewGuid().ToString(), $"P{i}", [], new Dictionary<string, string>())).ToList();
            if (plus is not null) list.Add(new LiveKitParticipant(plus, "Me", [], new Dictionary<string, string>()));
            Rooms[MeetingRoomKeys.LiveKitRoom(key)] = list;
        }
    }

    internal sealed class FakeTokens : ILiveKitTokenService
    {
        public bool IsConfigured { get; set; } = true;
        public string? ServerUrl => "wss://lk.test";
        public string? LastSources;
        public string GenerateToken(string roomName, string identity, string name, TimeSpan? ttl = null, IReadOnlyList<string>? sources = null, bool canUpdateOwnMetadata = false)
        { LastSources = sources is null ? null : string.Join(",", sources); return $"tok:{roomName}:{identity}"; }
        public string GenerateAdminToken(string? room, TimeSpan? ttl = null, bool record = false) => "admin";
    }

    private static MeetingRoomService Make(TestDb t, FakeRoomAdmin lk, FakeTokens? tokens = null) =>
        new(t.Db, lk, tokens ?? new FakeTokens(), new DbSettings(t.Db));

    [Fact]
    public async Task Join_mints_a_token_for_the_hall_room_with_camera_when_video_is_allowed()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens();
        var r = await Make(t, lk, tokens).JoinAsync(me.Id, "kalosi", default);

        Assert.Null(r.Error);
        Assert.Equal(("hall-kalosi", me.Id.ToString(), true), (r.Join!.LiveKitRoom, r.Join.Identity, r.Join.VideoAllowed));
        Assert.Contains("camera", tokens.LastSources);
        Assert.Equal(("hall-kalosi", 21), lk.Created.Single());
    }

    [Fact]
    public async Task Video_off_room_gets_no_camera_source()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); var tokens = new FakeTokens(); var svc = Make(t, lk, tokens);
        await svc.SetVideoAsync("kalosi", false, "admin", default);
        var r = await svc.JoinAsync(me.Id, "kalosi", default);
        Assert.False(r.Join!.VideoAllowed);
        Assert.DoesNotContain("camera", tokens.LastSources);
    }

    [Fact]
    public async Task Twenty_first_person_is_refused_with_rooms_that_have_space()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 20); lk.Fill("robusta", 20);
        var r = await Make(t, lk).JoinAsync(me.Id, "kapal-api", default);
        Assert.Equal("room_full", r.Error);
        Assert.Equal(["arabika", "kalosi", "toraja"], r.Suggestions);
    }

    [Fact]
    public async Task Rejoining_myself_in_a_full_room_is_not_refused()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        var lk = new FakeRoomAdmin(); lk.Fill("kapal-api", 19, plus: me.Id.ToString());
        Assert.Null((await Make(t, lk).JoinAsync(me.Id, "kapal-api", default)).Error);
    }

    [Fact]
    public async Task Unknown_room_and_unconfigured_media_are_errors_not_exceptions()
    {
        using var t = new TestDb(); var me = t.AddUser("U1");
        Assert.Equal("unknown_room", (await Make(t, new FakeRoomAdmin()).JoinAsync(me.Id, "gayo", default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin(), new FakeTokens { IsConfigured = false }).JoinAsync(me.Id, "kalosi", default)).Error);
        Assert.Equal("media_unavailable", (await Make(t, new FakeRoomAdmin { Throws = true }).JoinAsync(me.Id, "kalosi", default)).Error);
    }

    [Fact]
    public async Task List_shows_people_sharing_and_pc_recording_and_survives_livekit_down()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin();
        lk.Rooms["hall-arabika"] = [new("u1", "Budi", [new LiveKitTrack("S", "SCREEN_SHARE", false)], new Dictionary<string, string> { ["rec"] = "pc" })];
        var rooms = await Make(t, lk).ListAsync(default);
        Assert.Equal(5, rooms.Count);
        var arabika = rooms.Single(r => r.Key == "arabika");
        Assert.Equal(new MeetingRoomPerson(Guid.Empty, "Budi", true, true) with { UserId = arabika.People[0].UserId }, arabika.People[0]);

        lk.Throws = true;
        var down = await Make(t, lk).ListAsync(default);
        Assert.All(down, r => Assert.False(r.Available));
    }

    [Fact]
    public async Task Turning_video_off_strips_camera_from_everyone_in_the_room()
    {
        using var t = new TestDb();
        var lk = new FakeRoomAdmin(); lk.Fill("toraja", 2);
        await Make(t, lk).SetVideoAsync("toraja", false, "admin", default);
        Assert.Equal(2, lk.SourceChanges.Count);
        Assert.All(lk.SourceChanges, c => Assert.DoesNotContain("camera", c.Sources));
        Assert.False((await Make(t, lk).VideoAsync(default))["toraja"]);
    }
}
```

Note: identities in `LiveKitParticipant` that are not Guids map to `Guid.Empty` — the test above relies on that ("u1").

- [ ] **Step 2: Run, expect FAIL** (types missing).

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~MeetingRoomServiceTests"`

- [ ] **Step 3: Settings keys** — in `SettingKeys` add constants and defaults:

```csharp
    public const string MeetingRoomsVideo = "meetingRooms.video";
    public const string MeetingRoomsRetentionDays = "meetingRooms.retentionDays";
```
```csharp
        [MeetingRoomsVideo] = new Dictionary<string, bool>(),
        [MeetingRoomsRetentionDays] = 90,
```

- [ ] **Step 4: Application file** — create `src/IPop.Application/MeetingRooms/MeetingRooms.cs` with exactly the "Produces" block above.

- [ ] **Step 5: Service** — `src/IPop.Infrastructure/MeetingRooms/MeetingRoomService.cs`

```csharp
using IPop.Application.Calls;
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.MeetingRooms;

/// The five SJA Hall rooms as drop-in meeting rooms. LiveKit is the truth for who is inside.
public sealed class MeetingRoomService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitTokenService tokens, ISettings settings)
    : IMeetingRoomService
{
    public async Task<IReadOnlyList<MeetingRoomDto>> ListAsync(CancellationToken ct)
    {
        var rooms = await db.HallRooms.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync(ct);
        var video = await VideoAsync(ct);
        var now = DateTime.UtcNow;
        var recordings = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.Status == MeetingRecordingStatus.Recording)
            .Select(r => new { r.RoomKey, r.Id, r.StartedAt, Name = db.Users.Where(u => u.Id == r.StartedById).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var list = new List<MeetingRoomDto>();
        foreach (var room in rooms)
        {
            var (people, available) = await PeopleAsync(room.Key, ct);
            var current = await HallBookings.CurrentAsync(db, room.Key, Guid.Empty, now, ct);
            var next = await HallBookings.NextAsync(db, room.Key, now, ct);
            var rec = recordings.FirstOrDefault(r => r.RoomKey == room.Key);
            list.Add(new MeetingRoomDto(room.Key, room.Name, room.Capacity, video[room.Key], available, people,
                current is null ? null : new MeetingRoomBookingDto(current.MeetingId, current.Title, now, current.EndAt, null),
                next is null ? null : new MeetingRoomBookingDto(next.MeetingId, next.Title, next.StartAt, next.EndAt, next.OrganizerName),
                rec is null ? null : new MeetingRoomRecordingState(rec.Id, rec.Name ?? "", rec.StartedAt)));
        }
        return list;
    }

    private async Task<(IReadOnlyList<MeetingRoomPerson> People, bool Available)> PeopleAsync(string key, CancellationToken ct)
    {
        if (!liveKit.IsConfigured) return ([], false);
        try
        {
            var ps = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(key), ct);
            return (ps.Where(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal))   // the hidden Egress recorder
                .Select(p => new MeetingRoomPerson(Guid.TryParse(p.Identity, out var id) ? id : Guid.Empty, p.Name,
                    p.Tracks.Any(t => !t.Muted && t.IsSource("SCREEN_SHARE")),
                    p.Attributes.TryGetValue("rec", out var rec) && rec == "pc")).ToList(), true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return ([], false); }
    }

    public async Task<MeetingRoomJoinResult> JoinAsync(Guid userId, string key, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room", []);
        if (!tokens.IsConfigured || !liveKit.IsConfigured) return new(null, "media_unavailable", []);
        var me = userId.ToString();
        try
        {
            var inside = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(key), ct);
            var others = inside.Count(p => p.Identity != me && !p.Identity.StartsWith("EG_", StringComparison.Ordinal));
            if (others >= room.Capacity)
            {
                var free = new List<string>();
                foreach (var r in await db.HallRooms.AsNoTracking().Where(r => r.Key != key).OrderBy(r => r.SortOrder).ToListAsync(ct))
                    if ((await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(r.Key), ct)).Count(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)) < r.Capacity)
                        free.Add(r.Key);
                return new(null, "room_full", free);
            }
            await liveKit.CreateRoomAsync(MeetingRoomKeys.LiveKitRoom(key), MeetingRoomKeys.LiveKitMaxParticipants, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "media_unavailable", []); }

        var video = (await VideoAsync(ct))[key];
        var name = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).FirstAsync(ct);
        var lkRoom = MeetingRoomKeys.LiveKitRoom(key);
        var token = tokens.GenerateToken(lkRoom, me, name, TimeSpan.FromHours(6),
            video ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources, canUpdateOwnMetadata: true);
        return new(new MeetingRoomJoinDto(room.Key, room.Name, lkRoom, tokens.ServerUrl!, token, video, me), null, []);
    }

    public async Task<IReadOnlyDictionary<string, bool>> VideoAsync(CancellationToken ct)
    {
        var saved = await settings.GetAsync(SettingKeys.MeetingRoomsVideo, new Dictionary<string, bool>(), ct);
        return HallBooking.Rooms.ToDictionary(r => r.Key, r => !saved.TryGetValue(r.Key, out var on) || on);
    }

    public async Task SetVideoAsync(string key, bool allowed, string? actorNik, CancellationToken ct)
    {
        var saved = new Dictionary<string, bool>(await VideoAsync(ct)) { [key] = allowed };
        await settings.SetAsync(SettingKeys.MeetingRoomsVideo, saved, actorNik, ct);
        if (!liveKit.IsConfigured) return;
        var sources = allowed ? MeetingRoomKeys.VideoSources : MeetingRoomKeys.AudioSources;
        var room = MeetingRoomKeys.LiveKitRoom(key);
        try
        {
            foreach (var p in await liveKit.ListAsync(room, ct))
                if (!p.Identity.StartsWith("EG_", StringComparison.Ordinal))
                    await liveKit.SetSourcesAsync(room, p.Identity, sources, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* saved; applies on next join */ }
    }
}
```

`db.MeetingRecordings` / `MeetingRecordingStatus` come from Task 4 — implement Task 4 Step 3 (entity + DbSet) before running these tests, or temporarily comment the `recordings` query; Task 4 is next and the executor should do its Step 3 now if building fails.

- [ ] **Step 6: Run tests, expect PASS.**
- [ ] **Step 7: Commit** — `feat(rooms): meeting room list, join with capacity, per-room video switch`.

---

### Task 4: Recording entities, migration, Egress client and recording service

**Files:**
- Create: `src/IPop.Core/Entities/MeetingRecording.cs`, `src/IPop.Infrastructure/Calls/LiveKitEgress.cs`, `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingService.cs`
- Modify: `IPopDbContext.cs`, `src/IPop.Application/MeetingRooms/MeetingRooms.cs`
- Create migration `AddMeetingRooms`
- Test: `tests/IPop.Tests/MeetingRecordingServiceTests.cs`

**Interfaces:**
- Produces:

```csharp
// Core
public enum MeetingRecordingStatus { Recording = 0, Processing = 1, Ready = 2, Failed = 3, Deleted = 4 }
public class MeetingRecording { Guid Id; string RoomKey; string Title; string EgressId; Guid StartedById; DateTime StartedAt; DateTime? EndedAt;
    MeetingRecordingStatus Status; string? SpoolPath; string? NasPath; long? SizeBytes; int? DurationSeconds; string? Error; List<MeetingRecordingViewer> Viewers; }
public class MeetingRecordingViewer { Guid RecordingId; Guid UserId; }

// Application (append to MeetingRooms.cs)
public record MeetingRecordingDto(Guid Id, string RoomKey, string RoomName, string Title, string StartedByName, DateTime StartedAt,
    int? DurationSeconds, long? SizeBytes, int PeopleCount, string Status, DateTime DeleteAfter);
public record MeetingRecordingResult(MeetingRecordingDto? Recording, string? Error);
public interface IMeetingRecordingService
{
    /// Errors: "unknown_room", "not_in_room", "already_recording", "egress_unavailable".
    Task<MeetingRecordingResult> StartAsync(Guid userId, string key, CancellationToken ct);
    /// Anyone in the room may stop it. Errors: "not_found", "not_in_room".
    Task<MeetingRecordingResult> StopAsync(Guid userId, Guid recordingId, CancellationToken ct);
    /// Ready/Processing recordings the user may see (viewers), or all when admin.
    Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct);
    Task<bool> CanViewAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct);
    /// Adds a viewer if a server recording is running in that room (called on every join).
    Task NoteJoinAsync(Guid userId, string key, CancellationToken ct);
    Task<bool> DeleteAsync(Guid recordingId, CancellationToken ct);
}

// Infrastructure
public interface ILiveKitEgress
{
    bool IsConfigured { get; }
    Task<string> StartRoomCompositeAsync(string room, string filepath, CancellationToken ct);   // returns egress_id
    Task StopAsync(string egressId, CancellationToken ct);
    Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct);
}
public record LiveKitEgressInfo(string Status, string? Filename, long? Size, long? DurationNs, string? Error);
// Status values: "EGRESS_STARTING","EGRESS_ACTIVE","EGRESS_ENDING","EGRESS_COMPLETE","EGRESS_FAILED","EGRESS_ABORTED","EGRESS_LIMIT_REACHED"
```

- Config: `MeetingRooms:SpoolDir` (host folder the Egress container writes to; default `F:\ipop-data\recordings-spool`), `MeetingRooms:EgressOutDir` (path inside the container; default `/out`), `MeetingRooms:NasFolder` (default `/sja/SJA SPJ/ICT/recordings`).

- [ ] **Step 1: Failing tests** — `tests/IPop.Tests/MeetingRecordingServiceTests.cs`

```csharp
using IPop.Application.MeetingRooms;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPop.Tests;

public class MeetingRecordingServiceTests
{
    internal sealed class FakeEgress : ILiveKitEgress
    {
        public bool IsConfigured { get; set; } = true;
        public List<(string Room, string Path)> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public Dictionary<string, LiveKitEgressInfo> Infos { get; } = new();
        public Task<string> StartRoomCompositeAsync(string room, string filepath, CancellationToken ct)
        { Started.Add((room, filepath)); return Task.FromResult($"EG_{Started.Count}"); }
        public Task StopAsync(string egressId, CancellationToken ct) { Stopped.Add(egressId); return Task.CompletedTask; }
        public Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct) => Task.FromResult(Infos.GetValueOrDefault(egressId));
    }

    internal static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["MeetingRooms:SpoolDir"] = Path.Combine(Path.GetTempPath(), "ipop-rec-tests"), ["MeetingRooms:EgressOutDir"] = "/out",
    }).Build();

    private static MeetingRecordingService Make(TestDb t, MeetingRoomServiceTests.FakeRoomAdmin lk, FakeEgress eg) =>
        new(t.Db, lk, eg, new DbSettings(t.Db), Config());

    [Fact]
    public async Task Start_records_the_room_and_makes_everyone_inside_a_viewer()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>()), new(b.Id.ToString(), "B", [], new Dictionary<string, string>())];
        var eg = new FakeEgress();

        var r = await Make(t, lk, eg).StartAsync(a.Id, "kalosi", default);

        Assert.Null(r.Error);
        var (room, path) = eg.Started.Single();
        Assert.Equal("hall-kalosi", room);
        Assert.StartsWith("/out/kalosi/", path);
        Assert.EndsWith(".mp4", path);
        var row = await t.Verify().MeetingRecordings.Include(x => x.Viewers).SingleAsync();
        Assert.Equal((MeetingRecordingStatus.Recording, "Kalosi · ad-hoc"), (row.Status, row.Title));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), row.Viewers.Select(v => v.UserId).Order());
    }

    [Fact]
    public async Task Title_is_the_booking_running_now()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        await IPop.Api.Endpoints.MeetingEndpoints.ApplyCreateAsync(t.Db, a.Id,
            new("Production review", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(55), null, "SJA Hall · Arabika", null, null, null, null, null), default);
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-arabika"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        await Make(t, lk, new FakeEgress()).StartAsync(a.Id, "arabika", default);
        Assert.Equal("Production review", (await t.Verify().MeetingRecordings.SingleAsync()).Title);
    }

    [Fact]
    public async Task Only_one_server_recording_per_room_and_only_from_inside()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var out1 = t.AddUser("X1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);

        Assert.Equal("not_in_room", (await svc.StartAsync(out1.Id, "kalosi", default)).Error);
        Assert.Null((await svc.StartAsync(a.Id, "kalosi", default)).Error);
        Assert.Equal("already_recording", (await Make(t, lk, eg).StartAsync(a.Id, "kalosi", default)).Error);
        Assert.Single(eg.Started);
        Assert.Equal("egress_unavailable", (await Make(t, lk, new FakeEgress { IsConfigured = false }).StartAsync(a.Id, "toraja", default)).Error);
    }

    [Fact]
    public async Task Late_joiners_become_viewers_and_outsiders_cannot_view()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var late = t.AddUser("L1"); var never = t.AddUser("N1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var svc = Make(t, lk, new FakeEgress());
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        await svc.NoteJoinAsync(late.Id, "kalosi", default);
        await svc.NoteJoinAsync(late.Id, "kalosi", default);   // idempotent

        Assert.True(await svc.CanViewAsync(late.Id, false, rec.Id, default));
        Assert.False(await svc.CanViewAsync(never.Id, false, rec.Id, default));
        Assert.True(await svc.CanViewAsync(never.Id, true, rec.Id, default));
        Assert.Empty(await svc.ListAsync(never.Id, false, default));
    }

    [Fact]
    public async Task Stop_ends_the_egress_and_moves_to_processing()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var lk = new MeetingRoomServiceTests.FakeRoomAdmin();
        lk.Rooms["hall-kalosi"] = [new(a.Id.ToString(), "A", [], new Dictionary<string, string>())];
        var eg = new FakeEgress(); var svc = Make(t, lk, eg);
        var rec = (await svc.StartAsync(a.Id, "kalosi", default)).Recording!;
        var r = await svc.StopAsync(a.Id, rec.Id, default);
        Assert.Null(r.Error);
        Assert.Equal(["EG_1"], eg.Stopped);
        Assert.Equal(MeetingRecordingStatus.Processing, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Entity** — `src/IPop.Core/Entities/MeetingRecording.cs`

```csharp
namespace IPop.Core.Entities;

public enum MeetingRecordingStatus { Recording = 0, Processing = 1, Ready = 2, Failed = 3, Deleted = 4 }

/// A server (LiveKit Egress) recording of a meeting room. The file lives on the NAS; only Viewers
/// (people in the room while it recorded) and admins may see it.
public class MeetingRecording
{
    public Guid Id { get; set; }
    public required string RoomKey { get; set; }
    public required string Title { get; set; }
    public required string EgressId { get; set; }
    public Guid StartedById { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public MeetingRecordingStatus Status { get; set; }
    /// Path the Egress container writes to, relative to MeetingRooms:SpoolDir.
    public string? SpoolPath { get; set; }
    public string? NasPath { get; set; }
    public long? SizeBytes { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Error { get; set; }
    public List<MeetingRecordingViewer> Viewers { get; set; } = [];
}

public class MeetingRecordingViewer
{
    public Guid RecordingId { get; set; }
    public Guid UserId { get; set; }
}
```

DbContext:

```csharp
    public DbSet<MeetingRecording> MeetingRecordings => Set<MeetingRecording>();
    public DbSet<MeetingRecordingViewer> MeetingRecordingViewers => Set<MeetingRecordingViewer>();
```
```csharp
        modelBuilder.Entity<MeetingRecording>(e =>
        {
            e.Property(x => x.RoomKey).HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.EgressId).HasMaxLength(64);
            e.Property(x => x.SpoolPath).HasMaxLength(500);
            e.Property(x => x.NasPath).HasMaxLength(500);
            e.HasIndex(x => new { x.RoomKey, x.Status });
            e.HasMany(x => x.Viewers).WithOne().HasForeignKey(v => v.RecordingId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MeetingRecordingViewer>(e =>
        {
            e.HasKey(x => new { x.RecordingId, x.UserId });
            e.HasIndex(x => x.UserId);
        });
```

- [ ] **Step 4: Egress client** — `src/IPop.Infrastructure/Calls/LiveKitEgress.cs`

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.Calls;

namespace IPop.Infrastructure.Calls;

/// LiveKit Egress (Twirp livekit.Egress on the LiveKit server): room-composite MP4 recordings.
public interface ILiveKitEgress
{
    bool IsConfigured { get; }
    Task<string> StartRoomCompositeAsync(string room, string filepath, CancellationToken ct);
    Task StopAsync(string egressId, CancellationToken ct);
    Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct);
}

public record LiveKitEgressInfo(string Status, string? Filename, long? Size, long? DurationNs, string? Error);

public sealed class LiveKitEgress(HttpClient http, ILiveKitTokenService tokens) : ILiveKitEgress
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool IsConfigured => tokens.IsConfigured;
    private Uri Base => new(tokens.ServerUrl!.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/') + "/twirp/livekit.Egress/");

    private async Task<JsonElement> CallAsync(string method, string? room, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, method)) { Content = JsonContent.Create(body, options: Json) };
        req.Headers.Authorization = new("Bearer", tokens.GenerateAdminToken(room, record: true));
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
    }

    public async Task<string> StartRoomCompositeAsync(string room, string filepath, CancellationToken ct)
    {
        var r = await CallAsync("StartRoomCompositeEgress", room, new
        {
            room_name = room, layout = "speaker", preset = "H264_720P_30",
            file_outputs = new[] { new { file_type = "MP4", filepath } },
        }, ct);
        return r.GetProperty("egress_id").GetString()!;
    }

    public async Task StopAsync(string egressId, CancellationToken ct) => await CallAsync("StopEgress", null, new { egress_id = egressId }, ct);

    public async Task<LiveKitEgressInfo?> GetAsync(string egressId, CancellationToken ct)
    {
        var r = await CallAsync("ListEgress", null, new { egress_id = egressId }, ct);
        if (!r.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
        var e = items[0];
        string? file = null; long? size = null; long? dur = null;
        if (e.TryGetProperty("file_results", out var fr) && fr.GetArrayLength() > 0)
        {
            var f = fr[0];
            file = f.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
            size = f.TryGetProperty("size", out var s) ? long.Parse(s.ToString()) : null;
            dur = f.TryGetProperty("duration", out var d) ? long.Parse(d.ToString()) : null;
        }
        var status = e.TryGetProperty("status", out var st) ? st.ToString() : "";
        status = status switch { "0" => "EGRESS_STARTING", "1" => "EGRESS_ACTIVE", "2" => "EGRESS_ENDING", "3" => "EGRESS_COMPLETE", "4" => "EGRESS_FAILED", "5" => "EGRESS_ABORTED", "6" => "EGRESS_LIMIT_REACHED", _ => status };
        return new(status, file, size, dur, e.TryGetProperty("error", out var err) ? err.GetString() : null);
    }
}
```

Register in Program.cs: `builder.Services.AddHttpClient<IPop.Infrastructure.Calls.ILiveKitEgress, IPop.Infrastructure.Calls.LiveKitEgress>(c => c.Timeout = TimeSpan.FromSeconds(10));`

- [ ] **Step 5: Recording service** — `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingService.cs`

```csharp
using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.MeetingRooms;

public sealed class MeetingRecordingService(IPopDbContext db, ILiveKitRoomAdmin liveKit, ILiveKitEgress egress, ISettings settings, IConfiguration config)
    : IMeetingRecordingService
{
    private string OutDir => (config["MeetingRooms:EgressOutDir"] ?? "/out").TrimEnd('/');

    public async Task<MeetingRecordingResult> StartAsync(Guid userId, string key, CancellationToken ct)
    {
        var room = await db.HallRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
        if (room is null) return new(null, "unknown_room");
        if (!egress.IsConfigured || !liveKit.IsConfigured) return new(null, "egress_unavailable");
        var lkRoom = MeetingRoomKeys.LiveKitRoom(key);
        IReadOnlyList<LiveKitParticipant> inside;
        try { inside = await liveKit.ListAsync(lkRoom, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return new(null, "egress_unavailable"); }
        if (inside.All(p => p.Identity != userId.ToString())) return new(null, "not_in_room");
        if (await db.MeetingRecordings.AnyAsync(r => r.RoomKey == key && r.Status == MeetingRecordingStatus.Recording, ct))
            return new(null, "already_recording");

        var now = DateTime.UtcNow;
        var booking = await HallBookings.CurrentAsync(db, key, userId, now, ct);
        var id = Guid.NewGuid();
        var rel = $"{key}/{now.ToLocalTime():yyyy-MM-dd}/{now.ToLocalTime():HHmm}-{id:N}.mp4";
        var row = new MeetingRecording
        {
            Id = id, RoomKey = key, Title = booking?.Title ?? $"{room.Name} · ad-hoc", EgressId = "", StartedById = userId,
            StartedAt = now, Status = MeetingRecordingStatus.Recording, SpoolPath = rel,
        };
        foreach (var p in inside)
            if (Guid.TryParse(p.Identity, out var uid) && row.Viewers.All(v => v.UserId != uid))
                row.Viewers.Add(new MeetingRecordingViewer { RecordingId = id, UserId = uid });
        db.MeetingRecordings.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return new(null, "already_recording"); }   // unique filtered index lost the race

        try { row.EgressId = await egress.StartRoomCompositeAsync(lkRoom, $"{OutDir}/{rel}", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            db.MeetingRecordings.Remove(row);
            await db.SaveChangesAsync(ct);
            return new(null, "egress_unavailable");
        }
        await db.SaveChangesAsync(ct);
        await PublishStateAsync(lkRoom, row, ct);
        return new(await DtoAsync(row.Id, ct), null);
    }

    public async Task<MeetingRecordingResult> StopAsync(Guid userId, Guid recordingId, CancellationToken ct)
    {
        var row = await db.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.Status == MeetingRecordingStatus.Recording, ct);
        if (row is null) return new(null, "not_found");
        var inside = await liveKit.ListAsync(MeetingRoomKeys.LiveKitRoom(row.RoomKey), ct);
        if (inside.All(p => p.Identity != userId.ToString())) return new(null, "not_in_room");
        try { await egress.StopAsync(row.EgressId, ct); }
        catch (HttpRequestException) { /* already ended; the worker reads the final state */ }
        row.Status = MeetingRecordingStatus.Processing;
        row.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await PublishStateAsync(MeetingRoomKeys.LiveKitRoom(row.RoomKey), null, ct);
        return new(await DtoAsync(row.Id, ct), null);
    }

    /// Room metadata carries the running server recording so every client shows "Recording · started by X".
    private async Task PublishStateAsync(string lkRoom, MeetingRecording? row, CancellationToken ct)
    {
        var json = row is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new
        {
            recording = new { id = row.Id, by = await db.Users.Where(u => u.Id == row.StartedById).Select(u => u.Name).FirstAsync(ct), startedAt = row.StartedAt },
        });
        try { await liveKit.UpdateRoomMetadataAsync(lkRoom, json, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    public async Task NoteJoinAsync(Guid userId, string key, CancellationToken ct)
    {
        var rec = await db.MeetingRecordings.Where(r => r.RoomKey == key && r.Status == MeetingRecordingStatus.Recording)
            .Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (rec == Guid.Empty || await db.MeetingRecordingViewers.AnyAsync(v => v.RecordingId == rec && v.UserId == userId, ct)) return;
        db.MeetingRecordingViewers.Add(new MeetingRecordingViewer { RecordingId = rec, UserId = userId });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> CanViewAsync(Guid userId, bool admin, Guid recordingId, CancellationToken ct) =>
        await db.MeetingRecordings.AnyAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted
            && (admin || r.Viewers.Any(v => v.UserId == userId)), ct);

    public async Task<IReadOnlyList<MeetingRecordingDto>> ListAsync(Guid userId, bool admin, CancellationToken ct)
    {
        var ids = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.Status != MeetingRecordingStatus.Deleted && r.Status != MeetingRecordingStatus.Recording
                && (admin || r.Viewers.Any(v => v.UserId == userId)))
            .OrderByDescending(r => r.StartedAt).Select(r => r.Id).Take(200).ToListAsync(ct);
        var list = new List<MeetingRecordingDto>();
        foreach (var id in ids) list.Add(await DtoAsync(id, ct));
        return list;
    }

    public async Task<bool> DeleteAsync(Guid recordingId, CancellationToken ct)
    {
        var row = await db.MeetingRecordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.Status != MeetingRecordingStatus.Deleted, ct);
        if (row is null) return false;
        row.Status = MeetingRecordingStatus.Deleted;   // the worker removes the NAS file
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<MeetingRecordingDto> DtoAsync(Guid id, CancellationToken ct)
    {
        var days = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var r = await db.MeetingRecordings.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.RoomKey, x.Title, x.StartedAt, x.DurationSeconds, x.SizeBytes, x.Status, People = x.Viewers.Count,
            By = db.Users.Where(u => u.Id == x.StartedById).Select(u => u.Name).FirstOrDefault(),
        }).SingleAsync(ct);
        var name = HallBooking.Rooms.FirstOrDefault(x => x.Key == r.RoomKey).Name ?? r.RoomKey;
        return new(r.Id, r.RoomKey, name, r.Title, r.By ?? "", r.StartedAt, r.DurationSeconds, r.SizeBytes, r.People,
            r.Status.ToString().ToLowerInvariant(), r.StartedAt.AddDays(days));
    }
}
```

Add a filtered unique index so two simultaneous starts cannot both insert (Review Focus #4). In the `MeetingRecording` config:

```csharp
            e.HasIndex(x => x.RoomKey).IsUnique().HasFilter("\"Status\" = 0").HasDatabaseName("IX_MeetingRecordings_OneLivePerRoom");
```

(SQLite in tests supports the same filter syntax.)

- [ ] **Step 6: Run tests, expect PASS** (`MeetingRecordingServiceTests` + `MeetingRoomServiceTests`).

- [ ] **Step 7: Migration** — the API may be running (see memory dotnet-ef-while-api-running): build to a separate output and use `--no-build`:

```bash
dotnet build src/IPop.Api -o .scratch/efbuild
dotnet ef migrations add AddMeetingRooms --project src/IPop.Infrastructure --startup-project src/IPop.Api --no-build -- --contentRoot src/IPop.Api
```

If `--no-build` cannot find the assembly, run `dotnet ef migrations add AddMeetingRooms --project src/IPop.Infrastructure --startup-project src/IPop.Api` (this worktree's API is not running). Open the generated file: it must create `HallRooms` (+5 seed rows), `MeetingRecordings`, `MeetingRecordingViewers` and nothing else. If it contains unrelated model drift, stop and report.

- [ ] **Step 8: Full test suite**

Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj`
Expected: all pass.

- [ ] **Step 9: Commit** — `feat(rooms): server recording model, Egress client and recording service`.

---

### Task 5: Recording worker (finish → NAS → notify, retention purge) and playback cache

**Files:**
- Create: `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingWorker.cs`, `src/IPop.Infrastructure/MeetingRooms/RecordingFileCache.cs`
- Modify: `src/IPop.Api/Program.cs`
- Test: `tests/IPop.Tests/MeetingRecordingWorkerTests.cs`

**Interfaces:**
- Consumes: `IFileAccessService.UploadAsServiceAsync(dir, filename, stream, ct)`, `DeleteAsServiceAsync(path, ct)`, `DownloadAsServiceAsync(path, ct)`, `HasServiceAccount`; `INotificationGatewayService.PublishInternalAsync(...)`; `ILiveKitEgress.GetAsync`.
- Produces: `MeetingRecordingWorker` with `public static Task<int> FinishAsync(IServiceProvider sp, CancellationToken ct)` and `public static Task<int> PurgeAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct)` (both testable without the loop); `RecordingFileCache.OpenAsync(MeetingRecording row, CancellationToken ct) → Task<string?>` returning a local file path suitable for range serving.

- [ ] **Step 1: Failing tests** — `tests/IPop.Tests/MeetingRecordingWorkerTests.cs`

```csharp
using IPop.Application.Files;
using IPop.Application.Notifications;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class MeetingRecordingWorkerTests
{
    private sealed class Nas : IFileAccessService
    {
        // Only the service-account members are used here; every other member throws (DispatchProxy-free fake).
        public List<string> Uploaded { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool HasServiceAccount => true;
        public string? ServiceServer => "ict";
        public Task UploadAsServiceAsync(string dir, string filename, Stream content, CancellationToken ct) { Uploaded.Add($"{dir}/{filename}"); return Task.CompletedTask; }
        public Task DeleteAsServiceAsync(string path, CancellationToken ct) { Deleted.Add(path); return Task.CompletedTask; }
        public Task<Stream> DownloadAsServiceAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        // … implement the remaining IFileAccessService members with `throw new NotSupportedException();`
    }
}
```

The executor must open `src/IPop.Application/Files/IFileAccessService.cs` and add `=> throw new NotSupportedException()` bodies for every remaining member so `Nas` compiles (existing fakes: grep `: IFileAccessService` in tests and copy one if it exists). Then add the tests:

```csharp
    private sealed class Gateway : INotificationGatewayService { /* copy RecordingGateway from MeetingSchedulingTests.cs; it records (eventType, title, target, targetUrl) */ }

    private static (ServiceProvider Sp, string Spool) Services(TestDb t, MeetingRecordingServiceTests.FakeEgress eg, Nas nas, Gateway gw)
    {
        var spool = Path.Combine(Path.GetTempPath(), "ipop-rec-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["MeetingRooms:SpoolDir"] = spool, ["MeetingRooms:NasFolder"] = "/sja/SJA SPJ/ICT/recordings" }).Build();
        var sc = new ServiceCollection();
        sc.AddSingleton<IConfiguration>(cfg);
        sc.AddScoped(_ => t.Verify());
        sc.AddScoped<ISettings>(p => new DbSettings(p.GetRequiredService<IPopDbContext>()));
        sc.AddSingleton<ILiveKitEgress>(eg);
        sc.AddSingleton<IFileAccessService>(nas);
        sc.AddSingleton<INotificationGatewayService>(gw);
        return (sc.BuildServiceProvider(), spool);
    }

    private static MeetingRecording Row(TestDb t, MeetingRecordingStatus status, Guid by, params Guid[] viewers)
    {
        var id = Guid.NewGuid();
        var r = new MeetingRecording
        {
            Id = id, RoomKey = "kalosi", Title = "Weekly sync", EgressId = "EG_1", StartedById = by, StartedAt = DateTime.UtcNow.AddHours(-1),
            Status = status, SpoolPath = "kalosi/2026-10-11/1000-x.mp4",
            Viewers = viewers.Select(v => new MeetingRecordingViewer { RecordingId = id, UserId = v }).ToList(),
        };
        t.Db.MeetingRecordings.Add(r); t.Db.SaveChanges();
        return r;
    }

    [Fact]
    public async Task Complete_egress_is_uploaded_to_the_nas_folder_and_viewers_are_notified()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var row = Row(t, MeetingRecordingStatus.Processing, a.Id, a.Id, b.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new("EGRESS_COMPLETE", "/out/kalosi/2026-10-11/1000-x.mp4", 1234, 125_000_000_000, null);
        var nas = new Nas(); var gw = new Gateway();
        var (sp, spool) = Services(t, eg, nas, gw);
        Directory.CreateDirectory(Path.Combine(spool, "kalosi", "2026-10-11"));
        await File.WriteAllBytesAsync(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4"), [1, 2, 3]);

        Assert.Equal(1, await MeetingRecordingWorker.FinishAsync(sp, default));

        var saved = await t.Verify().MeetingRecordings.SingleAsync();
        Assert.Equal(MeetingRecordingStatus.Ready, saved.Status);
        Assert.Equal("/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-11/1000-x.mp4", saved.NasPath);
        Assert.Equal((1234L, 125), (saved.SizeBytes!.Value, saved.DurationSeconds!.Value));
        Assert.Equal(["/sja/SJA SPJ/ICT/recordings/kalosi/2026-10-11/1000-x.mp4"], nas.Uploaded);
        Assert.False(File.Exists(Path.Combine(spool, "kalosi", "2026-10-11", "1000-x.mp4")));
        Assert.Equal(2, gw.Sent.Count(s => s.EventType == "meeting.recording.ready"));
    }

    [Fact]
    public async Task Failed_egress_marks_failed_and_tells_who_started_it()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        Row(t, MeetingRecordingStatus.Recording, a.Id, a.Id);
        var eg = new MeetingRecordingServiceTests.FakeEgress();
        eg.Infos["EG_1"] = new("EGRESS_FAILED", null, null, null, "chrome crashed");
        var gw = new Gateway(); var (sp, _) = Services(t, eg, new Nas(), gw);
        await MeetingRecordingWorker.FinishAsync(sp, default);
        Assert.Equal(MeetingRecordingStatus.Failed, (await t.Verify().MeetingRecordings.SingleAsync()).Status);
        Assert.Single(gw.Sent, s => s.EventType == "meeting.recording.failed");
    }

    [Fact]
    public async Task Purge_deletes_old_and_admin_deleted_files_from_the_nas()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var old = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var fresh = Row(t, MeetingRecordingStatus.Ready, a.Id, a.Id);
        var gone = Row(t, MeetingRecordingStatus.Deleted, a.Id, a.Id);
        var db = t.Verify();
        foreach (var r in db.MeetingRecordings) r.NasPath = $"/n/{r.Id}.mp4";
        db.MeetingRecordings.Single(r => r.Id == old.Id).StartedAt = DateTime.UtcNow.AddDays(-91);
        db.SaveChanges();
        var nas = new Nas(); var (sp, _) = Services(t, new MeetingRecordingServiceTests.FakeEgress(), nas, new Gateway());

        Assert.Equal(2, await MeetingRecordingWorker.PurgeAsync(sp, DateTime.UtcNow, default));
        Assert.Equal(new[] { $"/n/{old.Id}.mp4", $"/n/{gone.Id}.mp4" }.Order(), nas.Deleted.Order());
        var after = t.Verify().MeetingRecordings.ToDictionary(r => r.Id);
        Assert.Equal(MeetingRecordingStatus.Deleted, after[old.Id].Status);
        Assert.Null(after[gone.Id].NasPath);
        Assert.Equal(MeetingRecordingStatus.Ready, after[fresh.Id].Status);
    }
```

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Worker** — `src/IPop.Infrastructure/MeetingRooms/MeetingRecordingWorker.cs`

```csharp
using IPop.Application.Files;
using IPop.Application.Hall;
using IPop.Application.Notifications;
using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// Every 15 s: finished Egress recordings go from the spool folder to the NAS and their viewers are told.
/// Once a night (02:30 local): recordings past the retention days, and ones an admin deleted, leave the NAS.
public sealed class MeetingRecordingWorker(IServiceProvider services, ILogger<MeetingRecordingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateOnly.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FinishAsync(services, stoppingToken);
                var local = DateTime.Now;
                if (local.Hour >= 2 && local.Minute >= 30 && DateOnly.FromDateTime(local) != lastPurge)
                {
                    await PurgeAsync(services, DateTime.UtcNow, stoppingToken);
                    lastPurge = DateOnly.FromDateTime(local);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Meeting recording worker pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    public static async Task<int> FinishAsync(IServiceProvider sp, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var egress = scope.ServiceProvider.GetRequiredService<ILiveKitEgress>();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var notify = scope.ServiceProvider.GetRequiredService<INotificationGatewayService>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var spool = config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool";
        var nasRoot = (config["MeetingRooms:NasFolder"] ?? "/sja/SJA SPJ/ICT/recordings").TrimEnd('/');
        var done = 0;

        var open = await db.MeetingRecordings.Include(r => r.Viewers)
            .Where(r => r.Status == MeetingRecordingStatus.Recording || r.Status == MeetingRecordingStatus.Processing)
            .ToListAsync(ct);
        foreach (var row in open)
        {
            if (string.IsNullOrEmpty(row.EgressId)) continue;
            var info = await egress.GetAsync(row.EgressId, ct);
            if (info is null) continue;
            if (info.Status is "EGRESS_FAILED" or "EGRESS_ABORTED")
            {
                row.Status = MeetingRecordingStatus.Failed; row.Error = info.Error; row.EndedAt ??= DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await notify.PublishInternalAsync("meetings", "meeting.recording.failed", "Recording failed",
                    $"The server recording of {row.Title} could not be saved.", new NotificationTarget(UserId: row.StartedById),
                    targetUrl: "/calls?tab=rooms", ct: ct);
                done++;
                continue;
            }
            if (info.Status is not ("EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED")) continue;

            var local = Path.Combine(spool, row.SpoolPath!.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(local)) continue;   // the container may still be flushing; try next pass
            var dir = $"{nasRoot}/{Path.GetDirectoryName(row.SpoolPath)!.Replace('\\', '/')}";
            var file = Path.GetFileName(row.SpoolPath);
            await using (var fs = File.OpenRead(local))
                await nas.UploadAsServiceAsync(dir, file, fs, ct);
            File.Delete(local);
            row.NasPath = $"{dir}/{file}";
            row.SizeBytes = info.Size;
            row.DurationSeconds = info.DurationNs is { } ns ? (int)(ns / 1_000_000_000) : null;
            row.Status = MeetingRecordingStatus.Ready;
            row.EndedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await notify.PublishInternalAsync("meetings", "meeting.recording.ready", "Recording ready",
                $"{row.Title} ({HallBooking.Rooms.FirstOrDefault(r => r.Key == row.RoomKey).Name}) can be watched now.",
                new NotificationTarget(UserIds: row.Viewers.Select(v => v.UserId).ToList()),
                targetUrl: $"/calls?tab=rooms&recording={row.Id}", ct: ct);
            done++;
        }
        return done;
    }

    public static async Task<int> PurgeAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var nas = scope.ServiceProvider.GetRequiredService<IFileAccessService>();
        var days = await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct);
        var cutoff = nowUtc.AddDays(-Math.Max(1, days));
        var rows = await db.MeetingRecordings
            .Where(r => r.NasPath != null && (r.Status == MeetingRecordingStatus.Deleted
                || ((r.Status == MeetingRecordingStatus.Ready || r.Status == MeetingRecordingStatus.Failed) && r.StartedAt < cutoff)))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            await nas.DeleteAsServiceAsync(r.NasPath!, ct);
            r.NasPath = null;
            r.Status = MeetingRecordingStatus.Deleted;
        }
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
```

Check `NotificationTarget`'s actual positional/named parameters in `src/IPop.Application/Notifications` and adapt the two `new NotificationTarget(...)` calls (named args `UserId:` / `UserIds:`).

- [ ] **Step 4: Playback cache** — `src/IPop.Infrastructure/MeetingRooms/RecordingFileCache.cs`. The NAS stream is not seekable, and MP4 seeking needs HTTP ranges, so the first play copies the file into a local cache folder (`MeetingRooms:CacheDir`, default `{SpoolDir}\..\recordings-cache`) and later plays serve it with ranges; files untouched for 24 h are deleted on each call.

```csharp
using IPop.Application.Files;
using IPop.Core.Entities;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.MeetingRooms;

public sealed class RecordingFileCache(IFileAccessService nas, IConfiguration config)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private string Dir => config["MeetingRooms:CacheDir"]
        ?? Path.Combine(Path.GetDirectoryName((config["MeetingRooms:SpoolDir"] ?? @"F:\ipop-data\recordings-spool").TrimEnd('\\', '/'))!, "recordings-cache");

    public async Task<string?> OpenAsync(MeetingRecording row, CancellationToken ct)
    {
        if (row.NasPath is null) return null;
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, row.Id.ToString("N") + ".mp4");
        await Gate.WaitAsync(ct);
        try
        {
            foreach (var old in new DirectoryInfo(Dir).GetFiles("*.mp4").Where(f => f.LastAccessTimeUtc < DateTime.UtcNow.AddHours(-24) && f.FullName != path))
                try { old.Delete(); } catch (IOException) { }
            if (!File.Exists(path))
            {
                var tmp = path + ".part";
                await using (var src = await nas.DownloadAsServiceAsync(row.NasPath, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                File.Move(tmp, path, overwrite: true);
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        finally { Gate.Release(); }
    }
}
```

- [ ] **Step 5: Register** in `Program.cs` (next to `MeetingReminderWorker`):

```csharp
builder.Services.AddScoped<IPop.Application.MeetingRooms.IMeetingRoomService, IPop.Infrastructure.MeetingRooms.MeetingRoomService>();
builder.Services.AddScoped<IPop.Application.MeetingRooms.IMeetingRecordingService, IPop.Infrastructure.MeetingRooms.MeetingRecordingService>();
builder.Services.AddSingleton<IPop.Infrastructure.MeetingRooms.RecordingFileCache>();
builder.Services.AddHostedService<IPop.Infrastructure.MeetingRooms.MeetingRecordingWorker>();
```

`RecordingFileCache` is a singleton but `IFileAccessService` may be scoped — check its registration; if scoped, register the cache as scoped (the static `Gate` keeps it single-writer).

- [ ] **Step 6: Run tests, expect PASS** (`MeetingRecordingWorkerTests`).
- [ ] **Step 7: Commit** — `feat(rooms): recording worker uploads to NAS, notifies viewers, purges by retention`.

---

### Task 6: API endpoints (user + admin) and reminder link

**Files:**
- Create: `src/IPop.Api/Endpoints/MeetingRoomEndpoints.cs`
- Modify: `src/IPop.Api/Program.cs` (`app.MapMeetingRooms();` after `app.MapMeetings();`), `src/IPop.Infrastructure/Meetings/MeetingReminderWorker.cs`
- Test: `tests/IPop.Tests/MeetingRoomEndpointTests.cs`

**Interfaces:**
- Produces routes:
  - `GET  /api/meeting-rooms` → `MeetingRoomDto[]`
  - `POST /api/meeting-rooms/{key}/join` → 200 `MeetingRoomJoinDto` | 404 `unknown_room` | 409 `{code:"room_full", message, suggestions:[keys]}` | 503 `media_unavailable`. On success also calls `NoteJoinAsync`.
  - `POST /api/meeting-rooms/{key}/recordings` → 200 `MeetingRecordingDto` | 409 `already_recording` | 403 `not_in_room` | 503 `egress_unavailable` | 404 `unknown_room`
  - `POST /api/meeting-rooms/recordings/{id}/stop` → 200 | 404 | 403
  - `GET  /api/meeting-rooms/recordings` → `MeetingRecordingDto[]` (viewer only; admins see only their own here)
  - `GET  /api/meeting-rooms/recordings/{id}/file?download=1` → video/mp4 with range; 404 when not a viewer (do not reveal existence).
  - Admin: `GET /api/admin/meeting-rooms` → `{ video: {key:bool}, retentionDays:int, egress: "online"|"offline"|"not_configured", recordings: MeetingRecordingDto[] }`; `PUT /api/admin/meeting-rooms/{key}/video` body `{ allowed: bool }`; `PUT /api/admin/meeting-rooms/retention` body `{ days: int }` (1..3650, else 400 `invalid_days`); `DELETE /api/admin/meeting-rooms/recordings/{id}`.
- Pure static helpers for tests: `MeetingRoomEndpoints.JoinResponse(MeetingRoomJoinResult) → IResult`, `MeetingRoomEndpoints.RecordingResponse(MeetingRecordingResult) → IResult`.

- [ ] **Step 1: Failing tests** — `tests/IPop.Tests/MeetingRoomEndpointTests.cs`

```csharp
using IPop.Api.Endpoints;
using IPop.Application.MeetingRooms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomEndpointTests
{
    private static int Status(IResult r) => r is IStatusCodeHttpResult s ? s.StatusCode ?? 200 : 200;

    [Theory]
    [InlineData("unknown_room", 404)]
    [InlineData("room_full", 409)]
    [InlineData("media_unavailable", 503)]
    public void Join_errors_map_to_status(string code, int status) =>
        Assert.Equal(status, Status(MeetingRoomEndpoints.JoinResponse(new(null, code, ["kalosi"]))));

    [Theory]
    [InlineData("unknown_room", 404)]
    [InlineData("not_found", 404)]
    [InlineData("not_in_room", 403)]
    [InlineData("already_recording", 409)]
    [InlineData("egress_unavailable", 503)]
    public void Recording_errors_map_to_status(string code, int status) =>
        Assert.Equal(status, Status(MeetingRoomEndpoints.RecordingResponse(new(null, code))));

    [Fact]
    public void Join_success_is_ok() =>
        Assert.Equal(200, Status(MeetingRoomEndpoints.JoinResponse(new(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", true, "id"), null, []))));
}
```

Add one reminder test to `MeetingSchedulingTests` (or a new `MeetingRoomReminderTests`) asserting a reminder for a meeting located "SJA Hall · Kalosi" targets `/calls?tab=rooms&join=kalosi`. Model it on the existing reminder test in `MeetingSchedulingTests.cs` (find the test that asserts `targetUrl` and copy its arrange/act; change Location and the expected URL).

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Endpoints** — `src/IPop.Api/Endpoints/MeetingRoomEndpoints.cs`

```csharp
using System.Text.Json;
using IPop.Api.Rbac;
using IPop.Application.MeetingRooms;
using IPop.Application.Settings;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Calls & Meetings → Meeting Rooms: the five SJA Hall rooms (LiveKit "hall-{key}"), server recordings and the admin switches.
public static class MeetingRoomEndpoints
{
    public record VideoRequest(bool Allowed);
    public record RetentionRequest(int Days);

    public static IEndpointRouteBuilder MapMeetingRooms(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/meeting-rooms").RequireAuthorization().RequirePermission("calls.use");

        g.MapGet("", async (IMeetingRoomService rooms, CancellationToken ct) => Results.Ok(await rooms.ListAsync(ct)));

        g.MapPost("/{key}/join", async (string key, HttpContext http, IPopDbContext db, IMeetingRoomService rooms,
            IMeetingRecordingService recs, CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            var r = await rooms.JoinAsync(me, key, ct);
            if (r.Join is not null) await recs.NoteJoinAsync(me, key, ct);
            return JoinResponse(r);
        });

        g.MapPost("/{key}/recordings", async (string key, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StartAsync(me, key, ct)) : Results.Unauthorized());

        g.MapPost("/recordings/{id:guid}/stop", async (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StopAsync(me, id, ct)) : Results.Unauthorized());

        g.MapGet("/recordings", async (HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await recs.ListAsync(me, false, ct)) : Results.Unauthorized());

        g.MapGet("/recordings/{id:guid}/file", async (Guid id, bool? download, HttpContext http, IPopDbContext db,
            IMeetingRecordingService recs, RecordingFileCache cache, CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            if (!await recs.CanViewAsync(me, http.User.IsInRole("Admin"), id, ct)) return Results.NotFound();
            var row = await db.MeetingRecordings.AsNoTracking().FirstAsync(r => r.Id == id, ct);
            if (await cache.OpenAsync(row, ct) is not { } path) return Results.NotFound();
            var name = $"{row.Title} {row.StartedAt.ToLocalTime():yyyy-MM-dd HHmm}.mp4".Replace('/', '-');
            return Results.File(path, "video/mp4", download == true ? name : null, enableRangeProcessing: true);
        });

        var a = app.MapGroup("/api/admin/meeting-rooms").RequireAuthorization("AdminOnly");

        a.MapGet("", async (IMeetingRoomService rooms, IMeetingRecordingService recs, ISettings settings, ILiveKitEgress egress,
            ILiveKitRoomAdmin liveKit, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await UserIdAsync(http, db, ct) ?? Guid.Empty;
            string status;
            if (!egress.IsConfigured) status = "not_configured";
            else { try { await liveKit.ListAsync("hall-robusta", ct); status = "online"; } catch { status = "offline"; } }
            return Results.Ok(new
            {
                video = await rooms.VideoAsync(ct),
                retentionDays = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct),
                egress = status,
                recordings = await recs.ListAsync(me, true, ct),
            });
        });

        a.MapPut("/{key}/video", async (string key, VideoRequest req, IMeetingRoomService rooms, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (!await db.HallRooms.AnyAsync(r => r.Key == key, ct)) return Results.NotFound();
            await rooms.SetVideoAsync(key, req.Allowed, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        a.MapPut("/retention", async (RetentionRequest req, ISettings settings, HttpContext http, CancellationToken ct) =>
        {
            if (req.Days is < 1 or > 3650) return Results.BadRequest(new { code = "invalid_days", message = "Use 1 to 3650 days." });
            await settings.SetAsync(SettingKeys.MeetingRoomsRetentionDays, req.Days, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        a.MapDelete("/recordings/{id:guid}", async (Guid id, IMeetingRecordingService recs, CancellationToken ct) =>
            await recs.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    public static IResult JoinResponse(MeetingRoomJoinResult r) => r.Error switch
    {
        null => Results.Ok(r.Join),
        "unknown_room" => Results.Json(new { code = r.Error, message = "That room does not exist." }, statusCode: 404),
        "room_full" => Results.Json(new { code = r.Error, message = "This room is full (20 people).", suggestions = r.Suggestions }, statusCode: 409),
        _ => Results.Json(new { code = "media_unavailable", message = "The meeting server is not reachable right now." }, statusCode: 503),
    };

    public static IResult RecordingResponse(MeetingRecordingResult r) => r.Error switch
    {
        null => Results.Ok(r.Recording),
        "unknown_room" or "not_found" => Results.Json(new { code = r.Error, message = "Not found." }, statusCode: 404),
        "not_in_room" => Results.Json(new { code = r.Error, message = "Join the room first." }, statusCode: 403),
        "already_recording" => Results.Json(new { code = r.Error, message = "This room is already being recorded to the server." }, statusCode: 409),
        _ => Results.Json(new { code = "egress_unavailable", message = "Server recording is not available right now. Try Record to this PC." }, statusCode: 503),
    };

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
```

- [ ] **Step 4: Reminder link** — in `MeetingReminderWorker.cs`, the meeting reminder's `targetUrl` (around line 114): load the meeting's `Location` with the row and put the room first:

```csharp
                targetUrl: IPop.Application.Hall.HallBooking.RoomKey(row.Meeting.Location) is { } roomKey
                    ? $"/calls?tab=rooms&join={roomKey}"
                    : await db.CalendarEvents.AsNoTracking() /* … existing expression unchanged … */,
```

Ensure `row.Meeting` includes `Location` (it is an entity navigation; confirm the query loads it).

- [ ] **Step 5: Map** — `app.MapMeetingRooms();` after `app.MapMeetings();` in Program.cs.

- [ ] **Step 6: Run tests** (`MeetingRoomEndpointTests`, reminder test, full suite), expect PASS.
- [ ] **Step 7: Commit** — `feat(rooms): meeting room and recording endpoints, admin switches, reminder opens the room`.

---

### Task 7: LiveKit + Egress infrastructure on 10.10.6.13 (dev)

**Files:**
- Modify: `deploy/livekit/docker-compose.yml`, `deploy/livekit/setup.ps1`, `deploy/livekit/README.md`
- Create: `deploy/livekit/egress.yaml.template`

Facts found while planning (2026-10-11): container `ipop-livekit` is **Exited since 2026-09-27** with restart policy `no`, so LiveKit group calls on prod are currently down. The API config points at `ws://10.10.6.13:7880` with key+secret set. Egress needs Redis shared with the LiveKit server; use a dedicated `ipop-livekit-redis` (do not touch `ipop-redis`, the SignalR backplane).

- [ ] **Step 1: Ask the user before starting anything** — starting `ipop-livekit` again also restores prod group calls (it was down). Show: "LiveKit has been stopped since 27 Sep; I will start it again with Redis + Egress. OK?" Wait for OK.

- [ ] **Step 2: Compose** — `deploy/livekit/docker-compose.yml`

```yaml
services:
  redis:
    image: redis:7-alpine
    container_name: ipop-livekit-redis
    restart: unless-stopped
    command: redis-server --save "" --appendonly no
    ports:
      - "127.0.0.1:6380:6379"

  livekit:
    image: livekit/livekit-server:latest
    container_name: ipop-livekit
    restart: unless-stopped
    command: --config /livekit.yaml
    environment:
      LIVEKIT_KEYS: ${LIVEKIT_KEYS:?run setup.ps1 first}
    volumes:
      - ./livekit.yaml:/livekit.yaml:ro
    ports:
      - "7880:7880"
      - "7881:7881"
      - "3478:3478/udp"
      - "50000-50050:50000-50050/udp"
    depends_on: [redis]

  egress:
    image: livekit/egress:latest
    container_name: ipop-livekit-egress
    restart: unless-stopped
    cap_add: [SYS_ADMIN]
    environment:
      EGRESS_CONFIG_FILE: /egress.yaml
    volumes:
      - ./egress.yaml:/egress.yaml:ro
      - ${RECORDINGS_SPOOL:-F:/ipop-data/recordings-spool}:/out
    depends_on: [livekit, redis]
```

- [ ] **Step 3: Configs** — in `setup.ps1` add param `[string] $SpoolDir = "F:\ipop-data\recordings-spool"`, create it, append `RECORDINGS_SPOOL=$SpoolDir` to `.env`, add to the generated `livekit.yaml`:

```yaml
redis:
  address: redis:6379
```

and write `egress.yaml` from `egress.yaml.template`:

```yaml
api_key: __API_KEY__
api_secret: __API_SECRET__
ws_url: ws://livekit:7880
redis:
  address: redis:6379
health_port: 8080
log_level: info
template_base: https://recorder.livekit.io
```

(`setup.ps1` replaces `__API_KEY__`/`__API_SECRET__` with the values it already holds; `egress.yaml` and `.env` stay git-ignored — add them to `deploy/livekit/.gitignore`.) Keep the existing behaviour of reusing the secret in `.env`.

- [ ] **Step 4: Bring it up (after the user's OK)** — from `deploy/livekit` in an elevated PowerShell: `.\setup.ps1 -NodeIp 10.10.6.13 -PublicDomain ipop.santos.co.id -SkipFirewall`. Then verify:

```bash
curl -s -m 5 http://10.10.6.13:7880        # "OK"
docker logs --tail 20 ipop-livekit-egress   # "service ready" / no auth errors
```

Note: the setup regenerates `.env` only when missing, so the API's secret keeps working.

- [ ] **Step 5: README** — document the three containers, the spool folder, that the API must run on the same host (it reads the spool), and the `MeetingRooms:*` config keys.

- [ ] **Step 6: Commit** — `chore(livekit): Redis + Egress for meeting room recordings`.

---

### Task 8: Browser media module `meeting-rooms.js`

**Files:**
- Create: `src/IPop.Web/wwwroot/js/meeting-rooms.js`
- Modify: `src/IPop.Web/wwwroot/js/lazy.js` (wrap `ipopRooms.join` like `ipopMeeting.join`), `src/IPop.Web/wwwroot/index.html` (script tag next to `meeting-room.js`)
- Test: `tests/meeting-rooms-layout.test.cjs` (pure layout helper)

**Interfaces:**
- Produces `window.ipopRooms`:
  - `join(url, token, dotnetRef, opts) → Promise<bool>`; `opts = { stageId, stripId, inputDeviceId }`. Joins muted with camera off.
  - `leave()`, `setMic(on) → bool`, `setCamera(on) → bool`, `setShare(on, takeover) → "ok"|"busy"|"denied"`, `startPcRecording() → bool`, `stopPcRecording()`, `setRecordingAttr(value)`.
  - `.NET callbacks` on `dotnetRef`: `OnRoomState(string state)` ("connected"/"reconnecting"/"disconnected"), `OnRoomPeople(PeopleJson[])` where each item is `{ identity, name, speaking, micOn, camOn, sharing, recPc, isLocal }`, `OnRoomMetadata(string json)`, `OnRoomNotice(string code, string? name)` codes: `"video_off"`, `"video_on"`, `"share_taken"`, `"muted_by_server"`.
  - `module.exports.tileLayout(count, width, height) → { cols, rows, w, h }` for node tests (exported when `typeof module !== "undefined"`).

- [ ] **Step 1: Failing node test** — `tests/meeting-rooms-layout.test.cjs`

```js
const test = require('node:test');
const assert = require('node:assert');
const { tileLayout } = require('../src/IPop.Web/wwwroot/js/meeting-rooms.js');

test('one person fills the stage', () => assert.deepStrictEqual(tileLayout(1, 1280, 720), { cols: 1, rows: 1, w: 1280, h: 720 }));
test('four people make a 2x2 grid', () => assert.deepStrictEqual(tileLayout(4, 1280, 720), { cols: 2, rows: 2, w: 640, h: 360 }));
test('twenty people stay inside the canvas', () => {
  const l = tileLayout(20, 1280, 720);
  assert.ok(l.cols * l.rows >= 20);
  assert.ok(l.cols * l.w <= 1280 && l.rows * l.h <= 720);
});
```

Run: `node --test tests/meeting-rooms-layout.test.cjs` → FAIL (module missing).

- [ ] **Step 2: Implement** `src/IPop.Web/wwwroot/js/meeting-rooms.js`

```js
// Calls & Meetings → Meeting Rooms: LiveKit media for the five SJA Hall rooms (camera off on join, one screen
// share at a time, record to this PC). The .NET side (MeetingRoomState) owns the UI; this file owns tracks.
(function () {
  function tileLayout(count, width, height) {
    if (count <= 1) return { cols: 1, rows: 1, w: width, h: height };
    let best = null;
    for (let cols = 1; cols <= count; cols++) {
      const rows = Math.ceil(count / cols);
      const w = Math.floor(width / cols), h = Math.floor(height / rows);
      const tile = Math.min(w, h * 16 / 9);
      if (!best || tile > best.tile) best = { cols, rows, w, h, tile };
    }
    return { cols: best.cols, rows: best.rows, w: best.w, h: best.h };
  }
  if (typeof module !== 'undefined') { module.exports = { tileLayout }; return; }

  const LK = () => window.LivekitClient;
  let room = null, ref = null, opts = null, rec = null, peopleTimer = 0, lastVideoAllowed = null;

  function resolveUrl(url) {
    // Same rule as livekit-call.js: on https use the page host (the proxy forwards /rtc).
    if (location.protocol === 'https:' && url.startsWith('ws://')) return `wss://${location.host}`;
    return url;
  }

  function el(id) { return document.getElementById(id); }

  function tileFor(identity, name, isLocal) {
    const strip = el(opts.stripId); if (!strip) return null;
    let t = strip.querySelector(`[data-identity="${CSS.escape(identity)}"]`);
    if (!t) {
      t = document.createElement('div');
      t.className = 'mr-tile'; t.dataset.identity = identity;
      t.innerHTML = `<div class="mr-tile-av"></div><video autoplay playsinline muted></video><span class="mr-tile-nm"></span>`;
      t.querySelector('.mr-tile-av').textContent = (name || '?').split(' ').map(s => s[0]).join('').slice(0, 2).toUpperCase();
      t.querySelector('.mr-tile-nm').textContent = isLocal ? 'You' : name;
      strip.appendChild(t);
    }
    return t;
  }

  function render() {
    if (!room) return;
    const all = [room.localParticipant, ...room.remoteParticipants.values()];
    const strip = el(opts.stripId), stage = el(opts.stageId);
    if (!strip || !stage) return;
    const alive = new Set();
    let sharer = null;
    for (const p of all) {
      if (p.identity.startsWith('EG_')) continue;
      alive.add(p.identity);
      const t = tileFor(p.identity, p.name, p === room.localParticipant);
      const cam = p.getTrackPublication(LK().Track.Source.Camera);
      const v = t.querySelector('video');
      if (cam && cam.track && !cam.isMuted) { if (v.srcObject !== cam.track.mediaStream) cam.track.attach(v); t.classList.add('cam'); }
      else { t.classList.remove('cam'); }
      t.classList.toggle('speaking', p.isSpeaking);
      const share = p.getTrackPublication(LK().Track.Source.ScreenShare);
      if (share && share.track && !share.isMuted) sharer = { p, track: share.track };
    }
    strip.querySelectorAll('.mr-tile').forEach(t => { if (!alive.has(t.dataset.identity)) t.remove(); });
    const sv = stage.querySelector('video.mr-share');
    if (sharer) {
      stage.classList.add('sharing');
      if (sv && sv.srcObject !== sharer.track.mediaStream) sharer.track.attach(sv);
    } else {
      stage.classList.remove('sharing');
      if (sv) sv.srcObject = null;
    }
    stage.classList.toggle('grid', !sharer);
  }

  function pushPeople() {
    if (!room || !ref) return;
    const S = LK().Track.Source;
    const people = [room.localParticipant, ...room.remoteParticipants.values()]
      .filter(p => !p.identity.startsWith('EG_'))
      .map(p => ({
        identity: p.identity, name: p.name || '', speaking: p.isSpeaking,
        micOn: !!p.getTrackPublication(S.Microphone) && !p.getTrackPublication(S.Microphone).isMuted,
        camOn: !!p.getTrackPublication(S.Camera) && !p.getTrackPublication(S.Camera).isMuted,
        sharing: !!p.getTrackPublication(S.ScreenShare) && !p.getTrackPublication(S.ScreenShare).isMuted,
        recPc: (p.attributes || {}).rec === 'pc',
        isLocal: p === room.localParticipant,
      }));
    ref.invokeMethodAsync('OnRoomPeople', people).catch(() => {});
  }

  function changed() {
    render();
    clearTimeout(peopleTimer);
    peopleTimer = setTimeout(pushPeople, 150);
  }

  function canCamera() {
    const perm = room && room.localParticipant.permissions;
    const list = perm && perm.canPublishSources;
    return !list || list.length === 0 || list.includes(LK().TrackSource ? LK().TrackSource.CAMERA : 1) || list.includes('camera') || list.includes(1);
  }

  async function join(url, token, dotnetRef, o) {
    ref = dotnetRef; opts = o || {};
    const L = LK();
    room = new L.Room({
      adaptiveStream: true, dynacast: true,
      videoCaptureDefaults: { resolution: { width: 640, height: 360, frameRate: 15 } },
      publishDefaults: { videoEncoding: { maxBitrate: 450_000, maxFramerate: 15 }, screenShareEncoding: { maxBitrate: 1_500_000, maxFramerate: 10 } },
      audioCaptureDefaults: { deviceId: opts.inputDeviceId || undefined, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    });
    const E = L.RoomEvent;
    room.on(E.ParticipantConnected, changed).on(E.ParticipantDisconnected, changed)
      .on(E.TrackSubscribed, (track) => { if (track.kind === 'audio') { const a = track.attach(); a.dataset.mrAudio = '1'; document.body.appendChild(a); } changed(); })
      .on(E.TrackUnsubscribed, (track) => { track.detach().forEach(x => x.remove()); changed(); })
      .on(E.TrackMuted, changed).on(E.TrackUnmuted, changed)
      .on(E.LocalTrackPublished, changed).on(E.LocalTrackUnpublished, changed)
      .on(E.ActiveSpeakersChanged, changed)
      .on(E.ParticipantAttributesChanged, changed)
      .on(E.RoomMetadataChanged, (m) => ref.invokeMethodAsync('OnRoomMetadata', m || '{}').catch(() => {}))
      .on(E.ConnectionStateChanged, (s) => ref.invokeMethodAsync('OnRoomState', String(s)).catch(() => {}))
      .on(E.Disconnected, () => ref.invokeMethodAsync('OnRoomState', 'disconnected').catch(() => {}))
      .on(E.ParticipantPermissionsChanged, async (_prev, p) => {
        if (p !== room.localParticipant) return;
        const allowed = canCamera();
        if (lastVideoAllowed !== null && allowed !== lastVideoAllowed) {
          if (!allowed) await room.localParticipant.setCameraEnabled(false).catch(() => {});
          ref.invokeMethodAsync('OnRoomNotice', allowed ? 'video_on' : 'video_off', null).catch(() => {});
        }
        lastVideoAllowed = allowed; changed();
      })
      .on(E.DataReceived, async (payload, participant) => {
        try {
          const msg = JSON.parse(new TextDecoder().decode(payload));
          if (msg.t === 'share-takeover' && msg.target === room.localParticipant.identity) {
            await room.localParticipant.setScreenShareEnabled(false);
            ref.invokeMethodAsync('OnRoomNotice', 'share_taken', participant ? participant.name : null).catch(() => {});
            changed();
          }
        } catch { /* ignore */ }
      });
    try {
      await room.connect(resolveUrl(url), token, { autoSubscribe: true });
    } catch (e) {
      console.warn('meeting room connect failed', e); room = null; return false;
    }
    lastVideoAllowed = canCamera();
    await room.localParticipant.setMicrophoneEnabled(true).catch(() => {});
    await room.localParticipant.setMicrophoneEnabled(false).catch(() => {});   // publish then mute: unmute is instant later
    if (room.metadata) ref.invokeMethodAsync('OnRoomMetadata', room.metadata).catch(() => {});
    changed();
    return true;
  }

  async function leave() {
    if (rec) stopPcRecording();
    document.querySelectorAll('[data-mr-audio]').forEach(a => a.remove());
    if (room) { const r = room; room = null; await r.disconnect().catch(() => {}); }
    const strip = opts && el(opts.stripId); if (strip) strip.innerHTML = '';
  }

  async function setMic(on) { if (!room) return false; await room.localParticipant.setMicrophoneEnabled(on); changed(); return room.localParticipant.isMicrophoneEnabled; }

  async function setCamera(on) {
    if (!room) return false;
    if (on && !canCamera()) return false;
    try { await room.localParticipant.setCameraEnabled(on); } catch { return false; }
    changed(); return room.localParticipant.isCameraEnabled;
  }

  async function setShare(on, takeover) {
    if (!room) return 'denied';
    if (on) {
      const other = [...room.remoteParticipants.values()].find(p => {
        const s = p.getTrackPublication(LK().Track.Source.ScreenShare); return s && !s.isMuted;
      });
      if (other && !takeover) return 'busy';
      if (other) {
        const data = new TextEncoder().encode(JSON.stringify({ t: 'share-takeover', target: other.identity }));
        await room.localParticipant.publishData(data, { reliable: true, destinationIdentities: [other.identity] });
      }
    }
    try {
      await room.localParticipant.setScreenShareEnabled(on, { audio: false, resolution: { width: 1280, height: 720, frameRate: 10 } });
    } catch { return 'denied'; }   // user cancelled the picker
    changed(); return 'ok';
  }

  async function setRecordingAttr(value) {
    if (!room) return;
    try { await room.localParticipant.setAttributes({ rec: value || '' }); } catch { /* old server: indicator only from metadata */ }
  }

  // Record to this PC: draw the stage + tiles onto a canvas, mix every voice, and save a .webm when stopped.
  function startPcRecording() {
    if (!room || rec) return false;
    const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720;
    const g = canvas.getContext('2d');
    const ac = new AudioContext();
    const dest = ac.createMediaStreamDestination();
    const sources = new Map();
    const mix = () => {
      const tracks = [];
      for (const p of [room.localParticipant, ...room.remoteParticipants.values()]) {
        const m = p.getTrackPublication(LK().Track.Source.Microphone);
        if (m && m.track && m.track.mediaStreamTrack) tracks.push(m.track.mediaStreamTrack);
      }
      for (const t of tracks) if (!sources.has(t.id)) { const s = ac.createMediaStreamSource(new MediaStream([t])); s.connect(dest); sources.set(t.id, s); }
    };
    mix();
    const draw = () => {
      if (!rec) return;
      mix();
      g.fillStyle = '#141210'; g.fillRect(0, 0, 1280, 720);
      const stage = el(opts.stageId), share = stage && stage.querySelector('video.mr-share');
      const tiles = [...(el(opts.stripId)?.querySelectorAll('.mr-tile') || [])];
      let area = { x: 0, y: 0, w: 1280, h: 720 };
      if (share && share.srcObject && share.videoWidth) {
        g.drawImage(share, 0, 0, 1280, 600);
        area = { x: 0, y: 600, w: 1280, h: 120 };
      }
      const l = area.h === 720 ? tileLayout(tiles.length, area.w, area.h) : { cols: tiles.length || 1, rows: 1, w: Math.floor(area.w / Math.max(1, tiles.length)), h: area.h };
      tiles.forEach((t, i) => {
        const x = area.x + (i % l.cols) * l.w, y = area.y + Math.floor(i / l.cols) * l.h;
        const v = t.querySelector('video');
        g.fillStyle = '#24201d'; g.fillRect(x + 2, y + 2, l.w - 4, l.h - 4);
        if (t.classList.contains('cam') && v.videoWidth) g.drawImage(v, x + 2, y + 2, l.w - 4, l.h - 4);
        else { g.fillStyle = '#e7e5e4'; g.font = `${Math.max(14, Math.floor(l.h / 5))}px Inter, sans-serif`; g.textAlign = 'center'; g.fillText(t.querySelector('.mr-tile-av').textContent, x + l.w / 2, y + l.h / 2); }
        g.fillStyle = 'rgba(0,0,0,.55)'; g.fillRect(x + 6, y + l.h - 26, Math.min(l.w - 12, 160), 20);
        g.fillStyle = '#fff'; g.font = '12px Inter, sans-serif'; g.textAlign = 'left'; g.fillText(t.querySelector('.mr-tile-nm').textContent, x + 12, y + l.h - 12);
      });
      rec.raf = requestAnimationFrame(draw);
    };
    const stream = new MediaStream([...canvas.captureStream(15).getVideoTracks(), ...dest.stream.getAudioTracks()]);
    const mime = MediaRecorder.isTypeSupported('video/webm;codecs=vp9,opus') ? 'video/webm;codecs=vp9,opus' : 'video/webm';
    const mr = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 2_000_000 });
    const chunks = [];
    mr.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
    mr.onstop = () => {
      const blob = new Blob(chunks, { type: 'video/webm' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `${rec.name || 'meeting'} ${new Date().toISOString().slice(0, 16).replace('T', ' ').replace(':', '')}.webm`;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 60_000);
      ac.close().catch(() => {});
      rec = null;
    };
    rec = { mr, raf: 0, name: room.name.replace(/^hall-/, '') };
    mr.start(1000);
    draw();
    setRecordingAttr('pc');
    return true;
  }

  function stopPcRecording() {
    if (!rec) return;
    cancelAnimationFrame(rec.raf);
    rec.mr.stop();
    setRecordingAttr('');
  }

  window.ipopRooms = { join, leave, setMic, setCamera, setShare, startPcRecording, stopPcRecording, setRecordingAttr, tileLayout };
})();
```

Before relying on `canCamera()`, check livekit-client 2.22.3's `ParticipantPermission.canPublishSources` element type in `wwwroot/js/livekit-client.umd.js` (grep `canPublishSources`) — it is the protobuf `TrackSource` enum (numbers: CAMERA = 1). Simplify `canCamera()` to the form that matches and keep it a one-liner.

- [ ] **Step 3: lazy.js + index.html** — in `lazy.js` add `ipopRooms.join` to the wrapped functions the same way `ipopMeeting.join` is wrapped (loads `livekit-client.umd.js` first). In `index.html` add `<script src="js/meeting-rooms.js?v=20261011-rooms"></script>` after the `meeting-room.js` tag.

- [ ] **Step 4: Run node test** — `node --test tests/meeting-rooms-layout.test.cjs` → PASS.
- [ ] **Step 5: Commit** — `feat(rooms): browser media for meeting rooms (camera, share takeover, PC recording)`.

---

### Task 9: MeetingRoomState + overlay (in-room view and minimized pill)

**Files:**
- Create: `src/IPop.Web/Services/MeetingRoomState.cs`, `src/IPop.Web/Components/Calls/MeetingRoomOverlay.razor`, `src/IPop.Web/wwwroot/css/modules/meeting-rooms.css`
- Modify: `src/IPop.Web/Program.cs` (`builder.Services.AddScoped<IPop.Web.Services.MeetingRoomState>();`), `src/IPop.Web/Layout/MainLayout.razor` (mount `<IPop.Web.Components.Calls.MeetingRoomOverlay />` next to `GlobalCallOverlay`), `index.html` (css link next to `calls.css`)
- Test: `tests/IPop.Tests/MeetingRoomOverlayTests.cs` (bUnit)

**Interfaces:**
- Consumes: `/api/meeting-rooms/*` (Task 6), `window.ipopRooms` (Task 8), `CallState.HasLiveCall`.
- Produces `MeetingRoomState`:

```csharp
public sealed class MeetingRoomState
{
    public record Person(string Identity, string Name, bool Speaking, bool MicOn, bool CamOn, bool Sharing, bool RecPc, bool IsLocal);
    public record ServerRecording(Guid Id, string By, DateTime StartedAt);
    public MeetingRoomJoinDto? Room { get; }           // null = not in a room
    public bool Minimized { get; set; }
    public bool MicOn { get; } public bool CamOn { get; } public bool Sharing { get; }
    public bool RecordingPc { get; } public ServerRecording? RecordingServer { get; }
    public bool VideoAllowed { get; }
    public IReadOnlyList<Person> People { get; }
    public string ConnectionState { get; }
    public DateTime JoinedAt { get; }
    public event Action? Changed;
    public event Action<string, string?>? Notice;       // code, name
    /// null = joined; else an error code ("room_full" with suggestions, "in_call", "media_unavailable", "connect_failed").
    public Task<(string? Error, IReadOnlyList<string> Suggestions)> JoinAsync(string key);
    public Task LeaveAsync();
    public Task ToggleMicAsync(); public Task ToggleCameraAsync();
    /// "ok" | "busy" (someone shares — ask, then call with takeover: true) | "denied"
    public Task<string> ToggleShareAsync(bool takeover = false);
    public Task StartPcRecordingAsync(); public Task StopPcRecordingAsync();
    /// null = started; else error code from the API ("already_recording", "egress_unavailable", …)
    public Task<string?> StartServerRecordingAsync(); public Task StopServerRecordingAsync();
    [JSInvokable] OnRoomState(string), OnRoomPeople(Person[]), OnRoomMetadata(string), OnRoomNotice(string, string?)
}
```

- [ ] **Step 1: Failing bUnit test** — `tests/IPop.Tests/MeetingRoomOverlayTests.cs`. Follow the setup in `CallOverlayDragTests.Context(...)` (same services; add `MeetingRoomState`). Tests:

```csharp
    [Fact]
    public void Nothing_renders_when_not_in_a_room()
    {
        using var ctx = Context(out _);
        Assert.Empty(ctx.Render<MeetingRoomOverlay>().FindAll(".mr-meet, .mr-pill"));
    }

    [Fact]
    public void In_a_room_shows_controls_and_recording_menu_with_both_choices()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Contains("Arabika", cut.Find(".mr-top").TextContent);
        cut.Find("button.mr-rec").Click();
        var menu = cut.Find(".mr-menu").TextContent;
        Assert.Contains("Record to this PC", menu);
        Assert.Contains("Record to server", menu);
    }

    [Fact]
    public void Server_recording_shows_the_banner_with_who_started_it_and_greys_the_server_option()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, "me"));
        state.OnRoomMetadata("""{"recording":{"id":"7f1c0a4e-0000-4000-8000-000000000001","by":"Budi","startedAt":"2026-10-11T03:00:00Z"}}""");
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Contains("started by Budi", cut.Find(".mr-recbar").TextContent);
    }

    [Fact]
    public void Video_not_allowed_disables_the_camera_button()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", false, "me"));
        Assert.True(ctx.Render<MeetingRoomOverlay>().Find("button.mr-cam").HasAttribute("disabled"));
    }

    [Fact]
    public void Minimize_shows_the_pill_and_keeps_the_media_containers()
    {
        using var ctx = Context(out var state);
        state.SetForTest(new("kalosi", "Kalosi", "hall-kalosi", "wss://x", "t", true, "me"));
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-minimize").Click();
        Assert.NotNull(cut.Find(".mr-pill"));
        Assert.NotNull(cut.Find("#mr-strip"));               // still in the DOM so video keeps playing
        Assert.Contains("mr-hidden", cut.Find(".mr-meet").ClassName);
    }
```

`SetForTest` is an `internal` method on `MeetingRoomState` (add `[assembly: InternalsVisibleTo("IPop.Tests")]` if the Web project does not have it yet — check `IPop.Web.csproj`) that sets `Room` and `VideoAllowed` without JS.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: State service** — `src/IPop.Web/Services/MeetingRoomState.cs`

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using IPop.Application.MeetingRooms;
using Microsoft.JSInterop;

namespace IPop.Web.Services;

/// The meeting room the user is in (one at a time). Lives for the session so the meeting keeps running while
/// the user moves between modules; MeetingRoomOverlay draws it, js/meeting-rooms.js owns the media.
public sealed class MeetingRoomState(AuthState auth, IJSRuntime js, CallState calls) : IAsyncDisposable
{
    public record Person(string Identity, string Name, bool Speaking, bool MicOn, bool CamOn, bool Sharing, bool RecPc, bool IsLocal);
    public record ServerRecording(Guid Id, string By, DateTime StartedAt);

    private DotNetObjectReference<MeetingRoomState>? selfRef;

    public MeetingRoomJoinDto? Room { get; private set; }
    public bool Minimized { get; set; }
    public bool MicOn { get; private set; }
    public bool CamOn { get; private set; }
    public bool Sharing { get; private set; }
    public bool RecordingPc { get; private set; }
    public ServerRecording? RecordingServer { get; private set; }
    public bool VideoAllowed { get; private set; }
    public IReadOnlyList<Person> People { get; private set; } = [];
    public string ConnectionState { get; private set; } = "";
    public DateTime JoinedAt { get; private set; }

    public event Action? Changed;
    public event Action<string, string?>? Notice;

    private void Raise() => Changed?.Invoke();

    public async Task<(string? Error, IReadOnlyList<string> Suggestions)> JoinAsync(string key)
    {
        if (Room?.Key == key) { Minimized = false; Raise(); return (null, []); }
        if (calls.HasLiveCall) return ("in_call", []);
        if (Room is not null) await LeaveAsync();
        var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{key}/join");
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadFromJsonAsync<JsonElement>();
            var code = err.TryGetProperty("code", out var c) ? c.GetString() ?? "media_unavailable" : "media_unavailable";
            var sug = err.TryGetProperty("suggestions", out var s) ? s.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            return (code, sug);
        }
        var dto = (await res.Content.ReadFromJsonAsync<MeetingRoomJoinDto>())!;
        selfRef ??= DotNetObjectReference.Create(this);
        Room = dto; VideoAllowed = dto.VideoAllowed; Minimized = false; JoinedAt = DateTime.UtcNow;
        MicOn = CamOn = Sharing = RecordingPc = false; RecordingServer = null; People = [];
        Raise();
        var ok = await js.InvokeAsync<bool>("ipopRooms.join", dto.Url, dto.Token, selfRef,
            new { stageId = "mr-stage", stripId = "mr-strip", inputDeviceId = await auth.GetUserSettingAsync("call.inputDevice") });
        if (!ok) { Room = null; Raise(); return ("connect_failed", []); }
        return (null, []);
    }

    public async Task LeaveAsync()
    {
        if (Room is null) return;
        try { await js.InvokeVoidAsync("ipopRooms.leave"); } catch (JSException) { }
        Room = null; People = []; RecordingPc = false; RecordingServer = null;
        Raise();
    }

    public async Task ToggleMicAsync() { MicOn = await js.InvokeAsync<bool>("ipopRooms.setMic", !MicOn); Raise(); }

    public async Task ToggleCameraAsync()
    {
        if (!VideoAllowed) return;
        CamOn = await js.InvokeAsync<bool>("ipopRooms.setCamera", !CamOn); Raise();
    }

    public async Task<string> ToggleShareAsync(bool takeover = false)
    {
        var r = await js.InvokeAsync<string>("ipopRooms.setShare", !Sharing, takeover);
        if (r == "ok") Sharing = !Sharing;
        Raise();
        return r;
    }

    public async Task StartPcRecordingAsync() { RecordingPc = await js.InvokeAsync<bool>("ipopRooms.startPcRecording"); Raise(); }
    public async Task StopPcRecordingAsync() { await js.InvokeVoidAsync("ipopRooms.stopPcRecording"); RecordingPc = false; Raise(); }

    public async Task<string?> StartServerRecordingAsync()
    {
        if (Room is null) return "not_in_room";
        var res = await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/{Room.Key}/recordings");
        if (res.IsSuccessStatusCode) return null;
        var err = await res.Content.ReadFromJsonAsync<JsonElement>();
        return err.TryGetProperty("code", out var c) ? c.GetString() : "egress_unavailable";
    }

    public async Task StopServerRecordingAsync()
    {
        if (RecordingServer is null) return;
        await auth.SendAsync(HttpMethod.Post, $"/api/meeting-rooms/recordings/{RecordingServer.Id}/stop");
    }

    [JSInvokable] public void OnRoomState(string state) { ConnectionState = state; if (state == "disconnected" && Room is not null) { Room = null; People = []; } Raise(); }

    [JSInvokable]
    public void OnRoomPeople(Person[] people)
    {
        People = people;
        var me = people.FirstOrDefault(p => p.IsLocal);
        if (me is not null) { MicOn = me.MicOn; CamOn = me.CamOn; Sharing = me.Sharing; }
        Raise();
    }

    [JSInvokable]
    public void OnRoomMetadata(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement;
            RecordingServer = doc.TryGetProperty("recording", out var r)
                ? new ServerRecording(r.GetProperty("id").GetGuid(), r.GetProperty("by").GetString() ?? "", r.GetProperty("startedAt").GetDateTime())
                : null;
        }
        catch (JsonException) { RecordingServer = null; }
        Raise();
    }

    [JSInvokable]
    public void OnRoomNotice(string code, string? name)
    {
        if (code == "video_off") { VideoAllowed = false; CamOn = false; }
        if (code == "video_on") VideoAllowed = true;
        if (code == "share_taken") Sharing = false;
        Notice?.Invoke(code, name);
        Raise();
    }

    internal void SetForTest(MeetingRoomJoinDto room) { Room = room; VideoAllowed = room.VideoAllowed; JoinedAt = DateTime.UtcNow; Raise(); }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync();
        selfRef?.Dispose();
    }
}
```

Check `AuthState.GetUserSettingAsync` key used by the existing device test for the mic (grep `inputDevice` in `CallState.cs`) and use the same key. `MeetingRoomJoinDto` comes from `IPop.Application` (already referenced by Web).

- [ ] **Step 4: Overlay** — `src/IPop.Web/Components/Calls/MeetingRoomOverlay.razor`. Markup follows mockup screen 2/2b/2c (dark full view, top bar, stage + strip, people panel, controls row). Important structure (tests depend on these classes/ids):

```razor
@implements IDisposable
@inject MeetingRoomState Rooms
@inject ToastService Toasts

@if (Rooms.Room is { } room)
{
    <div class="mr-meet @(Rooms.Minimized ? "mr-hidden" : "")" role="dialog" aria-label="Meeting room @room.Name">
        <header class="mr-top">
            <span class="mr-swatch" style="background:@Swatch(room.Key)"></span><strong>@room.Name</strong>
            <span class="mr-sub">@Rooms.People.Count / 20 · @Elapsed</span>
            @if (Rooms.RecordingServer is { } srv)
            {
                <span class="mr-recbar">Recording to server · started by @srv.By</span>
            }
            else if (Rooms.People.FirstOrDefault(p => p.RecPc) is { } pc)
            {
                <span class="mr-recbar">Recording to PC · started by @(pc.IsLocal ? "you" : pc.Name)</span>
            }
            <button type="button" class="mr-tb mr-minimize" @onclick="() => { Rooms.Minimized = true; }"><i data-lucide="minimize-2"></i>Minimize</button>
        </header>
        <div class="mr-body">
            <section id="mr-stage" class="mr-stage grid">
                <video class="mr-share" autoplay playsinline muted></video>
                <div id="mr-strip" class="mr-strip"></div>
            </section>
            <aside class="mr-people">
                <h3><span>In this room</span><span>@Rooms.People.Count / 20</span></h3>
                @foreach (var p in Rooms.People)
                {
                    <div class="mr-person"><PersonAvatar Name="@p.Name" Size="30" Decorative="true" />@(p.IsLocal ? $"{p.Name} (you)" : p.Name)
                        <span class="mr-st">@if (p.Sharing) { <i data-lucide="monitor-up"></i> }<i data-lucide="@(p.MicOn ? "mic" : "mic-off")" class="@(p.MicOn ? "" : "off")"></i>@if (p.CamOn) { <i data-lucide="video"></i> }</span>
                    </div>
                }
            </aside>
        </div>
        <footer class="mr-controls">
            <button type="button" class="mr-cbtn @(Rooms.MicOn ? "" : "off")" @onclick="Rooms.ToggleMicAsync" aria-pressed="@(Rooms.MicOn ? "true" : "false")"><i data-lucide="@(Rooms.MicOn ? "mic" : "mic-off")"></i><small>@(Rooms.MicOn ? "Mute" : "Unmute")</small></button>
            <button type="button" class="mr-cbtn mr-cam @(Rooms.CamOn ? "on" : "")" disabled="@(!Rooms.VideoAllowed)" title="@(Rooms.VideoAllowed ? "" : "Video is turned off for this room by an admin")" @onclick="Rooms.ToggleCameraAsync"><i data-lucide="@(Rooms.CamOn ? "video" : "video-off")"></i><small>@(Rooms.VideoAllowed ? "Camera" : "Video off")</small></button>
            <button type="button" class="mr-cbtn @(Rooms.Sharing ? "on" : "")" @onclick="() => ShareAsync(false)"><i data-lucide="monitor-up"></i><small>@(Rooms.Sharing ? "Stop share" : "Share")</small></button>
            <button type="button" class="mr-cbtn mr-rec @(Recording ? "recording" : "")" @onclick="RecordClick"><i data-lucide="@(Recording ? "square" : "circle-dot")"></i><small>@(Recording ? "Stop" : "Record")</small></button>
            <span class="mr-sep"></span>
            <button type="button" class="mr-cbtn mr-leave" @onclick="Rooms.LeaveAsync"><i data-lucide="phone-off"></i>Leave</button>
            @if (menuOpen)
            {
                <div class="mr-menu" role="menu">
                    <div class="mr-mh">Record this meeting</div>
                    <button type="button" role="menuitem" @onclick="RecordPcAsync"><i data-lucide="hard-drive-download"></i><div><b>Record to this PC</b><span>Saves the whole meeting view + all voices as a .webm file to your Downloads when you stop.</span></div></button>
                    <button type="button" role="menuitem" disabled="@(Rooms.RecordingServer is not null)" @onclick="RecordServerAsync"><i data-lucide="server"></i><div><b>Record to server</b><span>@(Rooms.RecordingServer is null ? "MP4 720p stored on the NAS. Everyone in the room while it records can watch it in Calls & Meetings → Recordings." : "This room is already being recorded to the server.")</span></div></button>
                    <div class="mr-note"><i data-lucide="info"></i>Everyone in the room will see that you are recording.</div>
                </div>
            }
        </footer>
        @if (takeoverName is not null)
        {
            <div class="mr-scrim"><div class="mr-dlg">
                <h3>@takeoverName is sharing a screen</h3>
                <p>Only one screen can be shared at a time. Take over? @takeoverName's share will stop and they'll be told you took over.</p>
                <div class="mr-row"><button type="button" class="ghost-btn" @onclick="() => takeoverName = null">Cancel</button><button type="button" class="btn-primary" @onclick="() => ShareAsync(true)">Share my screen</button></div>
            </div></div>
        }
    </div>
    @if (Rooms.Minimized)
    {
        <button type="button" class="mr-pill" @onclick="() => { Rooms.Minimized = false; }">
            <span class="mr-swatch" style="background:@Swatch(room.Key)"></span>@room.Name · @Rooms.People.Count
            @if (Recording || Rooms.RecordingServer is not null) { <span class="mr-pill-rec">REC</span> }
            <i data-lucide="maximize-2"></i>
        </button>
    }
}

@code {
    private bool menuOpen;
    private string? takeoverName;
    private System.Threading.Timer? tick;

    private bool Recording => Rooms.RecordingPc || (Rooms.RecordingServer is not null && Rooms.People.Any(p => p.IsLocal));
    private string Elapsed => (DateTime.UtcNow - Rooms.JoinedAt).ToString(@"hh\:mm\:ss");

    public static string Swatch(string key) => key switch
    {
        "robusta" => "#c9a184", "arabika" => "#e2b98f", "kalosi" => "#a8cba3", "kapal-api" => "#eda58d", _ => "#a9b7e2",
    };

    protected override void OnInitialized()
    {
        Rooms.Changed += OnChanged;
        Rooms.Notice += OnNotice;
        tick = new(_ => { if (Rooms.Room is not null && !Rooms.Minimized) InvokeAsync(StateHasChanged); }, null, 1000, 1000);
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    private void OnNotice(string code, string? name) => _ = InvokeAsync(() =>
    {
        switch (code)
        {
            case "video_off": Toasts.Show($"Video was turned off for {Rooms.Room?.Name} by an admin. Audio and screen share still work."); break;
            case "video_on": Toasts.Show($"Video is allowed again in {Rooms.Room?.Name}."); break;
            case "share_taken": Toasts.Show($"{name ?? "Someone"} took over the screen share."); break;
        }
    });

    private async Task ShareAsync(bool takeover)
    {
        takeoverName = null;
        var r = await Rooms.ToggleShareAsync(takeover);
        if (r == "busy") takeoverName = Rooms.People.FirstOrDefault(p => p.Sharing && !p.IsLocal)?.Name ?? "Someone";
    }

    private async Task RecordClick()
    {
        if (Rooms.RecordingPc) { await Rooms.StopPcRecordingAsync(); return; }
        if (Rooms.RecordingServer is not null && Recording) { await Rooms.StopServerRecordingAsync(); return; }
        menuOpen = !menuOpen;
    }

    private async Task RecordPcAsync() { menuOpen = false; await Rooms.StartPcRecordingAsync(); }

    private async Task RecordServerAsync()
    {
        menuOpen = false;
        var err = await Rooms.StartServerRecordingAsync();
        if (err is not null)
            Toasts.Show(err switch
            {
                "already_recording" => "This room is already being recorded to the server.",
                "egress_unavailable" => "Server recording is not available right now. Try Record to this PC.",
                _ => "Could not start the recording.",
            });
    }

    public void Dispose()
    {
        Rooms.Changed -= OnChanged;
        Rooms.Notice -= OnNotice;
        tick?.Dispose();
    }
}
```

Check `ToastService`'s real method name (grep `public void` in `Services/ToastService.cs`) and use it. Lucide icons are re-created by the app's existing mechanism (see how `GlobalCallOverlay` triggers `lucide.createIcons` after render) — copy that `OnAfterRenderAsync` call.

Note: "Stop" on the Record button stops the server recording for anyone in the room (decision: anyone may record; stopping follows the same rule).

- [ ] **Step 5: CSS** — `src/IPop.Web/wwwroot/css/modules/meeting-rooms.css`: port the mockup's `.meet`, `.meet-top`, `.recbar`, `.stage`, `.share`, `.strip`, `.tile`, `.people`, `.controls`, `.cbtn`, `.menu`, `.scrim/.dlg` rules to the `mr-` prefixed names above, plus:

```css
.mr-meet{position:fixed;inset:0;z-index:900;background:#141210;color:#f5f5f4;display:grid;grid-template-rows:56px 1fr 96px}
.mr-meet.mr-hidden{visibility:hidden;pointer-events:none}   /* keep video elements alive while minimized */
.mr-stage{position:relative;display:grid;grid-template-rows:1fr 112px;gap:12px;padding:14px;min-height:0}
.mr-stage.grid{grid-template-rows:1fr}
.mr-stage.grid .mr-share{display:none}
.mr-stage.grid .mr-strip{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));grid-auto-rows:minmax(140px,1fr);overflow:auto}
.mr-share{width:100%;height:100%;object-fit:contain;background:#0b0a09;border-radius:14px}
.mr-strip{display:flex;gap:10px;overflow:auto}
.mr-tile{position:relative;flex:0 0 168px;border-radius:12px;background:#24201d;display:grid;place-items:center;border:2px solid transparent;overflow:hidden}
.mr-tile video{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;display:none}
.mr-tile.cam video{display:block}
.mr-tile.cam .mr-tile-av{display:none}
.mr-tile.speaking{border-color:#30d158}
.mr-tile-av{width:44px;height:44px;border-radius:12px;display:grid;place-items:center;font-weight:700;background:#3a3530}
.mr-tile-nm{position:absolute;left:8px;bottom:7px;font-size:11.5px;background:rgba(0,0,0,.55);padding:2px 7px;border-radius:6px;z-index:2}
.mr-pill{position:fixed;right:20px;bottom:20px;z-index:901;display:flex;gap:8px;align-items:center;padding:10px 14px;border-radius:999px;border:0;background:#1c1917;color:#fff;box-shadow:0 10px 30px rgba(0,0,0,.3)}
.mr-pill-rec{background:#e5484d;border-radius:999px;padding:1px 7px;font-size:10.5px;font-weight:700}
@media (max-width:767px){.mr-meet .mr-people{display:none}.mr-body{grid-template-columns:1fr}}
```

Link it in `index.html` next to the other module CSS.

- [ ] **Step 6: Run bUnit tests, expect PASS.**
- [ ] **Step 7: Commit** — `feat(rooms): in-room view with share takeover, record menu and minimized pill`.

---

### Task 10: Meeting Rooms tab, "Calls & Meetings" rename, deep links

**Files:**
- Create: `src/IPop.Web/Components/Calls/MeetingRoomsTab.razor`
- Modify: `src/IPop.Web/Pages/Calls.razor`, `src/IPop.Web/Layout/MainLayout.razor:386`, `src/IPop.Web/Pages/MeetingRoom.razor:12`, `src/IPop.Web/Pages/Calendar.razor:507`, `src/IPop.Web/Pages/Notifications.razor:524`, `src/IPop.Web/Components/NotificationSettingsDialog.razor:1038`, `src/IPop.Web/Services/TutorialRegistry.cs:154`
- Test: `tests/IPop.Tests/MeetingRoomsTabTests.cs` (bUnit)

**Interfaces:**
- Consumes: `GET /api/meeting-rooms`, `GET /api/meeting-rooms/recordings`, `/recordings/{id}/file`, `MeetingRoomState.JoinAsync`, `CalendarLinks.NewMeeting(..., location:)` (Task 11 adds the parameter — do Task 11 Step 3 first if building fails).
- Produces: `<MeetingRoomsTab JoinKey="@joinKey" RecordingId="@recordingId" />`; `/calls?tab=rooms`, `/calls?tab=rooms&join={key}`, `/calls?tab=rooms&recording={id}`.

- [ ] **Step 1: Failing bUnit tests** — `tests/IPop.Tests/MeetingRoomsTabTests.cs` using bUnit's mocked HTTP in the same way existing page tests mock `AuthState` calls (find a bUnit test that stubs API JSON — e.g. grep `MockHttp` or `HttpMessageHandler` in `tests/IPop.Tests/*Render*Tests.cs` — and copy its setup). Tests:
  1. Renders five cards in order Robusta…Toraja with "Empty" / "6 / 20" / "Full" states from a stubbed `/api/meeting-rooms` payload (Kapal Api with 20 people → button text "Room full", disabled).
  2. A room with `Recording` set shows the "Recording" pill; a person with `Sharing` shows "{name} is sharing a screen".
  3. `Available = false` cards show "Unavailable" and a disabled Join.
  4. Clicking Join on a full room (stub join → 409 with suggestions `["robusta","toraja"]`) opens the dialog titled "Kapal Api is full" listing Robusta and Toraja.
  5. Recordings section lists stubbed recordings; status `processing` shows "Processing…" and no play button; `ready` shows Play and Download linking `/api/meeting-rooms/recordings/{id}/file?download=1`.

- [ ] **Step 2: Run, expect FAIL.**

- [ ] **Step 3: Component** — `MeetingRoomsTab.razor` renders mockup screen 1/1b with these rules:
  - Load rooms on init and every 10 s while the tab is visible (`System.Threading.Timer`, disposed in `Dispose`), and again on `Rooms.Changed`.
  - Card band colours from the mockup (`--floor/--tint/--rink` per key: robusta `#ead8c6/#c9a184/#7a4f33`, arabika `#f4e3cf/#e2b98f/#9a6537`, kalosi `#dcebd8/#a8cba3/#46704f`, kapal-api `#f7ddd2/#eda58d/#a8492f`, toraja `#dfe4f4/#a9b7e2/#4a5b93`).
  - Pill: `Recording` if `Recording != null` or any person `RecordingToPc`; else `Full` at capacity; else `{n} in room`; else `Empty`; `Unavailable` when `!Available`.
  - Lines: "Now: {title} · until HH:mm" when `Now`, else "Next: {today HH:mm–HH:mm | tomorrow HH:mm | ddd d MMM HH:mm} · {title} · {organizer}" when `Next`, else "No bookings today"; "{name} is sharing a screen" when someone shares, else "Video allowed" / "Audio + screen share only".
  - Join: `Rooms.JoinAsync(key)`; on `room_full` show the dialog with suggestions (name, people count, "audio only" when video off, Join); on `in_call` toast "Leave your current call first."; on `media_unavailable`/`connect_failed` toast "The meeting server is not reachable right now.".
  - "Book" (calendar-plus) button → `Nav.NavigateTo(CalLinks.NewMeeting(location: HallBooking.Location(room.Name), from: "From Meeting Rooms"))`.
  - `JoinKey` parameter (deep link) → join once after first load. `RecordingId` → scroll that row into view and open the player.
  - Recordings: `GET /api/meeting-rooms/recordings`; row = thumb (play icon + duration), title + "{date} · recorded by {name} · {n} people", room dot + name, "{min} min · {MB} MB", actions. Play opens an inline `<video controls src="/api/meeting-rooms/recordings/{id}/file">` in a modal; the API needs the auth cookie/header — check how existing media (e.g. chat attachment previews) load protected files (grep `attachments/{` in `src/IPop.Web`) and use the same approach (likely a blob URL fetched through `AuthState`; for large video prefer the same signed-URL or cookie approach the app already uses for news videos — grep `NewsVideo` in Web).
  - Empty recordings state: "No recordings yet. Server recordings you attend appear here."

- [ ] **Step 4: Calls.razor**
  - `<PageTitle>Calls & Meetings — IPop</PageTitle>`; `Layout.SetHeader("Calls & Meetings", "Direct calls · meeting rooms · history");` line 71 `<span>Calls &amp; Meetings</span>`.
  - Third tab after Scheduled: `<button class="@(tab == "rooms" ? "active" : "")" @onclick='() => SetTab("rooms")'><i data-lucide="door-open"></i> Meeting Rooms <span>5</span></button>`.
  - Toolbar: when `tab == "rooms"` show "Book a room" (`CalLinks.NewMeeting(location: "SJA Hall · ", from: "From Meeting Rooms")`) instead of "Schedule".
  - Body: when `tab == "rooms"`, render `<MeetingRoomsTab JoinKey="@roomJoinKey" RecordingId="@roomRecordingId" />` in place of the split list/detail (favorites strip hidden on this tab).
  - `HandleDeeplinkAsync`: accept `tab=rooms`; read `join` and `recording` query values into `roomJoinKey`/`roomRecordingId`.
- [ ] **Step 5: Other labels** — MainLayout `new("Calls & Meetings", "phone", "/calls", BadgeKey: "calls")`; MeetingRoom.razor "← Back to Calls & Meetings"; Calendar.razor "Open in Calls & Meetings"; Notifications.razor `"meeting" => "Calls & Meetings"`; NotificationSettingsDialog `"Calls & Meetings"`; TutorialRegistry title "Calls & Meetings". Run the existing tests that assert these strings (`dotnet test --filter "FullyQualifiedName~Call|FullyQualifiedName~Tutorial|FullyQualifiedName~Notification"`) and update their expected text only where it is the label.
- [ ] **Step 6: Run tests, expect PASS** (new + full suite).
- [ ] **Step 7: Commit** — `feat(rooms): Meeting Rooms tab and "Calls & Meetings" rename`.

---

### Task 11: Calendar — "Join room" and booking a room from the form

**Files:**
- Modify: `src/IPop.Web/Services/CalendarLinks.cs`, `src/IPop.Web/Pages/Calendar.Compose.cs`, `src/IPop.Web/Pages/Calendar.razor`
- Test: `tests/IPop.Tests/CalendarLinksTests.cs` (exists? grep; else create), `tests/IPop.Tests/MeetingRoomCalendarTests.cs` (bUnit detail)

**Interfaces:**
- Produces: `CalendarLinks.NewMeeting(..., string? location = null)` adds `location=` (escaped, ≤300 chars); Calendar reads `[SupplyParameterFromQuery(Name = "location")] LocationQuery` → `form.Online = false; form.Location = LocationQuery`.

- [ ] **Step 1: Failing tests**
  - `CalendarLinks.NewMeeting(location: "SJA Hall · Kalosi")` contains `location=SJA%20Hall%20%C2%B7%20Kalosi`.
  - Calendar detail for a meeting with `Location = "SJA Hall · Kalosi"` that is not cancelled renders `<a class="cal-btn primary" href="/calls?tab=rooms&join=kalosi">Join room</a>`; a meeting at "Lantai 3" does not.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: CalendarLinks** — add parameter `string? location = null` (last) and `if (!string.IsNullOrWhiteSpace(location)) q.Add("location=" + Uri.EscapeDataString(Trim(location, 300)));`.
- [ ] **Step 4: Compose** — add the query property and, inside `OpenFromComposeQueryAsync` after the title line: `if (!string.IsNullOrWhiteSpace(LocationQuery)) { form.Online = false; form.Location = LocationQuery.Trim(); }`.
- [ ] **Step 5: Detail** — in `Calendar.razor` after the online "Join meeting" block (≈ line 410):

```razor
                @if (detail.MeetingStatus != "cancelled" && IPop.Application.Hall.HallBooking.RoomKey(detail.Location) is { } roomKey)
                {
                    <div class="cal-detail-row"><i data-lucide="door-open"></i><a class="cal-btn primary" href="/calls?tab=rooms&join=@roomKey">Join room</a></div>
                }
```

Also make the "Room or location" input offer the five rooms: add `list="hall-rooms"` to the location input and a `<datalist id="hall-rooms">` with `SJA Hall · Robusta` … `SJA Hall · Toraja`. On save, a 409 `room_booked` must show the API message — check the form's existing error display handles `message` (it shows API messages for 422; confirm 409 is surfaced too, add if not).
- [ ] **Step 6: Run tests, expect PASS.**
- [ ] **Step 7: Commit** — `feat(rooms): Join room from Calendar and book a room from the meeting form`.

---

### Task 12: Administration → Meeting rooms

**Files:**
- Create: `src/IPop.Web/Components/Admin/AdminMeetingRooms.razor`
- Modify: `src/IPop.Web/Pages/Admin.razor` (section + case), `src/IPop.Web/wwwroot/css/modules/administration.css`
- Test: `tests/IPop.Tests/AdminMeetingRoomsTests.cs` (bUnit)

**Interfaces:**
- Consumes: `/api/admin/meeting-rooms` (GET, PUT `/{key}/video`, PUT `/retention`, DELETE `/recordings/{id}`), `GET /api/meeting-rooms` for live counts.

- [ ] **Step 1: Failing bUnit tests**
  1. Renders five switches with names in order and reflects `video` (kalosi off → switch `aria-checked="false"`; remember memory directory-aria-checked-bool-bug: render `"true"`/`"false"` strings explicitly).
  2. Clicking a switch PUTs `/api/admin/meeting-rooms/kalosi/video` with `{"allowed":true}`.
  3. Retention input saves on blur/Enter with PUT `/retention` `{"days":30}`; a value of 0 shows "Use 1 to 3650 days." and sends nothing.
  4. Egress status text: "Online" / "Offline" / "Not configured".
  5. Recordings list has a Delete button that asks for confirmation (ConfirmService) then DELETEs.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Component** — mockup screen 3: card "Video per room" (dot colour, name, live line "{n} people · {c} cameras on" from `/api/meeting-rooms` People `CamOn` is not in the DTO — use people count only: "{n} people" / "Empty"), switch per room; note strip "Cameras are capped at 360p · 15 fps. Room capacity is fixed at 20 people."; card "Server recordings" (Delete recordings after [n] days · Storage folder `/sja/SJA SPJ/ICT/recordings/{room}/{date}` · Recording service (Egress) status); card "All recordings" (list with Delete).
- [ ] **Step 4: Admin.razor** — add to `Groups` under Communication: `new("meeting-rooms", "Meeting rooms", "Video per room and server recordings", "door-open", "green")`, and `case "meeting-rooms": <AdminMeetingRooms />; break;`.
- [ ] **Step 5: Run tests, expect PASS.**
- [ ] **Step 6: Commit** — `feat(rooms): Administration → Meeting rooms (video per room, retention, recordings)`.

---

### Task 13: End-to-end verification on dev (copy DB, two browsers)

No production changes. Follow memory `ipop-two-browser-call-test` (demo 0001 + ipopadmin; mustChange rewritten in Playwright) and `dev-db-writes-allowed-for-tests` (use a throwaway copy DB).

- [ ] **Step 1:** Create copy DB `ipop_rooms_qa` from a fresh dump of prod (`pg_dump` → `createdb` → restore), point this worktree's `appsettings.Local.json` copy at it, set `MeetingRooms:SpoolDir`. Start this worktree's API + Web on free ports (never the prod ports). Migrations run at startup.
- [ ] **Step 2:** With LiveKit + Egress up (Task 7), in two Playwright browsers (https dev address, fake media flags `--use-fake-ui-for-media-stream --use-fake-device-for-media-stream`):
  1. Both join Kalosi from /calls → each sees 2/20 and the other's tile; rooms tab card shows "2 in room".
  2. A turns camera on → B sees A's video tile.
  3. Admin turns Kalosi video off → A's camera stops within 5 s, A sees the toast, camera button disabled; back on → enabled.
  4. A shares (fake screen via `getDisplayMedia` auto-accept flag `--auto-select-desktop-capture-source=Entire screen`), B clicks Share → takeover dialog → B shares, A sees "B took over".
  5. A records to PC for 10 s → a `.webm` download > 50 KB; B sees "Recording to PC · started by A".
  6. B records to server 30 s, stops → card shows Recording while running; within 2 min the row turns Ready, both get "Recording ready", playback seeks (HTTP 206), Download works; a third user (not present) gets 404 on the file URL.
  7. Full room: fill Robusta to 20 with a script that mints tokens via the API for 20 test users (or temporarily set capacity 2 in the copy DB) → 21st sees the "is full" dialog with suggestions.
  8. Minimize → navigate to /chat → pill visible, audio continues; click pill → back to the room.
  9. Calendar: book "SJA Hall · Toraja" for now → detail shows Join room → opens the room; second booking at the same time → "already booked".
- [ ] **Step 3:** Run `dotnet test tests/IPop.Tests/IPop.Tests.csproj` and `node --test tests/*.test.cjs`; record counts.
- [ ] **Step 4:** Stop only the processes this task started (by PID), drop the copy DB, clean the spool test files. Report results to the user with screenshots; deploy waits for "deploy ke prod".

---

## Known risks to tell the user

- **LiveKit is currently stopped** (exited 2026-09-27, restart policy none), so existing group calls on prod are down until it is started again; Task 7 asks before starting it.
- **Egress composite** uses LiveKit's hosted recorder template (`recorder.livekit.io`, reachable from the server today); if the internet is cut, server recordings fail ("Recording failed" notification) while PC recording still works.
- **sja-hall merge later**: its `AddSjaHall` migration also creates `HallRooms`; when merging, that migration must skip `HallRooms` (it will already exist).
- Egress needs ~2 CPU cores per active recording; with 5 rooms recording at once the server load is noticeable.
