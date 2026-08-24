# Bid Team Chat

## Context

`ProposalPage.razor` already has a **Bid Team Chat** nav item, but it renders a
placeholder: *"Group chat with the bid team is not built yet."* The existing **AI Chat**
tab is a person↔model Q&A — there is nowhere for the bid team to talk to *each other*
about a proposal, so that conversation lives in email and Teams, detached from the
material it is about.

This builds the human group chat: one thread per proposal, everyone on the bid team
posts and reads, `@mentions` resolve against team members and render in bold. The email
notification a mention should trigger is **deliberately out of scope for now** — the
mention is persisted as its own row so that feature is a service change later, not a
migration.

Two constraints from the request drive the design:

- **Readers get every feature.** Unlike AI Chat (where a Reader may not post into a
  shared chat), `ProposalRole.Reader` is enough for everything here.
- **A fixed four-colour palette** distinguishes speakers, own messages on the right.

Note: another agent is working on a billing feature concurrently. This change touches no
pricing, usage or extraction code. The one shared file is
`SagaDbContextModelSnapshot.cs`, which any new migration regenerates — see *Risks*.

## Decisions already settled

| Question | Decision |
|---|---|
| Live updates | In-memory singleton notifier; open circuits reload on post |
| Colours | Own = Cream `#f7f4e9` (right); teammates cycle Blue `#d6d7da` / Green `#e0e2df` / Rose `#f9f5f5` by team position, so a given person looks the same to everyone |
| Nav badge | Yes — unread count, mirroring the `ChatSeen` watermark pattern |

## Design

### Mentions resolve server-side, indices are stored

The composer's picker is a convenience, not the source of truth: a mention typed by hand
(`@Emil Larsen`) must work identically. So the **server** re-scans the posted text against
the current bid team and writes what it found.

`MentionScanner` (pure, testable, no DB) walks the text; at each `@` preceded by
start-of-text or whitespace it takes the **longest** match among all candidates'
`DisplayName` and `Email`, case-insensitive, requiring the following character not to be
a letter or digit. Longest-match is what makes `@Emil` and `@Emil Larsen` both correct
when both are on the team; the trailing-boundary check is what stops `@Emilia` from
bolding `@Emil` and leaving `ia` behind.

The resolved `Start`/`Length` are **stored on the mention row**, so rendering is a pure
splice that never re-runs the scanner. A member removed from the team later keeps their
mention bolded in history, instead of silently un-bolding.

### Live updates without polling

Blazor Server already holds a circuit per open tab, so a singleton event is enough:
`TeamChatService.PostAsync` publishes the proposal id after `SaveChangesAsync`, and every
component subscribed for that proposal reloads. Single-process only, which is what this
app is. No timer, no per-tab database round-trips.

### No JavaScript

The project currently contains **zero** JS interop — `wwwroot/` holds only `app.css`.
Keeping it that way costs two small compromises, both deliberate:

- **Autoscroll** uses the CSS `flex-direction: column-reverse` pin instead of
  `scrollIntoView`. A reversed flex container with a single child starts scrolled to the
  bottom and stays anchored there as messages arrive.
- **Picker selection is click or Tab-then-Enter**, not arrow keys. Selecting with
  arrows/Enter requires a per-key `preventDefault`, which Blazor can only set statically
  at render time — a static `preventDefault` on `keydown` would block ordinary typing.
  Rendering suggestions as real `<button>` elements *after* the textarea gives
  Tab→Enter selection natively for free. Escape closes the picker (no default to
  suppress). Enter in the textarea always sends, matching `ChatSection.razor`.

## Changes

### 1. Domain — `src/Saga.Core/Domain/TeamChat.cs` (new)

Three entities, modelled on `Chat.cs`:

- `TeamMessage` — `Id`, `ProposalId`, `AuthorId` (non-nullable; users are never deleted
  in this app), `Text`, `CreatedAt`, `ICollection<TeamMessageMention> Mentions`.
- `TeamMessageMention` — `Id`, `TeamMessageId`, `UserId`, `Start`, `Length`.
- `TeamChatSeen` — `Id`, `ProposalId`, `UserId`, `LastSeenAt`. Per-proposal watermark
  (`ChatSeen` is per-chat; there is only one team thread).

Add `ICollection<TeamMessage> TeamMessages` to `Proposal.cs`.

### 2. `src/Saga.Core/Pipeline/MentionScanner.cs` (new)

Alongside the other pure text helpers (`ChatTitle`, `DocumentChunker`).

```csharp
public readonly record struct MentionMatch(Guid UserId, int Start, int Length);
public static class MentionScanner
{
    public static List<MentionMatch> Scan(string text, IReadOnlyList<TeamChatMember> candidates);
}
```

