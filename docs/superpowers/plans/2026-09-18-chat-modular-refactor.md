# Modular Refactor Plan for IPop Chat Module

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the entire IPop Chat module across backend service, API endpoints, and frontend components by feature/responsibility to make it modular, maintainable, performant, and clean, with 0 regressions and 100% test pass.

**Architecture:**
- **Backend Service Layer (`IPop.Infrastructure/Chat`)**: Split the monolithic 1,900-line `ChatService.cs` into focused domain partial classes (`ChatService.Rooms.cs`, `ChatService.Messages.cs`, `ChatService.Threads.cs`, `ChatService.ReactionsAndPins.cs`, `ChatService.Receipts.cs`, `ChatService.Settings.cs`).
- **Backend Endpoints Layer (`IPop.Api/Endpoints`)**: Refactor `ChatEndpoints.cs` into modular extension groups (`ChatRoomEndpoints`, `ChatMessageEndpoints`, `ChatAttachmentEndpoints`, `ChatMemberEndpoints`).
- **Frontend Blazor Layer (`IPop.Web/Components/Chat` & `Pages/Chat.razor`)**: Decompose the 1,200-line `Chat.razor` into isolated reusable components:
  - `ChatRoomPane.razor` (Sidebar conversations, Direct/Groups tabs, Pinned/Recent/Archived)
  - `ChatHeader.razor` (Active room header, presence, call/pinned/media drawer triggers)
  - `ChatStickyPinnedBar.razor` (Sticky pinned announcement bar)
  - `ChatMessageFeed.razor` (Messages container, date separators, typing indicator, unread divider)
  - `ChatComposerBar.razor` (Message input, attachments strip, emoji/sticker, poll, voice note)
  - `ChatDrawerContainer.razor` (Right-side drawer: Overview, Members, Media, Pinned)
  - `ChatForwardDialog.razor` (Forward modal with search)

**Tech Stack:** ASP.NET Core (.NET 10), EF Core, SignalR, Blazor WebAssembly.

---

### Task 1: Refactor `ChatService.cs` into Modular Partial Classes

**Files:**
- Modify: `src/IPop.Infrastructure/Chat/ChatService.cs` (base fields & helpers)
- Create: `src/IPop.Infrastructure/Chat/ChatService.Rooms.cs`
- Create: `src/IPop.Infrastructure/Chat/ChatService.Messages.cs`
- Create: `src/IPop.Infrastructure/Chat/ChatService.Threads.cs`
- Create: `src/IPop.Infrastructure/Chat/ChatService.ReactionsAndPins.cs`
- Create: `src/IPop.Infrastructure/Chat/ChatService.Receipts.cs`
- Create: `src/IPop.Infrastructure/Chat/ChatService.Settings.cs`

- [ ] **Step 1: Extract Room and Group Member operations to `ChatService.Rooms.cs`**
- [ ] **Step 2: Extract Messaging, Edit, Delete, Forward, Search to `ChatService.Messages.cs`**
- [ ] **Step 3: Extract Thread operations to `ChatService.Threads.cs`**
- [ ] **Step 4: Extract Reactions and Pinning to `ChatService.ReactionsAndPins.cs`**
- [ ] **Step 5: Extract Delivery, Read Receipts, and Seen tracking to `ChatService.Receipts.cs`**
- [ ] **Step 6: Extract Mute, Archive, and User Room Settings to `ChatService.Settings.cs`**
- [ ] **Step 7: Verify compilation and tests**
Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter "FullyQualifiedName~Chat" -o tests/IPop.Tests/bin/TestRun`
Expected: PASS (100% tests pass).

---

### Task 2: Refactor `ChatEndpoints.cs` into Modular Route Mappers

**Files:**
- Modify: `src/IPop.Api/Endpoints/ChatEndpoints.cs`
- Create: `src/IPop.Api/Endpoints/Chat/ChatRoomEndpoints.cs`
- Create: `src/IPop.Api/Endpoints/Chat/ChatMessageEndpoints.cs`
- Create: `src/IPop.Api/Endpoints/Chat/ChatAttachmentEndpoints.cs`

- [ ] **Step 1: Modularize route groups under `ChatEndpoints`**
- [ ] **Step 2: Verify API project compilation**
Run: `dotnet build src/IPop.Api/IPop.Api.csproj`
Expected: 0 errors.

---

### Task 3: Extract Blazor Page Sub-Components in `IPop.Web`

**Files:**
- Create: `src/IPop.Web/Components/Chat/ChatRoomPane.razor`
- Create: `src/IPop.Web/Components/Chat/ChatHeader.razor`
- Create: `src/IPop.Web/Components/Chat/ChatStickyPinnedBar.razor`
- Create: `src/IPop.Web/Components/Chat/ChatComposerBar.razor`
- Create: `src/IPop.Web/Components/Chat/ChatForwardDialog.razor`
- Modify: `src/IPop.Web/Pages/Chat.razor` (orchestrates clean top-level subcomponents)

- [ ] **Step 1: Build `ChatRoomPane.razor`**
- [ ] **Step 2: Build `ChatHeader.razor` and `ChatStickyPinnedBar.razor`**
- [ ] **Step 3: Build `ChatComposerBar.razor` and `ChatForwardDialog.razor`**
- [ ] **Step 4: Streamline `Chat.razor`**
- [ ] **Step 5: Verify Web compilation**
Run: `dotnet build src/IPop.Web/IPop.Web.csproj`
Expected: 0 errors.

---

### Task 4: Full Verification & Test Suite Pass

- [ ] **Step 1: Run complete backend and frontend test suite**
Run: `dotnet test tests/IPop.Tests/IPop.Tests.csproj -o tests/IPop.Tests/bin/TestRun`
Expected: 313 Passed, 0 Failed.
- [ ] **Step 2: Git status and commit**
Commit the clean, modular architecture.
