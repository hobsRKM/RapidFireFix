using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace RapidFireFix;

public class RapidFireFix : BasePlugin
{
	public override string ModuleName => "Rapid Fire Fix";

	public override string ModuleVersion => "1.1.0";

	public override string ModuleAuthor => "jon";

	// ---- Vote configuration ----

	// How long after a map starts before the vote opens. This gives players
	// time to (re)connect and spawn after the map change before they are asked.
	private const float VoteStartDelaySeconds = 10.0f;

	// How long the vote stays open for players to cast a vote.
	private const float VoteDurationSeconds = 30.0f;

	// Percentage of the *cast* votes that must be YES for the vote to pass.
	// 50 = simple majority (strictly more YES than NO). A tie or no votes at all
	// fails the vote, which leaves the rapid-fire fix enabled (the safe default).
	private const int VotePassPercentage = 50;

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

	private static readonly string Tag = $" {ChatColors.Green}[DT Vote]{ChatColors.Default}";

	public override void Load(bool hotReload)
	{
		RegisterListener<Listeners.OnMapStart>(OnMapStartListener);

		// On a hot reload OnMapStart won't fire but players are already on the
		// server, so kick off a vote cycle straight away.
		if (hotReload)
			BeginMapVoteCycle();
	}

	private void OnMapStartListener(string mapName) => BeginMapVoteCycle();

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

	private void StartVote(int voteId)
	{
		_voteInProgress = true;
		_votes.Clear();

		Server.PrintToChatAll($"{Tag} Vote to {ChatColors.Lime}ENABLE Double Tap{ChatColors.Default} (rapid fire) for this map.");
		Server.PrintToChatAll($"{Tag} Type {ChatColors.Yellow}!dt yes{ChatColors.Default} or {ChatColors.Yellow}!dt no{ChatColors.Default} — you have {ChatColors.Yellow}{(int)VoteDurationSeconds}{ChatColors.Default} seconds.");

		// A single reminder halfway through the vote window.
		AddTimer(VoteDurationSeconds / 2.0f, () =>
		{
			if (voteId == _currentVoteId && _voteInProgress)
				Server.PrintToChatAll($"{Tag} Double Tap vote still open — {ChatColors.Yellow}!dt yes{ChatColors.Default} / {ChatColors.Yellow}!dt no{ChatColors.Default}.");
		});

		// Close the vote once the window elapses.
		AddTimer(VoteDurationSeconds, () =>
		{
			if (voteId == _currentVoteId)
				EndVote();
		});
	}

	private void EndVote()
	{
		if (!_voteInProgress)
			return;

		_voteInProgress = false;

		int yes = _votes.Values.Count(v => v);
		int no = _votes.Values.Count(v => !v);
		int total = yes + no;

		// Simple majority of the cast votes using integer math (no rounding).
		// With no votes cast this stays false, so the fix remains enabled.
		bool passed = total > 0 && yes * 100 > total * VotePassPercentage;

		_doubleTapEnabled = passed;

		if (passed)
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Lime}Vote PASSED{ChatColors.Default} (YES {yes} / NO {no}). Double Tap {ChatColors.Lime}ENABLED{ChatColors.Default} — rapid fire fix is OFF this map.");
		}
		else
		{
			Server.PrintToChatAll($"{Tag} {ChatColors.Red}Vote FAILED{ChatColors.Default} (YES {yes} / NO {no}). DT is disabled.");
		}
	}

	[ConsoleCommand("css_dt", "Vote to enable Double Tap (rapid fire) for this map")]
	[CommandHelper(minArgs: 1, usage: "<yes|no>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
	public void OnDoubleTapVoteCommand(CCSPlayerController? player, CommandInfo info)
	{
		if (player == null || !player.IsValid)
			return;

		if (!_voteInProgress)
		{
			info.ReplyToCommand($"{Tag} There is no Double Tap vote in progress right now.");
			return;
		}

		bool voteYes;
		switch (info.GetArg(1).ToLowerInvariant())
		{
			case "yes":
			case "y":
			case "1":
				voteYes = true;
				break;
			case "no":
			case "n":
			case "2":
				voteYes = false;
				break;
			default:
				info.ReplyToCommand($"{Tag} Usage: {ChatColors.Yellow}!dt <yes|no>{ChatColors.Default}");
				return;
		}

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
