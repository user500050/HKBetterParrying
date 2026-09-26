using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Modding;
using UnityEngine;

namespace BetterParrying
{
	public class BetterParryingSettings
	{
		public int LateParryMilliseconds = 40;
		public bool ExtendEarlyWindow = true;
		public bool DiagnosticLogging = false;
	}

	public class BetterParrying : Mod, ITogglableMod, IGlobalSettings<BetterParryingSettings>, IMenuMod
	{
		private long eventNumber;
		private long damageCallNumber;
		private bool initialized;
		private TriggerContext activeTrigger;
		private bool insideFixedStep;
		private bool resolvingDamage;
		private long fixedStep;
		private BetterParryingSettings settings = new BetterParryingSettings();
		private static readonly int[] DelayOptions =
			{ 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100 };
		private float LateParrySeconds => settings.LateParryMilliseconds / 1000f;
		private bool unloading;
		private static readonly FieldInfo SlashingField = typeof(NailSlash).GetField("slashing", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo CompletedField = typeof(NailSlash).GetField("animCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo StepField = typeof(NailSlash).GetField("stepCounter", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo ClashField = typeof(NailSlash).GetField("clashTinkPoly", BindingFlags.Instance | BindingFlags.NonPublic);
		private readonly Dictionary<Collider2D, bool> changedColliders = new Dictionary<Collider2D, bool>();
		private readonly List<PendingDamage> pendingDamage = new List<PendingDamage>();
		private readonly HashSet<long> parriedPairs = new HashSet<long>();

		private sealed class StepBeginMarker { }
		private sealed class StepEndMarker { }

		private sealed class PendingDamage
		{
			public On.HeroController.orig_TakeDamage Original;
			public HeroController Hero;
			public GameObject Source;
			public GlobalEnums.CollisionSide Side;
			public int Amount;
			public int Hazard;
			public long Call;
			public long Step;
			public long Pair;
			public float Deadline;
			public bool Cancelled;
		}

		private sealed class TriggerContext
		{
			public HutongGames.PlayMaker.Actions.Trigger2dEventLayer Action;
			public string Details;
		}

		public override string GetVersion() => "1.0.0";

		public void OnLoadGlobal(BetterParryingSettings saved)
		{
			settings = saved ?? new BetterParryingSettings();
			if (Array.IndexOf(DelayOptions, settings.LateParryMilliseconds) < 0)
				settings.LateParryMilliseconds = 40;
		}

		public BetterParryingSettings OnSaveGlobal() => settings;
		public bool ToggleButtonInsideMenu => true;

		public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggle)
		{
			var entries = new List<IMenuMod.MenuEntry>();
			if (toggle.HasValue) entries.Add(toggle.Value);
			entries.Add(new IMenuMod.MenuEntry("Late parry grace",
				Array.ConvertAll(DelayOptions, value => value.ToString(CultureInfo.InvariantCulture) + " ms"),
				"Additional late parry grace period; 0 ms disables it",
				index => { settings.LateParryMilliseconds = DelayOptions[index]; Log("Late grace=" + DelayOptions[index] + " ms"); },
				() => Array.IndexOf(DelayOptions, settings.LateParryMilliseconds)));
			entries.Add(new IMenuMod.MenuEntry("Extend early parry window", new[] { "Off", "On" },
				"Keep the nail active until the slash animation completes",
				index =>
				{
					settings.ExtendEarlyWindow = index == 1;
					if (!settings.ExtendEarlyWindow) RestoreClashColliders();
				}, () => settings.ExtendEarlyWindow ? 1 : 0));
			//entries.Add(new IMenuMod.MenuEntry("Diagnostic logging", new[] { "Off", "On" },
			//	"Detailed collision and parry logs for testing.",
			//	index => settings.DiagnosticLogging = index == 1,
			//	() => settings.DiagnosticLogging ? 1 : 0));
			return entries;
		}

		public override void Initialize()
		{
			if (initialized) return;
			initialized = true;
			unloading = false;
			bool installed = InstallFixedStepCallbacks();
			On.HeroController.NailParry += LogNailParry;
			On.HeroController.TakeDamage += LogTakeDamage;
			ModHooks.SlashHitHook += LogSlashHit;
			ModHooks.AttackHook += LogAttack;
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.DoTriggerEnter2D += TrackParryTrigger;
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.DoTriggerStay2D += CheckContinuingClash;
			On.NailSlash.FixedUpdate += ExtendClashWindow;
			Log("Shared parry prototype initialized. Fixed-step callbacks installed=" + installed +
				". SLASH CONTACT is not proof of damage or parry. Restart the game after replacing the DLL.");
			Log("MOD ENABLED: shared parry correction is " + (installed ? "ON" : "UNAVAILABLE"));
			Log("WINDOWS: late grace=" + settings.LateParryMilliseconds + " ms; early extension=" + settings.ExtendEarlyWindow);
		}

		public void Unload()
		{
			if (!initialized) return;
			// Finish accepted requests while their original hook delegates are still valid.
			insideFixedStep = false;
			unloading = true;
			EndFixedStep();
			initialized = false;
			On.HeroController.NailParry -= LogNailParry;
			On.HeroController.TakeDamage -= LogTakeDamage;
			ModHooks.SlashHitHook -= LogSlashHit;
			ModHooks.AttackHook -= LogAttack;
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.DoTriggerEnter2D -= TrackParryTrigger;
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.DoTriggerStay2D -= CheckContinuingClash;
			On.NailSlash.FixedUpdate -= ExtendClashWindow;
			RestoreClashColliders();
			activeTrigger = null;
			try
			{
				// Preserve every other mod's current loop entries.
				var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
				RemoveCallbacks(ref loop);
				UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
			}
			catch (Exception exception)
			{
				LogError("Could not remove loop markers (remaining callbacks are inactive): " + exception);
			}
			Log("MOD DISABLED: hooks removed; native game damage/parry handling restored.");
		}

		private static void RemoveCallbacks(ref UnityEngine.LowLevel.PlayerLoopSystem node)
		{
			if (node.subSystemList == null) return;
			var kept = new List<UnityEngine.LowLevel.PlayerLoopSystem>();
			foreach (var entry in node.subSystemList)
			{
				if (entry.type == typeof(StepBeginMarker) || entry.type == typeof(StepEndMarker)) continue;
				var child = entry;
				RemoveCallbacks(ref child);
				kept.Add(child);
			}
			node.subSystemList = kept.ToArray();
		}

		private bool InstallFixedStepCallbacks()
		{
			try
			{
				var loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
				if (!InsertCallbacks(ref loop))
				{
					LogError("FixedUpdate/Physics2DFixedUpdate not found; damage remains immediate.");
					return false;
				}
				UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
				return true;
			}
			catch (Exception exception)
			{
				LogError("Cannot install fixed-step callbacks; damage remains immediate: " + exception);
				return false;
			}
		}

		private bool InsertCallbacks(ref UnityEngine.LowLevel.PlayerLoopSystem node)
		{
			var children = node.subSystemList;
			if (children == null) return false;
			if (node.type == typeof(UnityEngine.PlayerLoop.FixedUpdate))
			{
				bool hasPhysics = Array.Exists(children,
					child => child.type == typeof(UnityEngine.PlayerLoop.FixedUpdate.Physics2DFixedUpdate));
				if (!hasPhysics) return false;
				var updated = new List<UnityEngine.LowLevel.PlayerLoopSystem>();
				updated.Add(new UnityEngine.LowLevel.PlayerLoopSystem
				{
					type = typeof(StepBeginMarker),
					updateDelegate = BeginFixedStep
				});
				foreach (var child in children)
					if (child.type != typeof(StepBeginMarker) && child.type != typeof(StepEndMarker))
						updated.Add(child);
				updated.Add(new UnityEngine.LowLevel.PlayerLoopSystem
				{
					type = typeof(StepEndMarker),
					updateDelegate = EndFixedStep
				});
				node.subSystemList = updated.ToArray();
				return true;
			}
			for (int i = 0; i < children.Length; i++)
				if (InsertCallbacks(ref children[i])) return true;
			return false;
		}

		private void BeginFixedStep()
		{
			if (!initialized) return;
			parriedPairs.Clear();
			fixedStep++;
			insideFixedStep = true;
		}

		private void EndFixedStep()
		{
			if (!initialized && pendingDamage.Count == 0) return;
			insideFixedStep = false;
			resolvingDamage = true;
			try
			{
				for (int index = 0; index < pendingDamage.Count;)
				{
					PendingDamage hit = pendingDamage[index];
					if (!unloading && !hit.Cancelled && Time.fixedTime + 0.00001f < hit.Deadline && hit.Hero != null)
					{
						index++;
						continue;
					}
					pendingDamage.RemoveAt(index);
					if (hit.Hero == null)
					{
						Trace("DAMAGE DISCARDED", () => "call=" + hit.Call + " reason=hero-destroyed");
						continue;
					}
					if (hit.Cancelled || (hit.Step == fixedStep && parriedPairs.Contains(hit.Pair)))
					{
						Trace("DAMAGE CANCELLED", () => "call=" + hit.Call + " step=" + hit.Step +
							" reason=same-source-parry-within-window source=" + ObjectPath(hit.Source) +
							" " + HeroState(hit.Hero));
						continue;
					}
					Trace("DAMAGE RESOLVE BEGIN", () => "call=" + hit.Call + " step=" + hit.Step +
						" source=" + ObjectPath(hit.Source) + " " + HeroState(hit.Hero));
					try
					{
						hit.Original(hit.Hero, hit.Source, hit.Side, hit.Amount, hit.Hazard);
					}
					catch (Exception exception)
					{
						LogError("Deferred damage handler failed for call=" + hit.Call + ": " + exception);
					}
					Trace("DAMAGE RESOLVE END", () => "call=" + hit.Call + " " + HeroState(hit.Hero));
				}
			}
			finally
			{
				parriedPairs.Clear();
				resolvingDamage = false;
			}
		}

		private static long PairKey(HeroController hero, GameObject source)
		{
			return ((long)hero.GetInstanceID() << 32) | (uint)source.GetInstanceID();
		}

		private void ExtendClashWindow(On.NailSlash.orig_FixedUpdate orig, NailSlash self)
		{
			orig(self);
			if (!settings.ExtendEarlyWindow) return;
			if (SlashingField == null || CompletedField == null || StepField == null || ClashField == null) return;
			var clash = ClashField.GetValue(self) as PolygonCollider2D;
			if (clash == null) return;
			bool liveSwing = (bool)SlashingField.GetValue(self) && !(bool)CompletedField.GetValue(self)
				&& (int)StepField.GetValue(self) > 1 && self.gameObject.activeInHierarchy;
			if (liveSwing && !clash.enabled)
			{
				if (!changedColliders.ContainsKey(clash))
					Trace("EARLY WINDOW EXTENDED", () => "slash=" + ObjectPath(self.gameObject));
				changedColliders[clash] = false;
				clash.enabled = true;
			}
			else if (!liveSwing && changedColliders.ContainsKey(clash))
			{
				clash.enabled = false;
				changedColliders.Remove(clash);
			}
			else if (clash.enabled)
				changedColliders.Remove(clash);
		}

		private void CheckContinuingClash(
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.orig_DoTriggerStay2D orig,
			HutongGames.PlayMaker.Actions.Trigger2dEventLayer self, Collider2D other)
		{
			orig(self, other);
			if (self.State.Name != "Detecting" || self.Fsm.ActiveStateName != "Detecting" ||
				self.Fsm.Name != "nail_clash_tink" || !IsSupportedAttack(self.Owner) ||
				other == null || !other.enabled || other.gameObject.layer != 16 ||
				other.gameObject.name != "Clash Tink" ||
				other.GetComponentInParent<NailSlash>() == null) return;
			Trace("CONTINUING CLASH CHECK", () => "source=" + ObjectPath(self.Owner));
			// Retain the game's tag/layer checks, collision storage, FSM transition and effects.
			self.DoTriggerEnter2D(other);
		}

		private void RestoreClashColliders()
		{
			foreach (var entry in changedColliders)
				if (entry.Key != null) entry.Key.enabled = entry.Value;
			changedColliders.Clear();
		}

		private static bool IsSupportedAttack(GameObject source)
		{
			if (source == null || !source.activeInHierarchy) return false;
			// Match the exact damage source, never an arbitrary parent/body collider.
			foreach (PlayMakerFSM fsm in source.GetComponents<PlayMakerFSM>())
				if (fsm.enabled && fsm.FsmName == "nail_clash_tink") return true;
			return false;
		}

		private static string Number(float value)
		{
			return value.ToString("F6", CultureInfo.InvariantCulture);
		}

		private static string ObjectPath(GameObject obj)
		{
			if (obj == null) return "<null>";
			string path = obj.name;
			Transform parent = obj.transform.parent;
			while (parent != null)
			{
				path = parent.name + "/" + path;
				parent = parent.parent;
			}
			return obj.scene.name + ":" + path + "#" + obj.GetInstanceID();
		}

		private static string HeroState(HeroController hero)
		{
			if (hero == null) return "hero=<null>";
			PlayerData data = hero.playerData;
			string health = data == null ? "hp=<null>" :
				"hp=" + data.GetInt("health") + " blue=" + data.GetInt("healthBlue");
			return health + " parryTimer=" + Number(hero.parryInvulnTimer) +
				" parryDuration=" + Number(hero.INVUL_TIME_PARRY);
		}

		// Diagnostic failures must not prevent the game's original methods from running.
		private void Trace(string label, Func<string> details)
		{
			if (!settings.DiagnosticLogging) return;
			try
			{
				Log("#" + (++eventNumber) + " " + label +
					" frame=" + Time.frameCount +
					" step=" + fixedStep + " inFixed=" + insideFixedStep +
					" time=" + Number(Time.time) +
					" fixedTime=" + Number(Time.fixedTime) + " " + details());
			}
			catch (Exception exception)
			{
				LogError("Diagnostic logging failed: " + exception.Message);
			}
		}

		private void LogAttack(GlobalEnums.AttackDirection direction)
		{
			Trace("ATTACK", () => "direction=" + direction + " " + HeroState(HeroController.instance));
		}

		private void LogSlashHit(Collider2D other, GameObject slash)
		{
			Trace("SLASH CONTACT", () => "slash=" + ObjectPath(slash) +
				" target=" + ObjectPath(other == null ? null : other.gameObject) +
				" collider=" + (other == null ? "<null>" : other.GetType().Name + "#" + other.GetInstanceID()) +
				" layer=" + (other == null ? "<null>" : other.gameObject.layer.ToString()) +
				" " + HeroState(HeroController.instance));
		}

		private void LogNailParry(On.HeroController.orig_NailParry orig, HeroController self)
		{
			Trace("PARRY BEGIN", () => HeroState(self));
			TriggerContext context = activeTrigger;
			Trace("PARRY TARGET", () => context == null
				? "triggerContext=<none>; no synchronous Trigger2dEventLayer caller captured"
				: context.Details + " currentState=" + context.Action.Fsm.ActiveStateName);
			orig(self);
			if (insideFixedStep && !resolvingDamage && context != null &&
				context.Action.Fsm.Name == "nail_clash_tink" && IsSupportedAttack(context.Action.Owner))
			{
				parriedPairs.Add(PairKey(self, context.Action.Owner));
				long pair = PairKey(self, context.Action.Owner);
				foreach (PendingDamage hit in pendingDamage)
					if (hit.Pair == pair && Time.fixedTime <= hit.Deadline + 0.00001f)
						hit.Cancelled = true;
				Trace("PARRY MATCH", () => "source=" + ObjectPath(context.Action.Owner) +
					" " + HeroState(self));
			}
			Trace("PARRY END", () => HeroState(self));
		}

		private void TrackParryTrigger(
			On.HutongGames.PlayMaker.Actions.Trigger2dEventLayer.orig_DoTriggerEnter2D orig,
			HutongGames.PlayMaker.Actions.Trigger2dEventLayer self, Collider2D other)
		{
			TriggerContext previous = activeTrigger;
			activeTrigger = null;
			try
			{
				// Capture the state before orig: sending the event can switch it immediately.
				try
				{
					activeTrigger = new TriggerContext
					{
						Action = self,
						Details = "owner=" + ObjectPath(self.Owner) +
							" fsm=" + self.Fsm.Name +
							" triggerState=" + self.State.Name +
							" configuredEvent=" + (self.sendEvent == null ? "<null>" : self.sendEvent.Name) +
							" filterLayer=" + (self.collideLayer == null ? "<null>" : self.collideLayer.Value.ToString()) +
							" filterTag=" + (self.collideTag == null ? "<null>" : self.collideTag.Value) +
							" incoming=" + ObjectPath(other == null ? null : other.gameObject) +
							" incomingCollider=" + (other == null ? "<null>" : other.GetType().Name + "#" + other.GetInstanceID()) +
							" incomingLayer=" + (other == null ? "<null>" : other.gameObject.layer.ToString())
					};
				}
				catch (Exception exception)
				{
					LogError("Trigger context capture failed: " + exception.Message);
				}
				orig(self, other);
			}
			finally
			{
				activeTrigger = previous;
			}
		}

		private void LogTakeDamage(On.HeroController.orig_TakeDamage orig,
			HeroController self, GameObject source, GlobalEnums.CollisionSide side,
			int damageAmount, int hazardType)
		{
			long call = ++damageCallNumber;
			Trace("DAMAGE BEGIN", () => "call=" + call + " source=" + ObjectPath(source) +
				" side=" + side + " amount=" + damageAmount + " hazardType=" + hazardType +
				" " + HeroState(self));
			if (insideFixedStep && !resolvingDamage && damageAmount > 0 && hazardType == 1 &&
				self.parryInvulnTimer <= 0f && IsSupportedAttack(source))
			{
				long pair = PairKey(self, source);
				foreach (PendingDamage hit in pendingDamage)
					if (hit.Pair == pair)
					{
						Trace("DAMAGE MERGED", () => "call=" + call + " pendingCall=" + hit.Call);
						return;
					}
				pendingDamage.Add(new PendingDamage
				{
					Original = orig,
					Hero = self,
					Source = source,
					Side = side,
					Amount = damageAmount,
					Hazard = hazardType,
					Call = call,
					Step = fixedStep,
					Pair = pair,
					Deadline = Time.fixedTime + LateParrySeconds
				});
				Trace("DAMAGE QUEUED", () => "call=" + call + " source=" + ObjectPath(source) +
					" " + HeroState(self));
				return;
			}
			orig(self, source, side, damageAmount, hazardType);
			Trace("DAMAGE END", () => "call=" + call + " " + HeroState(self));
		}
	}
}
