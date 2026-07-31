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

	public override string ModuleVersion => "1.2.2";

	public override string ModuleAuthor => "jon";

	// ---- Vote configuration ----

	// How long after a map starts before the vote opens. This gives players
	// time to (re)connect and spawn after the map change before they are asked.
	private const float VoteStartDelaySeconds = 10.0f;

	// How long the vote stays open for players to cast a vote.
	private const float VoteDurationSeconds = 30.0f;

	// Percentage of the *cast* votes that must be YES for the vote to pass.
	// 50 = simple majority (strictly more YES than NO). So out of 4 votes you
	// need 3 YES; a 2-2 tie fails and the rapid-fire fix stays on.
	private const int VotePassPercentage = 50;

	// Minimum number of votes that must be cast for the vote to count. Anything
	// below this always fails, so a single player can't enable double tap on
	// their own. It's also the minimum number of human players that must be
	// connected before a vote is started at all.
	private const int MinimumVotes = 4;

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

	// Bumped on every map/vote cycle so that timers scheduled for a previous
	// map are ignored if a new map (and therefore a new cycle) has started.
	private int _currentVoteId;

	// The map we last started a vote cycle for. OnMapStart can fire several times
	// during a single level load, so we only react when the map actually changes.
	private string? _currentMap;

	private static readonly string Tag = $" {ChatColors.Green}[DT Vote]{ChatColors.Default}";

	public override void Load(bool hotReload)
	{
		RegisterListener<Listeners.OnMapStart>(OnMapStartListener);

		Logger.LogInformation("RapidFireFix loaded (hotReload={HotReload}). A Double Tap vote runs ~{Delay}s after each map start. Use css_dtvote to start one now.", hotReload, (int)VoteStartDelaySeconds);

		// Start a cycle right away as well. OnMapStart only fires on the *next*
		// map change, so without this a plugin loaded on an already-running map
		// would do nothing until the map changed.
		_currentMap = Server.MapName;
		BeginMapVoteCycle();
	}

	private void OnMapStartListener(string mapName)
	{
		// OnMapStart can fire more than once during a single level load; only
		// react the first time we see a given map so the vote (and its repeated
		// announcement) runs exactly one cycle per map.
		if (mapName == _currentMap)
			return;

		Logger.LogInformation("Map started: {Map}. Scheduling Double Tap vote.", mapName);
		_currentMap = mapName;
		BeginMapVoteCycle();
	}

	private void BeginMapVoteCycle()
	{
		// Safe default until the vote resolves: the fix is ON (double tap disabled).
		_doubleTapEnabled = false;
		_voteInProgress = false;
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

		// Not enough players to reach the minimum turnout. Instead of staying
		// silent, tell players why there's no vote (repeated so it's visible) and
		// keep the fix on. A forced (test) vote skips this check.
		if (!force && players < MinimumVotes)
		{
			Logger.LogInformation("Double Tap vote skipped: only {Players}/{Min} players connected.", players, MinimumVotes);
			SpamMessage(voteId, false,
				$"{Tag} Need at least {ChatColors.Yellow}{MinimumVotes}{ChatColors.Default} players to hold a Double Tap vote — only {ChatColors.Yellow}{players}{ChatColors.Default} online, so DT stays disabled.");
			return;
		}

		_voteInProgress = true;
		_votes.Clear();

		Logger.LogInformation("Double Tap vote opened (players={Players}, force={Force}).", players, force);

		// Spam the "vote is open" line a few times so nobody misses it.
		SpamMessage(voteId, true,
			$"{Tag} Vote to {ChatColors.Lime}ENABLE Double Tap{ChatColors.Default} (rapid fire): type {ChatColors.Yellow}!yes{ChatColors.Default} or {ChatColors.Yellow}!no{ChatColors.Default} ({ChatColors.Yellow}{(int)VoteDurationSeconds}s{ChatColors.Default}, need {ChatColors.Yellow}{MinimumVotes}+{ChatColors.Default} votes).");

		// A single reminder halfway through the vote window.
		AddTimer(VoteDurationSeconds / 2.0f, () =>
		{
			if (voteId == _currentVoteId && _voteInProgress)
				Server.PrintToChatAll($"{Tag} Double Tap vote still open — {ChatColors.Yellow}!yes{ChatColors.Default} / {ChatColors.Yellow}!no{ChatColors.Default}.");
		});

		// Close the vote once the window elapses.
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
		int total = yes + no;

		// The vote must reach the minimum turnout AND have a simple majority of
		// YES (integer math, no rounding). Too few votes, a tie, or majority NO
		// all leave the fix on.
		bool enoughVotes = total >= MinimumVotes;
		bool majorityYes = yes * 100 > total * VotePassPercentage;
		bool passed = enoughVotes && majorityYes;

		_doubleTapEnabled = passed;

		Logger.LogInformation("Double Tap vote ended: YES={Yes} NO={No} total={Total} passed={Passed}.", yes, no, total, passed);

		if (passed)
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Lime}Vote PASSED{ChatColors.Default} (YES {yes} / NO {no}). Double Tap {ChatColors.Lime}ENABLED{ChatColors.Default} — rapid fire fix is OFF this map.");
		}
		else if (!enoughVotes)
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Red}Vote FAILED{ChatColors.Default} — only {total} vote(s), need {MinimumVotes}+. DT is disabled.");
		}
		else
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Red}Vote FAILED{ChatColors.Default} (YES {yes} / NO {no}). DT is disabled.");
		}
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
		_doubleTapEnabled = false;
		_voteInProgress = false;
		_votes.Clear();

		int voteId = ++_currentVoteId;
		StartVote(voteId, force: true);
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
	}

	[GameEventHandler]
	public HookResult OnBulletImpact(EventBulletImpact evt, GameEventInfo info)
	{
		// Vote passed -> double tap is allowed this map, so skip the rapid-fire
		// fix entirely and let the weapon fire at whatever rate the client asks.
		if (_doubleTapEnabled)
			return HookResult.Continue;

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
