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

	public override string ModuleVersion => "1.5.1";

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
	// who don't vote effectively count as NO.
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

	private bool _doubleTapEnabled;
	private bool _voteInProgress;
	private readonly Dictionary<ulong, bool> _votes = new();
	private int _votePlayers;
	private int _currentVoteId;
	private string? _currentMap;
	private bool _voteHeldThisMap;
	private bool _loggedEnabledSkip;

	private static readonly string Tag = $" {ChatColors.Green}[DT Vote]{ChatColors.Default}";

	public override void Load(bool hotReload)
	{
		RegisterListener<Listeners.OnMapStart>(OnMapStartListener);

		Logger.LogInformation("[DT] Loaded (hotReload={HotReload}, map={Map}, players={Players}). Vote triggers on the first round of each map. Commands: css_dtvote (force), css_dtstatus (state).", hotReload, Server.MapName, CountHumanPlayers());

		// Handle the map we're already on right away. OnRoundStart drives votes on
		// later maps, but on load / hot reload the next round could be a while off.
		_currentMap = Server.MapName;
		_voteHeldThisMap = true;
		BeginMapVoteCycle();
	}

	// Map-change detector #1: OnMapStart gives a reliable new-map name (but fires
	// before players are in, so it only arms the vote — it doesn't open it).
	private void OnMapStartListener(string mapName)
	{
		bool isNew = mapName != _currentMap;
		Logger.LogInformation("[DT] OnMapStart fired: map={Map} prevMap={Prev} newMap={New}.", mapName, _currentMap, isNew);

		if (isNew)
		{
			_currentMap = mapName;
			_voteHeldThisMap = false;
			_doubleTapEnabled = false;
			_loggedEnabledSkip = false;
		}
	}

	// Map-change detector #2 + the actual trigger: round start is reliable and
	// happens when players are spawned. It opens the vote on the first round of
	// each map.
	[GameEventHandler]
	public HookResult OnRoundStart(EventRoundStart evt, GameEventInfo info)
	{
		string map = Server.MapName;
		Logger.LogInformation("[DT] round_start: serverMap={Map} currentMap={Cur} voteHeldThisMap={Held} voteInProgress={InProg} players={Players}.", map, _currentMap, _voteHeldThisMap, _voteInProgress, CountHumanPlayers());

		// Fallback map-change detection in case OnMapStart didn't fire or was late.
		if (map != _currentMap)
		{
			Logger.LogInformation("[DT] round_start: new map detected ({Old} -> {New}); arming vote.", _currentMap, map);
			_currentMap = map;
			_voteHeldThisMap = false;
			_doubleTapEnabled = false;
			_loggedEnabledSkip = false;
		}

		if (!_voteHeldThisMap)
		{
			_voteHeldThisMap = true;
			Logger.LogInformation("[DT] round_start: first round on {Map} -> scheduling the vote.", map);
			BeginMapVoteCycle();
		}
		else
		{
			Logger.LogInformation("[DT] round_start: vote already handled for this map; nothing to do.");
		}

		return HookResult.Continue;
	}

	private void BeginMapVoteCycle()
	{
		_doubleTapEnabled = false;
		_voteInProgress = false;
		_loggedEnabledSkip = false;
		_votes.Clear();

		int voteId = ++_currentVoteId;
		Logger.LogInformation("[DT] BeginMapVoteCycle: scheduling StartVote in {Delay}s (voteId={Id}).", (int)VoteStartDelaySeconds, voteId);

		AddTimer(VoteStartDelaySeconds, () =>
		{
			Logger.LogInformation("[DT] Start-delay elapsed (voteId={Id}, currentVoteId={Cur}).", voteId, _currentVoteId);
			if (voteId == _currentVoteId)
				StartVote(voteId);
			else
				Logger.LogInformation("[DT] Superseded by a newer cycle; not starting this one.");
		});
	}

	private void StartVote(int voteId, bool force = false)
	{
		Logger.LogInformation("[DT] StartVote entered (voteId={Id}, force={Force}, voteInProgress={InProg}).", voteId, force, _voteInProgress);

		if (_voteInProgress)
		{
			Logger.LogInformation("[DT] StartVote aborted: a vote is already in progress.");
			return;
		}

		int players = CountHumanPlayers();
		Logger.LogInformation("[DT] StartVote: human players={Players}, minimum={Min}.", players, MinimumPlayers);

		if (!force && players < MinimumPlayers)
		{
			Logger.LogInformation("[DT] StartVote: too few players ({Players}/{Min}); announcing and skipping.", players, MinimumPlayers);
			SpamMessage(voteId, false,
				$"{Tag} Need at least {ChatColors.Yellow}{MinimumPlayers}{ChatColors.Default} players to hold a Double Tap vote — only {ChatColors.Yellow}{players}{ChatColors.Default} online, so DT stays disabled.");
			return;
		}

		_voteInProgress = true;
		_votes.Clear();
		_votePlayers = players;

		int required = RequiredYesVotes(players);
		Logger.LogInformation("[DT] StartVote: vote OPEN (players={Players}, needYes={Required}, force={Force}).", players, required, force);

		SpamMessage(voteId, true,
			$"{Tag} Vote to {ChatColors.Lime}ENABLE Double Tap{ChatColors.Default} (rapid fire): type {ChatColors.Yellow}!yes{ChatColors.Default} or {ChatColors.Yellow}!no{ChatColors.Default} ({ChatColors.Yellow}{(int)VoteDurationSeconds}s{ChatColors.Default}). Need {ChatColors.Yellow}{required}{ChatColors.Default} YES ({RequiredYesPercentage}% of {players}).");

		AddTimer(VoteDurationSeconds / 2.0f, () =>
		{
			if (voteId == _currentVoteId && _voteInProgress)
				Server.PrintToChatAll($"{Tag} Double Tap vote still open — {ChatColors.Yellow}!yes{ChatColors.Default} / {ChatColors.Yellow}!no{ChatColors.Default}.");
		});

		AddTimer(VoteDurationSeconds, () =>
		{
			if (voteId == _currentVoteId)
				EndVote();
		});
	}

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
		bool passed = yes >= required;

		_doubleTapEnabled = passed;
		_loggedEnabledSkip = false;

		Logger.LogInformation("[DT] EndVote: YES={Yes} NO={No} required={Required} players={Players} passed={Passed}.", yes, no, required, _votePlayers, passed);

		if (passed)
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Lime}Vote PASSED{ChatColors.Default} (YES {yes} / NO {no}, needed {required} of {_votePlayers}). Double Tap {ChatColors.Lime}ENABLED{ChatColors.Default} — rapid fire fix is OFF this map.");
		}
		else
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Red}Vote FAILED{ChatColors.Default} (YES {yes} / NO {no}, needed {required} of {_votePlayers}). DT is disabled.");
		}
	}

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
		Logger.LogInformation("[DT] css_dtvote used — force-starting a Double Tap vote.");
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

	[ConsoleCommand("css_dtstatus", "Show the Double Tap vote state")]
	public void OnStatusCommand(CCSPlayerController? player, CommandInfo info)
	{
		int players = CountHumanPlayers();
		int required = RequiredYesVotes(_votePlayers);
		int yes = _votes.Values.Count(v => v);
		int no = _votes.Values.Count(v => !v);

		string state = _doubleTapEnabled
			? $"{ChatColors.Lime}ENABLED{ChatColors.Default} (rapid-fire fix OFF)"
			: $"{ChatColors.Red}DISABLED{ChatColors.Default} (rapid-fire fix ON)";

		info.ReplyToCommand($"{Tag} Double Tap is {state}.");
		info.ReplyToCommand($"{Tag} voteInProgress={_voteInProgress} | voteHeldThisMap={_voteHeldThisMap} | map={_currentMap}");
		info.ReplyToCommand($"{Tag} players={players} | votePlayers={_votePlayers} | needYes={required} | YES={yes} NO={no} | voteId={_currentVoteId}");

		Logger.LogInformation("[DT] css_dtstatus: enabled={Enabled} voteInProgress={InProg} voteHeldThisMap={Held} map={Map} players={Players} votePlayers={VotePlayers} needYes={Required} yes={Yes} no={No} voteId={VoteId}.",
			_doubleTapEnabled, _voteInProgress, _voteHeldThisMap, _currentMap, players, _votePlayers, required, yes, no, _currentVoteId);
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

		if (!_voteInProgress)
		{
			Logger.LogInformation("[DT] Vote from {Name} ignored: no vote in progress.", player.PlayerName);
			return;
		}

		_votes[player.SteamID] = voteYes;

		int yes = _votes.Values.Count(v => v);
		int required = RequiredYesVotes(_votePlayers);
		Logger.LogInformation("[DT] {Name} voted {Choice}. YES={Yes}/{Required}.", player.PlayerName, voteYes ? "YES" : "NO", yes, required);

		string choice = voteYes ? $"{ChatColors.Lime}YES{ChatColors.Default}" : $"{ChatColors.Red}NO{ChatColors.Default}";
		info.ReplyToCommand($"{Tag} Your vote ({choice}) has been recorded.");

		// Finish early once enough YES votes are in.
		if (voteYes && yes >= required)
		{
			Logger.LogInformation("[DT] Early finish: YES {Yes} reached required {Required}.", yes, required);
			EndVote();
		}
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
				Logger.LogInformation("[DT] Double Tap ENABLED — skipping the rapid-fire fix, so rapid fire is allowed this map.");
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
