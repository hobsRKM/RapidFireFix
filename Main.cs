using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace RapidFireFix;

public class RapidFireFix : BasePlugin
{
	public override string ModuleName => "Rapid Fire Fix";

	public override string ModuleVersion => "1.5.0";

	public override string ModuleAuthor => "jon";

	// ---- Vote configuration ----

	// How long after the first round of a map before the vote opens. This gives
	// players a moment to spawn in before they are asked.
	private const float VoteStartDelaySeconds = 10.0f;

	// How long the vote stays open for players to cast a vote. The vote can also
	// finish earlier, as soon as enough YES votes are in to pass.
	private const float VoteDurationSeconds = 60.0f;

	// A vote passes when the number of YES votes is at least this percentage of
	// the human players who were connected when the vote opened. 50 = half the
	// server must vote YES (so 6 players -> 3 YES, 4 players -> 2 YES). Players
	// who don't vote effectively count as NO, so at least this share of the whole
	// server has to actively want double tap.
	private const int RequiredYesPercentage = 50;

	// Minimum human players that must be connected to hold a vote at all. Below
	// this no vote is held (a message says so) and the fix stays on, so a lone
	// player can't enable double tap.
	private const int MinimumPlayers = 2;

	// When a message is announced it is repeated this many times (spaced by the
	// interval below) so players don't miss it.
	private const int VoteAnnounceRepeats = 5;
	private const float VoteAnnounceIntervalSeconds = 1.0f;

	// ---- Vote state ----

	// When true the rapid-fire ("double tap") fix is skipped for the current map,
	// i.e. players are allowed to rapid fire because the vote passed.
	private bool _doubleTapEnabled;

	// Whether a vote is currently open for players to cast a vote.
	private bool _voteInProgress;

	// SteamID -> vote (true = YES/enable, false = NO/keep disabled). Using a
	// dictionary guarantees each player is only counted once; their latest
	// choice overwrites any previous one.
	private readonly Dictionary<ulong, bool> _votes = new();

	// Number of human players connected when the current vote opened. The YES
	// threshold is calculated from this so it doesn't drift as players join/leave.
	private int _votePlayers;

	// Bumped on every map/vote cycle so that timers scheduled for a previous
	// map are ignored if a new map (and therefore a new cycle) has started.
	private int _currentVoteId;

	// The map we last armed a vote for, and whether that vote has been held yet.
	// The vote is triggered on the first round of each map, which is reliable
	// (players are spawned) unlike OnMapStart.
	private string? _currentMap;
	private bool _voteHeldThisMap;

	// Set once we've logged "fix skipped" after a passed vote, so that line is
	// printed a single time per enable instead of on every bullet impact.
	private bool _loggedEnabledSkip;

	private static readonly string Tag = $" {ChatColors.Green}[DT Vote]{ChatColors.Default}";

	public override void Load(bool hotReload)
	{
		Logger.LogInformation("RapidFireFix loaded (hotReload={HotReload}). A Double Tap vote runs ~{Delay}s after the first round of each map. Use css_dtvote to start one now, css_dtstatus to check state.", hotReload, (int)VoteStartDelaySeconds);

		// Handle the map we're already on right away. OnRoundStart drives votes on
		// later maps, but on load (or hot reload) the next round could be a while
		// off, so kick off a cycle for the current map now.
		_currentMap = Server.MapName;
		_voteHeldThisMap = true;
		BeginMapVoteCycle();
	}

	[GameEventHandler]
	public HookResult OnRoundStart(EventRoundStart evt, GameEventInfo info)
	{
		string map = Server.MapName;

		// New map -> arm a fresh vote for it.
		if (map != _currentMap)
		{
			_currentMap = map;
			_voteHeldThisMap = false;
		}

		// Hold the vote on the first round of the map only.
		if (!_voteHeldThisMap)
		{
			_voteHeldThisMap = true;
			Logger.LogInformation("First round on {Map} — scheduling Double Tap vote.", map);
			BeginMapVoteCycle();
		}

		return HookResult.Continue;
	}

	private void BeginMapVoteCycle()
	{
		// Safe default until the vote resolves: the fix is ON (double tap disabled).
		_doubleTapEnabled = false;
		_voteInProgress = false;
		_loggedEnabledSkip = false;
		_votes.Clear();

		int voteId = ++_currentVoteId;
		AddTimer(VoteStartDelaySeconds, () =>
		{
			if (voteId == _currentVoteId)
				StartVote(voteId);
		});
	}

