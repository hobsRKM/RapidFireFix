# Rapid Fire Fix

Fixes the CS2 rapid fire ("double tap") exploit by forcing every shot to respect
the weapon's real cycle time, so weapons can't be fired faster than intended.

The plugin requires no configuration and starts working as soon as it's loaded.

## Double Tap vote

At the start of every map a vote is opened asking players whether double tap
(rapid fire) should be enabled for that map:

- Players vote with `!yes` or `!no` in chat.
- The vote opens `10s` after the map starts (so players have time to connect)
  and stays open for `30s`.
- If a simple majority of the cast votes are **YES**, the vote **passes**: the
  rapid-fire fix is skipped for the rest of that map, so double tap is allowed.
- Otherwise (majority NO, a tie, or no votes) the vote **fails** and the fix
  keeps running as normal — `vote failed, DT is disabled`.

The result applies until the next map starts, when a fresh vote is held.

The vote window, start delay and pass percentage are defined as constants at the
top of `Main.cs` (`VoteStartDelaySeconds`, `VoteDurationSeconds`,
`VotePassPercentage`).
