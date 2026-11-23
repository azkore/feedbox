# feedbox

feedbox is a Windows tray app that saves feeds to a local SQLite database. It starts with the New listing from your chosen subreddits. Open `http://127.0.0.1:51883/` to review posts, with text and image previews when Reddit includes them in its listing.

Run `build.ps1` from Windows PowerShell to build and test the app on 64-bit Windows. The build downloads the Microsoft WebView2 SDK files and uses the WebView2 runtime installed on the computer. Run `build\feedbox.exe` with the DLLs beside it.

1. Open feedbox from the notification area and choose **Open Reddit to sign in**. Sign into Reddit there, then click **Hide and check**.
2. Choose **Open inbox**, use **Add source** in the sidebar, and click **Check now**. **Check status** shows the result for each source.
3. Leave the app running in the tray to check feeds. **Pause collection** and **Resume collection** apply to all feeds. Open Reddit again if the app reports a sign-in problem.

Use **J** to select a post, then press **J** again to mark that post read and move forward. **K** moves back, **Shift+J/K** switches feeds, and **V** opens the selected original in a new tab. Opening a post does not mark it read.

Drag the sidebar's right edge to resize it. The browser remembers its width. The **Database** menu can delete read articles across all feeds and compact the SQLite file. The app keeps the deleted IDs so those articles don't return as unread.

The first check imports a recent starting batch, up to two pages. Subsequent checks look for posts from the last completed check before moving the catch-up point forward. A check that reaches its page budget resumes on the next run. If it reaches the end of Reddit's listing without enough overlap, the source status reports a possible gap. Posts already collected stay in the database.

Run `feedbox.exe` without arguments. It uses `feedbox.sqlite` and `profile` beside the executable. To override the defaults, edit `feedbox.conf` in that same folder. The keys are `database`, `profile`, and `port`. Relative paths start in the app folder. The inbox listens only on `127.0.0.1`, on port `51883` by default.

Use a native Windows SQLite tool to inspect the live database. A read-only connection from Windows worked while the app was running; a WSL connection through `/mnt/c` did not.