	private void StartVote(int voteId, bool force = false)
	{
		if (_voteInProgress)
			return;

		int players = CountHumanPlayers();

		// Not enough players to hold a vote. Instead of staying silent, tell
		// players why there's no vote (repeated so it's visible) and keep the fix
		// on. A forced (test) vote skips this check.
		if (!force && players < MinimumPlayers)
		{
			Logger.LogInformation("Double Tap vote skipped: only {Players}/{Min} players connected.", players, MinimumPlayers);
			SpamMessage(voteId, false,
				$"{Tag} Need at least {ChatColors.Yellow}{MinimumPlayers}{ChatColors.Default} players to hold a Double Tap vote — only {ChatColors.Yellow}{players}{ChatColors.Default} online, so DT stays disabled.");
			return;
		}

		_voteInProgress = true;
		_votes.Clear();
		_votePlayers = players;

		int required = RequiredYesVotes(players);

		Logger.LogInformation("Double Tap vote opened (players={Players}, needYes={Required}, force={Force}).", players, required, force);

		// Spam the "vote is open" line a few times so nobody misses it.
		SpamMessage(voteId, true,
			$"{Tag} Vote to {ChatColors.Lime}ENABLE Double Tap{ChatColors.Default} (rapid fire): type {ChatColors.Yellow}!yes{ChatColors.Default} or {ChatColors.Yellow}!no{ChatColors.Default} ({ChatColors.Yellow}{(int)VoteDurationSeconds}s{ChatColors.Default}). Need {ChatColors.Yellow}{required}{ChatColors.Default} YES ({RequiredYesPercentage}% of {players}).");

		// A single reminder halfway through the vote window.
		AddTimer(VoteDurationSeconds / 2.0f, () =>
		{
			if (voteId == _currentVoteId && _voteInProgress)
				Server.PrintToChatAll($"{Tag} Double Tap vote still open — {ChatColors.Yellow}!yes{ChatColors.Default} / {ChatColors.Yellow}!no{ChatColors.Default}.");
		});

		// Close the vote once the window elapses (if it hasn't finished early).
		AddTimer(VoteDurationSeconds, () =>
		{
			if (voteId == _currentVoteId)
				EndVote();
		});
	}

	// Prints a chat line immediately and then repeats it a few times (spaced by
	// VoteAnnounceIntervalSeconds) so players don't miss it. When requireVoteOpen
	// is true the repeats stop if the vote is no longer running; either way they
	// stop once a new map/vote cycle begins.
	private void SpamMessage(int voteId, bool requireVoteOpen, string message)
	{
		Server.PrintToChatAll(message);

		for (int i = 1; i < VoteAnnounceRepeats; i++)
		{
			AddTimer(i * VoteAnnounceIntervalSeconds, () =>
			{
				if (voteId != _currentVoteId)
					return;

				if (requireVoteOpen && !_voteInProgress)
					return;

				Server.PrintToChatAll(message);
			});
		}
	}

