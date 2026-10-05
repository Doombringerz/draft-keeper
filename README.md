# Draft Keeper

Keeps unsent text in the Claude Code chat box from being lost when the machine crashes.

Editors keep what you type in a chat box in memory only. A power cut or a stop error
takes it with them. Draft Keeper saves that text to disk as you write. After the crash
it is still there.

## What it works with

The chat box of the Claude Code extension, on Windows. Tested in Visual Studio Code.
Editors built on VS Code that run the same extension (VS Code Insiders, Cursor,
VSCodium, Windsurf) are watched too, but untested. Other chat extensions are not
supported.

## Download

Get the zip from Releases and run `DraftKeeper.exe`. No installer, and no .NET runtime
needed. The executable is not signed, so SmartScreen warns the first time it runs. Each
release lists the SHA-256 of the executable to check against.

## What it watches

The chat box in each open editor window, found by its accessibility name. Every window
is covered, so two workspaces open at once are both watched.

Every few seconds it searches each window's accessibility tree for the chat box. Between
searches it holds the box and reads the text only when Windows reports a change.

Each draft is labelled with its chat. VS Code gives every chat tab its own view, named
after the tab, with the box inside it. The label is read from that view. Where a box has
no such view around it, the label comes from the selected editor tab, then the window
title. When a box empties, it asks whether that exact text now shows in the
chat. Nothing else in the window is read.

It does not install a keyboard hook and it does not read the screen.

Set `DRAFTKEEPER_EXTRA_PROCESSES` to a comma separated list of process names to watch
another editor that runs the extension.

## Passwords

Windows reports the value of a password field as bullet characters. A password box
cannot be captured this way.

That protection does not extend to secrets typed into ordinary text boxes. An API key
pasted into a chat box is ordinary text and will be saved. Use the pause option in the
tray menu before typing anything you do not want on disk, and keep retention short.

## Storage

Saved to `%LOCALAPPDATA%\DraftKeeper\drafts.dat`, encrypted against your Windows
account. Another account on the same machine cannot read it, and the file is useless
on another machine.

A box being typed into owns one row, overwritten in place as you write. When the buffer
is replaced, by switching to another chat, that row becomes history. Each chat keeps its
three most recent finished drafts and no more.

Opening a chat puts its unsent text back in the box. When that text is already saved,
the saved row carries on with its original time and no copy is made. A finished draft
whose whole text is inside another draft of the same chat is folded into that one.

A message you send is dropped. Sending and clearing the box look the same from outside,
so before dropping anything it checks whether that text now shows in the conversation.
Opening a chat runs the same check on the drafts saved for it. Text it cannot confirm
was sent is kept.

Drafts expire on a timer, a day by default, adjustable from the tray menu to one, six
or twelve hours, or one, three or seven days. "Delete everything saved" wipes the store
immediately.

Line breaks are kept exactly as typed. Select an entry to read it in full.

There is no network code in this program. Nothing is uploaded, and there is no
telemetry.

## Using it

Run `DraftKeeper.exe`. It sits in the notification area. Double click the icon, or pick
"Show drafts", to see what has been saved. Select an entry to read it in full, then
copy it back. The box at the top filters by chat name or text, and the divider between
the list and the preview can be dragged. Minimising the window sends it back to the
notification area.

The tray tooltip reports how many boxes are being watched.

It starts with Windows by default, through the per-user run key. After a crash nothing is
saved until it runs again. "Start with Windows" in the tray menu turns that off.

## Appearance

The tray menu has System, Dark and Light. System follows what Windows is set to. The
choice is remembered, along with retention and the clipboard setting, in
`settings.json` next to the store.

## Build

Requires the .NET 9 SDK on Windows.

```
dotnet build src\DraftKeeper\DraftKeeper.csproj -c Release
```

The executable lands in `src\DraftKeeper\bin\Release\net9.0-windows\`.

To rebuild the icon after changing the artwork:

```
pwsh tools\make-icon.ps1 -Source src\DraftKeeper\Resources\draft-keeper.png `
                         -Destination src\DraftKeeper\Resources\draft-keeper.ico
```

Frames below 32 pixels are cropped tighter. A full figure scaled to 16 pixels is a
smudge, and an icon file is allowed to carry different artwork per size.

## Tests

```
pwsh tests\run.ps1
```

Builds everything into a temporary folder, then runs `tests\TestHost` while a copy of the
program watches it. The test host is a window with a text box under the same name the
program looks for. It types, edits, switches chats, rebuilds its input box, sends one
message and clears another. Then it shows chats the way VS Code does, each in its own
view named after its tab, and reopens them. The script also seeds the store with copies
an older version left behind. Afterwards it reads the store back and checks what was
kept: labels, trimming, copies, line breaks, placeholders, and sent versus cleared.

It takes about two and a half minutes. The test copy keeps its data in its own temporary
folder, set through `DRAFTKEEPER_DATA`. A copy you already have running keeps running
and keeps its drafts.

## Clipboard images

A screenshot pasted into a chat box is not part of the box's text, so text capture
cannot see it. There is a separate option for that: "Save clipboard images" in the tray
menu.

It is off until you switch it on. While it is off nothing looks at the clipboard at
all. While it is on, only image formats are read. Clipboard text is never touched.

Images are encrypted the same way drafts are, kept in the Images tab, capped at 20 and
expire on the same timer. "Save as PNG" writes one out where you choose.

## If it is not saving

Set `DRAFTKEEPER_LOG` to a file path and restart it. The watcher then records what it
found, when a box was replaced, and what it decided about each change. It logs
decisions and character counts, not the text of your drafts.

## Limits

- Text capture is text only. A pasted image or an attached file is not part of the
  box's text value. Switch on clipboard images to cover screenshots.
- Only chats on screen are watched. A chat in a hidden tab is not visible to Windows. Its text was saved when you left it, and anything that changes while it is
  hidden is picked up when you open it again.
- Windows only. It uses a Windows accessibility interface that has no counterpart
  elsewhere.
- The editor only builds its accessibility tree when something asks for it. Asking
  for the box makes the editor build it, which costs the editor a little.
- A crash in the middle of a sentence can still lose the last second or two. Capture
  is event driven with a poll as a backstop.
- The label attached to each draft comes from the chat's own view, then the selected
  editor tab, then the window title. When none gives a usable name the draft is filed
  under "unknown".

## Licence

MIT, artwork included. See LICENSE.