### 3. `src/Saga.Core/Models/TeamChatMember.cs` (new)

`record TeamChatMember(Guid UserId, string DisplayName, string Email, int ColourSlot)`
— feeds the picker, the scanner and the colour mapping. `ColourSlot` is the member's
index among the team ordered by `(AddedAt, UserId)`, mod 3.

### 4. `src/Saga.Infrastructure/Data/SagaDbContext.cs`

Three `DbSet`s plus `OnModelCreating` config, following the `ChatMessage`/`ChatSeen`
blocks directly above:

- `TeamMessage`: index `(ProposalId, CreatedAt)`; `Proposal` cascade; `Author`
  **`Restrict`** (a non-nullable author cannot take a second cascade path from `User`).
- `TeamMessageMention`: `TeamMessage` cascade; `User` `Restrict`.
- `TeamChatSeen`: unique index `(ProposalId, UserId)`; `Proposal` cascade; `User`
  cascade — the exact shape `ChatSeen` already proves SQL Server accepts.

`Text` stays `nvarchar(max)` (capped at 4000 chars in the service).

### 5. Migration

```
dotnet ef migrations add BidTeamChat -p src/Saga.Infrastructure -s src/Saga.Web
```

Applies automatically on startup in Development. Tests use `EnsureCreated` and do not
need it.

### 6. `src/Saga.Infrastructure/Services/TeamChatNotifier.cs` (new)

Singleton. `event Action<Guid>? Posted;` + `Publish(Guid proposalId)`.

### 7. `src/Saga.Infrastructure/Services/TeamChatService.cs` (new)

Constructed like `ChatService`: `IDbContextFactory<SagaDbContext>` + `TeamChatNotifier`.
Every method authorizes with the existing
`ProposalService.EnsureRoleAsync(db, proposalId, userId, ProposalRole.Reader, ct)` —
Reader everywhere, per the requirement.

- `MembersAsync` → `List<TeamChatMember>`, ordered `(AddedAt, UserId)`, assigning
  `ColourSlot`.
- `ListAsync` → messages with `Author` and `Mentions` included, ordered by `CreatedAt`.
- `PostAsync(proposalId, userId, text)` — trims, rejects empty, truncates > 4000; runs
  `MentionScanner.Scan` against `MembersAsync` **excluding the author**; inserts the
  message and one row per distinct match; moves the author's own watermark; saves; then
  `notifier.Publish(proposalId)`.
- `UnreadCountAsync` — messages with `CreatedAt > watermark && AuthorId != userId`.
- `MarkSeenAsync` — reuses the "skip the write when already caught up" shape from
  `ChatService.MarkSeenAsync`, so a component lifecycle method can call it per render
  for free.

### 8. `src/Saga.Web/Program.cs`

`AddScoped<TeamChatService>()` beside `ChatService` (line ~54);
`AddSingleton<TeamChatNotifier>()` in the singleton block.

### 9. `src/Saga.Web/TeamMessageText.cs` (new)

Static helper beside `Markdown.cs`, same `MarkupString` shape:

```csharp
public static MarkupString Render(string text, IEnumerable<TeamMessageMention> mentions, Guid meId)
```

HTML-encodes every plain segment, then splices
`<strong class="team-mention">` (or `team-mention team-mention-me` when the mention is
the viewer) over each stored `Start`/`Length`. **Not** markdown — team chat is plain
text, and `white-space: pre-wrap` on the bubble keeps newlines, as `ChatSection` does for
user messages.

### 10. `src/Saga.Web/Components/Proposal/BidTeamChatSection.razor` (new)

`@implements IDisposable`, injects `TeamChatService`, `CurrentUserService`,
`TeamChatNotifier`.

- Layout reuses the `.chat-pane` grid (`auto / 1fr / auto`) so the composer pins to the
  bottom.
- Each message: bubble with author name + `HH:mm` meta; class
  `team-msg-mine` when `AuthorId == me`, else `team-msg-1|2|3` from the author's
  `ColourSlot`.
- Composer: `<textarea @bind="_draft" @bind:event="oninput" @onkeydown="OnKeyDown">` +
  Send button, disabled while empty.
- Picker: computed from the tail of `_draft` — the last `@` at index 0 or preceded by
  whitespace, with no whitespace after it and ≤ 40 chars. Filters members
  (`DisplayName`/`Email` contains, case-insensitive, self excluded), caps at 6, renders
  as `<button>`s. Selecting replaces the `@query` run with `@DisplayName ` — appending at
  the end is exactly where the caret already is, so no caret restore is needed.
