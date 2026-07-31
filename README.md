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
- At least **4 votes** must be cast for the vote to count, and a simple majority
  of them must be **YES** for it to pass — so out of 4 votes you need **3 YES**
  (a 2-2 tie fails). This stops a single player from enabling double tap alone.
  A vote is only started if at least 4 human players are connected.
- If it passes, the rapid-fire fix is skipped for the rest of that map, so double
  tap is allowed.
- Otherwise (too few votes, majority NO, or a tie) the vote **fails** and the fix
  keeps running as normal — `vote failed, DT is disabled`.

The result applies until the next map starts, when a fresh vote is held.

Behaviour is controlled by constants at the top of `Main.cs`:
`VoteStartDelaySeconds`, `VoteDurationSeconds`, `VotePassPercentage` (majority
threshold), `MinimumVotes` (turnout required), and `VoteAnnounceRepeats` /
`VoteAnnounceIntervalSeconds` (how often the open message repeats).
