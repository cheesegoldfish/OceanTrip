using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Buddy.Coroutines;
using Clio.Utilities;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using Ocean_Trip.Definitions;
using OceanTrip;
using OceanTripPlanner.Definitions;
using OceanTripPlanner.Helpers;

namespace OceanTripPlanner.Strategies
{
	/// <summary>
	/// Manages a complete fishing session for one zone/stop on the ocean voyage
	/// </summary>
	public class FishingSessionManager
	{
		private readonly GameStateCache _gameCache;
		private readonly HookingStrategy _hookingStrategy;
		private readonly bool _loggingEnabled;

		public FishingSessionManager(GameStateCache gameCache, HookingStrategy hookingStrategy, bool enableLogging = true)
		{
			_gameCache = gameCache;
			_hookingStrategy = hookingStrategy;
			_loggingEnabled = enableLogging;
		}

		/// <summary>
		/// Execute a complete fishing session for one zone
		/// </summary>
		public async Task ExecuteFishingSession(FishingSessionContext context)
		{
			bool spectraled = false;
			bool hookExecuted = false;

			// Handle food consumption at start of session
			await ConsumeFood(context);

			while (context.ShouldContinueFishingCallback())
			{
				// Fast path: If pole is ready, skip expensive overhead and jump straight to casting
				if (FishingManager.State == FishingState.PoleReady)
				{
					// Minimal cache refresh
					_gameCache.RefreshIfNeeded();
					// Don't update spectraled here - let ManageBuffsCallback detect the change
				}
				else
				{
					// Full preparation path for first cast or between zones
					_gameCache.RefreshIfNeeded();
					if (FishingManager.State == FishingState.None)
					{
						context.RefreshUICallback();
					}

					// Just in case you're already standing in a fishing spot. IE: Restarting botbase/rebornbuddy
					if (!ActionManager.CanCast(Actions.Cast, Core.Me) && FishingManager.State == FishingState.None)
					{
						await MoveToFishingSpot(context.Spot);
					}

					context.RefreshBaitCallback();
				}

				if (FishingManager.State == FishingState.None || FishingManager.State == FishingState.PoleReady)
				{
					hookExecuted = false;
					// Process caught fish and check for Identical Cast BEFORE buff management
					// (Thaliak's Favor can trigger a GCD that blocks Identical Cast)
					bool identicalCastUsed = await context.ProcessCaughtFishCallback();

					// If Identical Cast was used, skip everything else
					if (identicalCastUsed)
					{
						await Coroutine.Yield();
						continue;
					}
				}

				// Manage buffs and consumables (IMPORTANT: Always check, not just on first cast!)
				// This will detect and log spectral changes
				await context.ManageBuffsCallback(spectraled);

				// Update spectraled status after management (allows ManageBuffsCallback to detect changes)
				spectraled = (_gameCache.CurrentWeatherId == Weather.Spectral);

				if (FishingManager.State == FishingState.None || FishingManager.State == FishingState.PoleReady)
				{
					hookExecuted = false;

					{
						// Check for Mooch before using Mooch II
						Log("Checking for Mooch before moving into bait checks.", OceanLogLevel.Debug);
						if (FishingManager.CanMoochAny == FishingManager.AvailableMooch.Mooch || FishingManager.CanMoochAny == FishingManager.AvailableMooch.Both)
						{
							Log("Using Mooch!");
							FishingManager.Mooch();
							context.SetLastCastMooch(true);
						}
						else if (FishingManager.CanMoochAny == FishingManager.AvailableMooch.MoochTwo)
						{
							Log("Using Mooch II!");
							FishingManager.MoochTwo();
							context.SetLastCastMooch(true);
						}
						else
						{
							// Select and apply bait based on current conditions
							await context.SelectAndApplyBaitCallback(spectraled);

							Log("Casting!", OceanLogLevel.Debug);

							FishingManager.Cast();
							context.SetLastCastMooch(false);

							// Apply lure after cast if configured
							await ApplyLure(context);
						}
					}

					await Coroutine.Yield();
				}

				while ((FishingManager.State != FishingState.PoleReady) && context.ShouldContinueFishingCallback())
				{
					// Refresh cache to detect spectral changes immediately during bite wait
					_gameCache.RefreshIfNeeded();

					//Spectral popped, don't wait for normal fish
					if (_gameCache.CurrentWeatherId == Weather.Spectral && !spectraled)
					{
						Log("Spectral popped!");
						spectraled = true;

						if (FishingManager.CanHook)
							FishingManager.Hook();
					}

					if (FishingManager.CanHook && FishingManager.State == FishingState.Bite && !hookExecuted)
					{
						hookExecuted = true;
						var hookContext = new HookContext
						{
							BiteElapsedSeconds = FishingManager.TimeSinceCast.TotalSeconds + FishingConstants.BITE_TIMER_OFFSET,
							Spectraled = spectraled,
							Location = context.Location,
							TimeOfDay = context.TimeOfDay,
							CurrentRoute = context.CurrentRoute,
							LastCastMooch = context.GetLastCastMooch()
						};
						hookContext.SetHookExecutedCallback(context.OnHookExecutedCallback);
						await _hookingStrategy.ExecuteHook(hookContext);
						context.SetLastCastMooch(false);
					}

					await Coroutine.Yield();
				}
			}

			// Cleanup after session
			spectraled = false;
			await Coroutine.Yield();

			//Log("Waiting for next stop...");
			if (FishingManager.State != FishingState.None)
			{
				ActionManager.DoAction(Actions.Quit, Core.Me);
			}
		}

