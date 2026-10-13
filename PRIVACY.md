# Privacy policy

*Last updated: 13 October 2026*

FontA is a free, open-source font manager for Windows. This page explains what it does with your information.
The short version: **FontA keeps everything on your PC. It has no accounts, no ads and no telemetry, and it never
uploads your fonts, your documents or anything about how you use it.**

## What FontA stores on your PC

All of it stays in your own Windows profile, in `%LOCALAPPDATA%\FontA`, and nothing in it is sent anywhere:

| What | Why |
|---|---|
| The font index (`library.db`): the names, styles and file locations of fonts in the folders you add, plus your favorites, tags, collections, remembered folders and settings | So the library opens instantly and remembers your organisation |
| Thumbnails (`thumbs\`) | Cached font previews. Safe to delete at any time |
| `active-fonts.txt` | Which fonts FontA switched on, so it can switch them off again after a crash |
| `crash.log` | Written only if FontA hits an unexpected error. It stays on your PC; attach it to a bug report only if you choose to |
| Font usage dates | When you last used a font, so the optional "deactivate unused fonts" feature works |

Your font files are never changed. FontA only copies one if you click **Install**, and then only into your own
per-user fonts folder.

## Optional feature: fonts your documents use

If you switch on **Count fonts used in my documents** (it is off by default), FontA reads the Illustrator, InDesign,
Photoshop, After Effects and PDF files in the folders you choose, and optionally the shortcuts in Windows' *Recent*
folder, to find out which of your fonts they use. This all happens on your PC:

- It looks only for font names and compares them with the fonts in your library.
- It does not keep, copy or upload the content of your documents. What it remembers is each document's file path, its
  saved date and size, and which of your fonts it names, in `library.db`.
- You can turn it off, remove the folders, or delete `library.db` whenever you like.

## What FontA sends over the internet

FontA makes **one kind of network request: checking for updates.** About once every few hours (and when you press
*Check for updates*) it asks `api.github.com` whether a newer release of FontA exists, and downloads the update from
GitHub when there is one. Like any web request, this lets GitHub see your IP address and the fact that your copy of
FontA asked for its releases. FontA adds nothing else: no identifier, no list of your fonts, no usage data. GitHub's
own handling of that request is described in [GitHub's privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

You can switch this off in **Settings**: turn off *Update automatically*. FontA then makes no network requests unless
you press *Check for updates*. Updates are verified with a checksum before they are installed.

FontA has no sign-in, does not contact any server run by its author, and has no advertising, analytics or tracking.

## Things FontA changes on your PC, only when you ask

- **Activate** switches a font on for the current Windows session only. It is switched off when you quit (unless you
  marked it *Always*).
- **Install** copies a font into your per-user fonts folder and registers it for your Windows account.
- **Start on system start** adds FontA to your account's list of startup programs.

FontA never needs administrator rights and never changes anything for other users of the PC.

## Windows and your other software

Windows itself may send crash information to Microsoft if FontA ever crashes and you have Windows Error Reporting
turned on. That is a Windows setting, separate from FontA, and FontA does not control it.

## Removing your data

Uninstall FontA from *Settings > Apps*. To remove everything it stored, delete the folder `%LOCALAPPDATA%\FontA`
afterwards. Nothing about you is kept anywhere else, so there is nothing to ask for or request from anyone.

## Children

FontA collects no personal information from anyone, including children.

## Changes to this policy

If FontA ever changes what it does with information, this page will change first and the change will appear in the
release notes. The history of every edit is public in this repository.

## Questions

Open an issue at <https://github.com/GusRabo/FontA/issues>. For a security problem, use the private route described in
[SECURITY.md](SECURITY.md).
