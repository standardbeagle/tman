Output assets/demo.gif

Set Shell bash
Set FontSize 15
Set Width 1100
Set Height 650
Set Theme "Catppuccin Mocha"
Set TypingSpeed 40ms
Set WindowBar Colorful

Hide
Type "export PATH=$PWD/bin/Release/net10.0/linux-x64/publish:$PATH"
Enter
Type "export TMAN_HOME=/tmp/tman-vhs-home"
Enter
Type "bash assets/demo-setup.sh && cd /tmp/tman-vhs-proj"
Enter
Sleep 1s
Type "clear"
Enter
Show

Type "# adopt tman in any project"
Enter
Sleep 400ms
Type "tman init --shims"
Enter
Sleep 2500ms

Type "# existing invocations are now supervised"
Enter
Sleep 400ms
Type "./test"
Enter
Sleep 3500ms

Type "# a hanging test gets killed at the wall-clock limit"
Enter
Sleep 400ms
Type "tman run --name hang --max-time 4s -- npm run hang"
Enter
Sleep 9s
Type "echo exit=$?"
Enter
Sleep 1200ms

Type "# duplicate runs are refused"
Enter
Sleep 400ms
Type "tman run --name dupe -- sleep 300 &"
Enter
Sleep 2500ms
Type "tman run --name dupe -- sleep 1"
Enter
Sleep 2s

Type "tman list --all"
Enter
Sleep 2500ms

Type "tman kill all"
Enter
Sleep 2s
Hide
Type "clear"
Enter
Show