		/// <summary>
		/// Handle food consumption at start of fishing session
		/// </summary>
		private async Task ConsumeFood(FishingSessionContext context)
		{
			uint edibleFood = 0;
			bool edibleFoodHQ = false;

			if (OceanTripNewSettings.Instance.OceanFood && !Core.Player.HasAura(CharacterAuras.WellFed))
			{
				uint food = (uint)OceanFood.NasiGoreng;

				if (DataManager.GetItem(food, true).ItemCount() >= 1)
				{
					edibleFood = food;
					edibleFoodHQ = true;
				}
				else if (DataManager.GetItem(food, false).ItemCount() >= 1)
				{
					edibleFood = food;
					edibleFoodHQ = false;
				}
				else
				{
					edibleFood = 0;
					edibleFoodHQ = false;
				}

				if (edibleFood > 0)
				{
					do
					{
						Log($"Eating {_gameCache.GetItemName(edibleFood, edibleFoodHQ)}...");

						foreach (BagSlot slot in InventoryManager.FilledSlots)
						{
							if (slot.RawItemId == (uint)edibleFood)
							{
								slot.UseItem();
							}
						}
						await Coroutine.Sleep(3000);

					} while (!Core.Player.Auras.Any(x => x.Id == CharacterAuras.WellFed));
					await Coroutine.Yield();
				}
				else
				{
					Log($"Out of {_gameCache.GetItemName(food, false)} to eat!");
				}
			}
		}

		/// <summary>
		/// Move to the designated fishing spot
		/// </summary>
		private async Task MoveToFishingSpot(int spot)
		{
			//Navigator.PlayerMover.MoveTowards(FishingConstants.FishSpots[spot]);
			while (FishingConstants.FishSpots[spot].Distance2DSqr(Core.Me.Location) > 2)
			{
				Navigator.PlayerMover.MoveTowards(FishingConstants.FishSpots[spot]);
				await Coroutine.Yield();
			}
			Navigator.PlayerMover.MoveStop();
			await Coroutine.Sleep(300);
			Core.Me.SetFacing(FishingConstants.Headings[spot]);

			await Coroutine.Yield();
		}

		/// <summary>
		/// Determine the appropriate lure action for the current context
		/// </summary>
		private uint GetLureAction(FishingSessionContext context)
		{
			var lureMode = OceanTripNewSettings.Instance.LureMode;
			if (lureMode == LureMode.Off)
				return 0;

			if (lureMode == LureMode.Modest)
				return Actions.ModestLure;
			if (lureMode == LureMode.Ambitious)
				return Actions.AmbitiousLure;

			// Auto mode: pick lure based on what the bot is currently chasing
			// Each check tries in priority order; first match wins

			// 1. Target fish override — only in the target zone
			uint targetFishId = OceanTripNewSettings.Instance.TargetFishId;
			if (targetFishId != 0)
			{
				var targetFish = FishDataCache.GetFish().FirstOrDefault(f => f.FishID == (int)targetFishId);
				if (targetFish != null && targetFish.RouteShortName == context.Location)
					return LureForBiteType(targetFish.BiteType);
			}

			// 2. Achievement focus — match achievement category fish at this zone
			var currentAchievement = GetCurrentAchievementType();
			if (currentAchievement != AchievementType.None)
			{
				var achievementFish = AchievementFishDataCache.GetFishForLocation(context.Location, currentAchievement);
				if (achievementFish != null && achievementFish.Any())
					return LureForMajorityBiteType(achievementFish.Select(f => f.BiteType));
			}

			// 3. Missing fish at this zone
			var missingFish = FishingLog.MissingFish();
			if (missingFish.Count > 0)
			{
				var missingHere = FishDataCache.GetFish()
					.Where(f => f.RouteShortName == context.Location && missingFish.Contains((uint)f.FishID) && !f.RequiresIntuition)
					.ToList();
				if (missingHere.Any())
					return LureForMajorityBiteType(missingHere.Select(f => f.BiteType));
			}

			// 4. Fallback — match highest-value fish at this zone
			var zoneFish = FishDataCache.GetFish()
				.Where(f => f.RouteShortName == context.Location && !f.RequiresIntuition)
				.OrderByDescending(f => f.Points)
				.Take(5)
				.ToList();
			if (zoneFish.Any())
				return LureForMajorityBiteType(zoneFish.Select(f => f.BiteType));

			return 0;
		}

