# Integrated terminal

Harness includes a focused terminal module for commands that a developer chooses
to run directly. Open it from the command-strip terminal icon or press
<kbd>Ctrl</kbd>+<kbd>`</kbd>.

## Behavior

- The shell starts in the active workspace and closes when its terminal module
  closes. Switching workspaces closes the old workspace's terminal.
- Windows uses the operating system's Windows PowerShell installation. macOS and
  Linux use `SHELL`, with `/bin/sh` as the fallback. Harness does not resolve a
  shell from another AI harness or desktop application.
- Commands run as the signed-in operating-system user. They are direct user
  input, so agent approval modes do not apply. Commands requested by a model
  continue through Harness's normal approval and sandbox path.
- Output is selectable and copyable. ANSI presentation sequences are removed so
  redirected output remains readable in the native Avalonia surface.
- Output delivery is coalesced off the UI thread. Visible history and pending
  output are bounded; if a command produces data faster than the UI can safely
  present it, Harness reports that older output was skipped.
- Up and down recall up to 100 commands for the current terminal. `Ctrl+L` clears
  visible output without restarting the shell.
- Restart shell terminates the owned process tree and starts a clean shell in the
  same workspace.

## Deliberate limits

Terminal transcripts and command history are not written to Harness storage or
portable backups because they may contain secrets. This first native terminal is
line-oriented; full-screen TUI programs that require a pseudoterminal are not
emulated. Long-running line-oriented development commands still stream output,
and Restart shell provides an explicit stop path for the owned process tree.
