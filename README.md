# Rapid Fire Fix

Fixes the CS2 rapid fire ("double tap") exploit by forcing every shot to respect
the weapon's real cycle time, so weapons can't be fired faster than intended.

The plugin requires no configuration and starts working as soon as it's loaded.

## Double Tap vote

At the start of every map a vote is opened asking players whether double tap
(rapid fire) should be enabled for that map:

- Players vote with `!yes` or `!no` in chat.
- The vote opens `10s` after the map starts (so players have time to connect)
  and stays open for `30s`. The "vote is open" message is repeated a few times
  when it opens so nobody misses it.
- The vote **passes** when the number of **YES** votes is at least **50% of the
  players** who were connected when the vote opened (rounded up) — so 6 players
  need 3 YES, 4 players need 2. Players who don't vote count as NO, so half the
  server has to actively want it. This stops a single player from enabling it.
- A vote is only held when at least **2 players** are connected; below that a
  message says so and DT stays disabled.
- If it passes, the rapid-fire fix is skipped for the rest of that map, so double
  tap is allowed.
- Otherwise the vote **fails** and the fix keeps running as normal —
  `vote failed, DT is disabled`.

You can also force a vote immediately for testing with `!dtvote` (chat) or
`css_dtvote` (server console), and the plugin logs each step to the server
console.

The result applies until the next map starts, when a fresh vote is held.

Behaviour is controlled by constants at the top of `Main.cs`:
`VoteStartDelaySeconds`, `VoteDurationSeconds`, `RequiredYesPercentage` (share of
players that must vote YES), `MinimumPlayers` (needed to hold a vote), and
`VoteAnnounceRepeats` / `VoteAnnounceIntervalSeconds` (how often messages repeat).