		private static uint LureForBiteType(TugType biteType)
		{
			return biteType == TugType.Light ? Actions.ModestLure : Actions.AmbitiousLure;
		}

		private static uint LureForMajorityBiteType(IEnumerable<TugType> biteTypes)
		{
			int lightCount = biteTypes.Count(t => t == TugType.Light);
			int otherCount = biteTypes.Count(t => t != TugType.Light);
			return lightCount > otherCount ? Actions.ModestLure : Actions.AmbitiousLure;
		}

		private AchievementType GetCurrentAchievementType()
		{
			int focus = OceanTripNewSettings.Instance.FishingRoute == FishingRoute.Indigo
				? OceanTripNewSettings.Instance.IndigoAchievementFocus
				: OceanTripNewSettings.Instance.RubyAchievementFocus;
			if (Enum.IsDefined(typeof(AchievementType), focus))
				return (AchievementType)focus;
			return AchievementType.None;
		}

		/// <summary>
		/// Apply Modest or Ambitious Lure after casting (not on mooch)
		/// </summary>
		private async Task ApplyLure(FishingSessionContext context)
		{
			uint lureAction = GetLureAction(context);
			if (lureAction == 0)
				return;

			int stackCount = OceanTripNewSettings.Instance.LureStackCount;
			string lureName = lureAction == Actions.ModestLure ? "Modest Lure" : "Ambitious Lure";

			// Wait for cast animation to settle before applying lure
			await Coroutine.Sleep(500);

			for (int i = 0; i < stackCount; i++)
			{
				if (!context.ShouldContinueFishingCallback())
					break;

				// Don't apply lure if a bite already landed
				if (FishingManager.State == FishingState.Bite || FishingManager.State == FishingState.PoleReady)
					break;

				if (!ActionManager.CanCast(lureAction, Core.Me))
				{
					Log($"Cannot cast {lureName} (insufficient GP or not available).", OceanLogLevel.Debug);
					break;
				}

				Log($"Using {lureName} (stack {i + 1}/{stackCount}).");
				ActionManager.DoAction(lureAction, Core.Me);

				// Wait for lure animation + grace period
				await Coroutine.Sleep(2000);
			}
		}

		/// <summary>
		/// Internal logging method
		/// </summary>
		private void Log(string text, OceanLogLevel level = OceanLogLevel.Info)
		{
			if (!_loggingEnabled)
				return;

			// Filter based on log level and settings
			if (level == OceanLogLevel.Debug && !OceanTripNewSettings.Instance.LoggingMode)
				return;

			var msg = string.Format("[Ocean Trip] " + text);
			Logging.Write(Colors.Aqua, msg);
		}
	}

	/// <summary>
	/// Context for fishing session containing all necessary state and callbacks
	/// </summary>
	public class FishingSessionContext
	{
		public string Location { get; set; }
		public string TimeOfDay { get; set; }
		public int Spot { get; set; }
		public RouteWithFish CurrentRoute { get; set; }
		public ulong BaitId { get; set; }
		public ulong SpectralBaitId { get; set; }

		// Callbacks to main bot methods
		public Func<bool> ShouldContinueFishingCallback { get; set; }
		public Action RefreshUICallback { get; set; }
		public Action RefreshBaitCallback { get; set; }
		public Func<bool, Task> ManageBuffsCallback { get; set; }
		public Func<Task<bool>> ProcessCaughtFishCallback { get; set; }
		public Func<bool, Task> SelectAndApplyBaitCallback { get; set; }
		public Action<bool> OnHookExecutedCallback { get; set; }

		// State management
		private bool _lastCastMooch;

		public bool GetLastCastMooch() => _lastCastMooch;
		public void SetLastCastMooch(bool value) => _lastCastMooch = value;
	}
}
