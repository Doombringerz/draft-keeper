# Security

This program reads text out of another application's window. This page says what it
reads, what it does not, and where the text ends up.

## What it reads

The chat box in each editor window, found through the Windows accessibility interface.
Every few seconds it searches each window's accessibility tree for the box. Between
searches it holds the box and reads its text only when Windows reports a change.

To label a draft with its chat it reads a name: that of the view the box sits in, or of
the selected editor tab, or the window title. Only the name is read.

When the box empties, it asks whether that exact text now appears in that chat's view,
or in the window when there is no view. That is how a sent message is told apart from a
cleared one. Opening a chat asks the same about the drafts already saved for it. The
answer is yes or no on text it already holds; nothing new is read out of the
conversation.

## What it does not do

- No keyboard hook. Nothing is intercepted on its way to an application.
- No screen reading, no screenshots of the desktop, no window capture.
- No reading of other controls. The box, the view and the tab are matched by type and
  name; nothing else found along the way has its text read.
- No network code. There is no HTTP client, no socket, no telemetry, no update check.

## Passwords

Windows reports the value of a password field as bullet characters. A password box
cannot be read this way.

That protection does not extend to a secret typed into an ordinary text box. An API key
pasted into a chat box is ordinary text and will be saved. Pause capture from the tray
menu before typing something that should not reach disk, and keep retention short.

## Where it is stored

`%LOCALAPPDATA%\DraftKeeper\drafts.dat`, or the folder named in `DRAFTKEEPER_DATA`,
encrypted with the Windows data protection interface against the signed-in account.
Another account on the same machine cannot read it and the file is useless if copied
elsewhere.

Drafts expire on a timer and "Delete everything saved" wipes the store.

`settings.json` beside it is plain text and holds preferences only.

## Optional clipboard capture

Off unless switched on. While off, nothing looks at the clipboard at all. While on,
only image formats are read; clipboard text is never touched. Images are encrypted the
same way and expire on the same timer.

## Diagnostics

Setting `DRAFTKEEPER_LOG` to a file path records what the watcher decided. The log holds
ids, chat names, ages, character counts and reasons. It never holds
draft text. The store is encrypted and the log is not.

## Reporting a problem

Open an issue. If it is something you would rather not post publicly, say so in the
issue without the details and a private channel can be arranged.

There is no funded security programme behind this. It is a single-person project and
the response to a report is whatever one person can do.
