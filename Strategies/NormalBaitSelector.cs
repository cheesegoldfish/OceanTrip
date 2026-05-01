using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ff14bot;
using ff14bot.Managers;
using Ocean_Trip.Definitions;
using OceanTripPlanner.Definitions;
using OceanTripPlanner.Helpers;

namespace OceanTripPlanner.Strategies
{
	public class NormalBaitSelector : IBaitSelector
	{
		private readonly BaitChanger _baitChanger;
		private readonly PatienceManager _patienceManager;
		private readonly GameStateCache _gameCache;

		public NormalBaitSelector(BaitChanger baitChanger, PatienceManager patienceManager, GameStateCache gameCache)
		{
			_baitChanger = baitChanger;
			_patienceManager = patienceManager;
			_gameCache = gameCache;
		}

		public async Task SelectBait(BaitSelectionContext context)
		{
			var missingFish = context.MissingFish;
			var currentRoute = context.CurrentRoute;
			var timeOfDay = context.TimeOfDay;
			var focusFishLog = context.FocusFishLog;
			var caughtFish = context.CaughtFish;
			string currentWeather = context.CurrentWeather;

			var availableNormalFish = currentRoute?.NormalFish
				.Where(f => f.TimeOfDayExclusion1 != timeOfDay
					&& f.TimeOfDayExclusion2 != timeOfDay
					&& f.WeatherExclusion1 != currentWeather
					&& f.WeatherExclusion2 != currentWeather)
				.ToList() ?? new List<Fish>();

			if (OceanTripNewSettings.Instance.Patience == ShouldUsePatience.AlwaysUsePatience)
				await _patienceManager.UsePatience();

			uint selectedBait = 0;
			string baitReason = null;

			// Step 0: Target fish override — use target fish's bait when in its zone
			if (context.TargetFishId != 0)
			{
				var targetFish = availableNormalFish.FirstOrDefault(f => f.FishID == (int)context.TargetFishId);
				if (targetFish != null)
				{
					selectedBait = targetFish.FavoriteBait;
					baitReason = $"Target fish mode — using {_gameCache.GetItemName(targetFish.FavoriteBait)} for {targetFish.FishName}";
				}
			}

			// Step 1: Intuition buff active — use highest-points fish bait (always the Intuition fish)
			if (selectedBait == 0 && Core.Player.HasAura(CharacterAuras.FishersIntuition))
			{
				var topFish = availableNormalFish.OrderByDescending(f => f.Points).FirstOrDefault();
				if (topFish != null)
				{
					selectedBait = topFish.FavoriteBait;
					baitReason = $"Fisher's Intuition active — targeting {topFish.FishName} ({topFish.Points} pts)";
				}
			}

			if (selectedBait == 0 && focusFishLog)
			{
				// Step 2: Chase missing Intuition fish prereqs
				var missingIntuitionFish = availableNormalFish
					.Where(f => f.RequiresIntuition && missingFish.Contains((uint)f.FishID))
					.OrderByDescending(f => f.Points)
					.FirstOrDefault();

				if (missingIntuitionFish?.IntuitionPrereqs != null)
				{
					foreach (var prereq in missingIntuitionFish.IntuitionPrereqs)
					{
						if (!prereq.IsMooch && caughtFish.Count(x => x == (uint)prereq.FishID) < prereq.Count)
						{
							var prereqFish = availableNormalFish.FirstOrDefault(f => f.FishID == prereq.FishID);
							if (prereqFish != null)
							{
								selectedBait = prereqFish.FavoriteBait;
								var caught = caughtFish.Count(x => x == (uint)prereq.FishID);
								baitReason = $"Targeting {caught}/{prereq.Count}x {prereqFish.FishName} (prereq for missing {missingIntuitionFish.FishName})";
								break;
							}
						}
					}
				}

				// Step 3: Other missing fish, rarity-first
				if (selectedBait == 0)
				{
					selectedBait = BaitRanker.SelectBaitForMissingFish(availableNormalFish, missingFish);
					if (selectedBait != 0)
					{
						var targetedFish = availableNormalFish
							.Where(f => missingFish.Contains((uint)f.FishID) && !f.RequiresIntuition && f.FavoriteBait == selectedBait)
							.Select(f => f.FishName);
						baitReason = $"Targeting missing fish: {string.Join(", ", targetedFish)}";
					}
				}
			}

			// Step 4: Fallback — points optimization
			if (selectedBait == 0)
			{
				selectedBait = BaitRanker.SelectBaitForPoints(availableNormalFish);
				if (selectedBait != 0)
					baitReason = "Optimizing for points";
			}

			if (selectedBait == 0)
				selectedBait = (uint)context.DefaultBaitId;

			await _baitChanger.ChangeBait(selectedBait, baitReason);

			// Chum handling
			if (_gameCache.MaxGP >= FishingConstants.FULL_GP_BUFFER
				&& (_gameCache.GPDeficit <= FishingConstants.FULL_GP_BUFFER)
				&& OceanTripNewSettings.Instance.FullGPAction == FullGPAction.Chum)
			{
				if (ActionManager.CanCast(Actions.Chum, Core.Me))
				{
					_baitChanger.Log("Triggering Full GP Action to keep regen going - Chum!");
					ActionManager.DoAction(Actions.Chum, Core.Me);
				}
			}
		}
	}
}