- `OnInitialized` subscribes to `TeamChatNotifier.Posted`; the handler filters on
  `ProposalId`, then `InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); })`,
  guarded by a `_disposed` flag as `ChatSection` does. `Dispose` unsubscribes.
- Calls `MarkSeenAsync` on load and after each reload, then raises
  `OnUnreadChanged`.

### 11. `src/Saga.Web/Components/Pages/ProposalPage.razor`

- Replace the placeholder block with
  `<BidTeamChatSection ProposalId="ProposalId" OnUnreadChanged="RefreshTeamUnreadAsync" />`.
- Add `Section.BidTeamChat` to the `workspace-main` `flush` condition, next to
  `Section.Chat`.
- `TeamBadge()` beside the existing `ChatBadge()`, reusing `nav-flag nav-flag-count`;
  `_unreadTeam` loaded in `LoadAsync`.
- Subscribe to `TeamChatNotifier.Posted` here too, so the badge goes live even when you
  are on another tab; unsubscribe in the existing `Dispose`.

### 12. `src/Saga.Web/wwwroot/app.css`

New `/* ---------- Bid team chat ---------- */` section after the existing chat blocks:

- `.team-scroll { display:flex; flex-direction:column-reverse; overflow-y:auto; min-height:0; padding: var(--saga-gutter); }` with one `.team-thread` child.
- `.team-msg` (shared bubble), `.team-msg-mine` (`align-self:flex-end`, `#f7f4e9`),
  `.team-msg-1` `#d6d7da`, `.team-msg-2` `#e0e2df`, `.team-msg-3` `#f9f5f5`, all
  left-aligned, `white-space: pre-wrap`.
- `.team-mention` (`font-weight:600`), `.team-mention-me` (tinted so being mentioned is
  noticeable).
- `.team-mention-picker` — absolutely positioned above the composer, bordered, using the
  existing `.menu-panel` visual language.

### 13. Tests

`tests/Saga.Tests/MentionScannerTests.cs` — pure, no fixture:

- `@Emil` and `@Emil Larsen` both resolve, longest wins when both are candidates
- email form `@sda@mannaz.com` resolves
- `@Emilia` does **not** match candidate `Emil` (trailing-letter boundary)
- `a@b` mid-word is not a mention (no whitespace before `@`)
- `@nobody` (not on the team) yields nothing
- the same person mentioned twice yields two matches with distinct offsets

`tests/Saga.Tests/TeamChatTests.cs` — `IClassFixture<LocalDbFixture>`, set up via
`ProposalService`/`UserService` as `ChatServiceTests` does:

- a Reader can post and read (the explicit requirement)
- a non-member gets `UnauthorizedAccessException`
- posting writes mention rows only for bid team members
- a mention of someone *not* on the team writes no row
- unread count excludes your own messages; `MarkSeenAsync` clears it
- `MembersAsync` gives stable colour slots across calls

## Verification

1. `dotnet build Saga.slnx`
2. `dotnet test tests/Saga.Tests` (needs LocalDB — `sqllocaldb info`)
3. Run the app via the **run-saga** skill (`http://localhost:5033`), which auto-signs in
   as `elv@mannaz.com`. Set `Ai:UseFakeAi: true` first if anything nearby would otherwise
   hit Azure — this feature makes no AI calls, but the tab sits beside ones that do.
4. Open a proposal → **Bid Team Chat**. Post a message; confirm it lands right-aligned in
   cream.
5. Add `sda@mannaz.com` as a **Reader** on the **Bid Team** tab. Sign in as them in a
   second browser profile (see the *Running a second dev user* memory) and confirm they
   can post — proving the Reader requirement — and that their message appears left-aligned
   in a different palette colour.
6. With both windows open side by side, post from one and confirm the other updates
   **without a refresh**, and that the nav badge increments on the window sitting on a
   different tab.
7. Type `@e` and confirm the picker lists matching members; click one and confirm the
   full name is inserted and renders **bold** after sending. Then type `@Emil Larsen` by
   hand without the picker and confirm it bolds identically.
8. Post > 20 messages and confirm the thread is scrolled to the bottom on open and stays
   pinned as new ones arrive.

## Risks

- **Snapshot conflict.** `dotnet ef migrations add` rewrites
  `SagaDbContextModelSnapshot.cs`. The tree already carries an uncommitted
  `20260824181725_DocumentTypeNamesPlural`, and the concurrent billing work may add its
  own migration. Generate this migration *last*, and if the snapshot conflicts, re-run
  the `add` on top of the other branch's snapshot rather than merging it by hand.
- **Notifier is single-process.** If Saga is ever scaled out, live push silently degrades
  to per-instance. Acceptable now; the fix is a backplane, not a redesign.