	private void EndVote()
	{
		if (!_voteInProgress)
			return;

		_voteInProgress = false;

		int yes = _votes.Values.Count(v => v);
		int no = _votes.Values.Count(v => !v);
		int required = RequiredYesVotes(_votePlayers);

		// Pass when YES reaches the required share of the players who were online
		// when the vote opened. Non-voters count as NO.
		bool passed = yes >= required;

		_doubleTapEnabled = passed;
		_loggedEnabledSkip = false;

		Logger.LogInformation("Double Tap vote ended: YES={Yes} NO={No} required={Required} players={Players} passed={Passed}.", yes, no, required, _votePlayers, passed);

		if (passed)
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Lime}Vote PASSED{ChatColors.Default} (YES {yes} / NO {no}, needed {required} of {_votePlayers}). Double Tap {ChatColors.Lime}ENABLED{ChatColors.Default} — rapid fire fix is OFF this map.");
		}
		else
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Red}Vote FAILED{ChatColors.Default} (YES {yes} / NO {no}, needed {required} of {_votePlayers}). DT is disabled.");
		}
	}

	// Number of YES votes needed to pass: RequiredYesPercentage% of the players
	// (rounded up), but always at least 1.
	private static int RequiredYesVotes(int players)
	{
		return Math.Max(1, (int)Math.Ceiling(players * RequiredYesPercentage / 100.0));
	}

	private static int CountHumanPlayers()
	{
		List<CCSPlayerController> players;
		try
		{
			players = Utilities.GetPlayers();
		}
		catch
		{
			return 0;
		}

		int count = 0;
		foreach (CCSPlayerController p in players)
		{
			try
			{
				if (p is { IsValid: true, IsBot: false, IsHLTV: false })
					count++;
			}
			catch
			{
				// Player left / is in a transient state — just skip them.
			}
		}

		return count;
	}

	[ConsoleCommand("css_dtvote", "Force-start a Double Tap vote now (for testing)")]
	public void OnForceVoteCommand(CCSPlayerController? player, CommandInfo info)
	{
		Logger.LogInformation("css_dtvote used — force-starting a Double Tap vote.");
		info.ReplyToCommand($"{Tag} Force-starting a Double Tap vote...");

		_currentMap = Server.MapName;
		_voteHeldThisMap = true;
		_doubleTapEnabled = false;
		_loggedEnabledSkip = false;
		_voteInProgress = false;
		_votes.Clear();

		int voteId = ++_currentVoteId;
		StartVote(voteId, force: true);
	}

	[ConsoleCommand("css_dtstatus", "Show whether Double Tap is currently enabled")]
	public void OnStatusCommand(CCSPlayerController? player, CommandInfo info)
	{
		string state = _doubleTapEnabled
			? $"{ChatColors.Lime}ENABLED{ChatColors.Default} — rapid-fire fix is OFF this map"
			: $"{ChatColors.Red}DISABLED{ChatColors.Default} — rapid-fire fix is ON";

		info.ReplyToCommand($"{Tag} Double Tap is {state}. Vote in progress: {(_voteInProgress ? "yes" : "no")}.");
		Logger.LogInformation("css_dtstatus: doubleTapEnabled={Enabled}, voteInProgress={InProgress}.", _doubleTapEnabled, _voteInProgress);
	}

	[ConsoleCommand("css_yes", "Vote YES to enable Double Tap (rapid fire) for this map")]
	[CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
	public void OnVoteYesCommand(CCSPlayerController? player, CommandInfo info) => CastVote(player, info, true);

	[ConsoleCommand("css_no", "Vote NO to keep Double Tap (rapid fire) disabled for this map")]
	[CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
	public void OnVoteNoCommand(CCSPlayerController? player, CommandInfo info) => CastVote(player, info, false);

	private void CastVote(CCSPlayerController? player, CommandInfo info, bool voteYes)
	{
		if (player == null || !player.IsValid)
			return;

		// Stay silent when no Double Tap vote is running so that other plugins
		// which also use !yes / !no aren't disrupted.
		if (!_voteInProgress)
			return;

		_votes[player.SteamID] = voteYes;

		string choice = voteYes ? $"{ChatColors.Lime}YES{ChatColors.Default}" : $"{ChatColors.Red}NO{ChatColors.Default}";
		info.ReplyToCommand($"{Tag} Your vote ({choice}) has been recorded.");

		// Finish the vote as soon as enough YES votes are in — no need to wait out
		// the rest of the timer.
		if (voteYes && _votes.Values.Count(v => v) >= RequiredYesVotes(_votePlayers))
			EndVote();
	}

	[GameEventHandler]
	public HookResult OnBulletImpact(EventBulletImpact evt, GameEventInfo info)
	{
		// Vote passed -> double tap is allowed this map, so skip the rapid-fire
		// fix entirely and let the weapon fire at whatever rate the client asks.
		if (_doubleTapEnabled)
		{
			if (!_loggedEnabledSkip)
			{
				_loggedEnabledSkip = true;
				Logger.LogInformation("Double Tap ENABLED — skipping the rapid-fire fix, so rapid fire is allowed this map.");
			}

			return HookResult.Continue;
		}

		if (evt.Userid?.Pawn?.Value?.WeaponServices?.ActiveWeapon?.Value == null)
			return HookResult.Continue;

		CBasePlayerWeapon firedWeapon = evt.Userid.Pawn.Value.WeaponServices.ActiveWeapon.Value!;

		CCSWeaponBaseVData? weaponData = firedWeapon.GetVData<CCSWeaponBaseVData>();

		if (weaponData == null)
			return HookResult.Continue;

		int tickBase = (int)evt.Userid.TickBase;

		int fixedPrimaryTick = (int)Math.Round(weaponData.CycleTime.Values[0] * 64) - 3;
		firedWeapon.NextPrimaryAttackTick = Math.Max(firedWeapon.NextPrimaryAttackTick, tickBase + fixedPrimaryTick);

		// R8 force fix
		if (firedWeapon.DesignerName == "weapon_revolver")
		{
			int fixedSecondaryTick = (int)Math.Round(weaponData.CycleTime.Values[1] * 64) - 3;
			firedWeapon.NextSecondaryAttackTick = Math.Max(firedWeapon.NextSecondaryAttackTick, tickBase + fixedSecondaryTick);
		}

		return HookResult.Continue;
	}
}
