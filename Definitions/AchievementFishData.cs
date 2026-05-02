using ff14bot.Enums;
using OceanTripPlanner;
using OceanTripPlanner.Definitions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ocean_Trip.Definitions
{
	/// <summary>
	/// Types of ocean fishing achievements based on fish categories
	/// </summary>
	public enum AchievementType
	{
		None = 0,

		// Indigo Route Achievements
		Mantas = 1,
		Octopods = 2,
		Sharks = 3,
		Jellyfish = 4,
		Seadragons = 5,
		Balloons = 6,
		Crabs = 7,

		// Ruby Route Achievements
		Shrimp = 8,
		Shellfish = 9,
		Squid = 10,
		MantisShrimp = 11,
		Prehistoric = 12
	}

	/// <summary>
	/// Hook type preference for catching specific fish
	/// </summary>
	public enum HookType
	{
		Normal = 0,
		Double = 1,
		Triple = 2
	}

	/// <summary>
	/// Information about a fish that counts toward an achievement
	/// </summary>
	public class AchievementFishInfo
	{
		/// <summary>Fish ID from OceanFish definitions</summary>
		public uint FishId { get; set; }

		/// <summary>Display name of the fish</summary>
		public string FishName { get; set; }

		/// <summary>Which achievement category this fish belongs to</summary>
		public AchievementType Achievement { get; set; }

		/// <summary>Location/zone short name (e.g., "galadion", "rhotano", "south")</summary>
		public string Location { get; set; }

		/// <summary>Which route this fish appears on</summary>
		public FishingRoute Route { get; set; }

		/// <summary>Preferred hook type for catching this fish (Normal, Double, or Triple)</summary>
		public HookType PreferredHookType { get; set; }

		/// <summary>Preferred bait ID for catching this fish</summary>
		public uint PreferredBait { get; set; }

		/// <summary>Whether this is a spectral current fish</summary>
		public bool IsSpectral { get; set; }

		/// <summary>Expected bite type (Light, Medium, Heavy)</summary>
		public TugType BiteType { get; set; }

		/// <summary>Minimum bite time in seconds</summary>
		public float BiteStart { get; set; }

		/// <summary>Maximum bite time in seconds</summary>
		public float BiteEnd { get; set; }

		/// <summary>Per-bait bite timers keyed by bait item ID</summary>
		public Dictionary<string, float[]> BiteTimers { get; set; }

		public (float start, float end) GetBiteRange(uint baitId)
		{
			if (BiteTimers != null && BiteTimers.TryGetValue(baitId.ToString(), out var range) && range.Length >= 2)
				return (range[0], range[1]);
			return (BiteStart, BiteEnd);
		}
	}

	/// <summary>
	/// Static cache for achievement fish data, populated from fishList.json
	/// </summary>
	public static class AchievementFishDataCache
	{
		private static List<AchievementFishInfo> _achievementFishList;

		private static readonly HashSet<string> IndigoZones = new HashSet<string>
			{ "galadion", "south", "north", "rhotano", "ciel", "blood", "sound" };

		/// <summary>
		/// Maps the Achievement string in fishList.json to the AchievementType enum
		/// </summary>
		private static readonly Dictionary<string, AchievementType> AchievementStringMap = new Dictionary<string, AchievementType>
		{
			{ "Manta", AchievementType.Mantas },
			{ "Octopus", AchievementType.Octopods },
			{ "Shark", AchievementType.Sharks },
			{ "Jellyfish", AchievementType.Jellyfish },
			{ "Seadragon", AchievementType.Seadragons },
			{ "Boxfish", AchievementType.Balloons },
			{ "Crab", AchievementType.Crabs },
			{ "Shrimp", AchievementType.Shrimp },
			{ "Mussel", AchievementType.Shellfish },
			{ "Squid", AchievementType.Squid },
			{ "Mantis", AchievementType.MantisShrimp },
			{ "Prehistoric", AchievementType.Prehistoric }
		};

		/// <summary>
		/// Gets all achievement fish data
		/// </summary>
		public static List<AchievementFishInfo> GetAchievementFish()
		{
			if (_achievementFishList == null)
			{
				_achievementFishList = InitializeAchievementFishData();
			}
			return _achievementFishList;
		}

		/// <summary>
		/// Gets achievement fish for a specific achievement type
		/// </summary>
		public static List<AchievementFishInfo> GetFishForAchievement(AchievementType achievementType)
		{
			return GetAchievementFish()
				.Where(f => f.Achievement == achievementType)
				.ToList();
		}

		/// <summary>
		/// Gets achievement fish for a specific location and achievement
		/// </summary>
		public static List<AchievementFishInfo> GetFishForLocation(string location, AchievementType achievementType)
		{
			return GetAchievementFish()
				.Where(f => f.Achievement == achievementType && f.Location == location)
				.ToList();
		}

		/// <summary>
		/// Gets achievement fish for a specific route
		/// </summary>
		public static List<AchievementFishInfo> GetFishForRoute(FishingRoute route)
		{
			return GetAchievementFish()
				.Where(f => f.Route == route)
				.ToList();
		}

		/// <summary>
		/// Determines which achievement types are valid for the given route
		/// </summary>
		public static List<AchievementType> GetValidAchievementsForRoute(FishingRoute route)
		{
			if (route == FishingRoute.Indigo)
			{
				return new List<AchievementType>
				{
					AchievementType.Mantas,
					AchievementType.Octopods,
					AchievementType.Sharks,
					AchievementType.Jellyfish,
					AchievementType.Seadragons,
					AchievementType.Balloons,
					AchievementType.Crabs
				};
			}
			else
			{
				return new List<AchievementType>
				{
					AchievementType.Shrimp,
					AchievementType.Shellfish,
					AchievementType.Squid,
					AchievementType.MantisShrimp,
					AchievementType.Prehistoric
				};
			}
		}

		/// <summary>
		/// Returns the user's currently selected achievement focus based on the active route
		/// </summary>
		public static AchievementType GetCurrentAchievementFocus()
		{
			int focus = OceanTripNewSettings.Instance.FishingRoute == FishingRoute.Indigo
				? OceanTripNewSettings.Instance.IndigoAchievementFocus
				: OceanTripNewSettings.Instance.RubyAchievementFocus;
			if (Enum.IsDefined(typeof(AchievementType), focus))
				return (AchievementType)focus;
			return AchievementType.None;
		}

		/// <summary>
		/// Maps an Achievement string from fishList.json to the AchievementType enum.
		/// Returns AchievementType.None if the string doesn't match.
		/// </summary>
		public static AchievementType MapAchievementString(string achievement)
		{
			if (achievement != null && AchievementStringMap.TryGetValue(achievement, out var type))
				return type;
			return AchievementType.None;
		}

		/// <summary>
		/// Initialize achievement fish data from fishList.json via FishDataCache
		/// </summary>
		private static List<AchievementFishInfo> InitializeAchievementFishData()
		{
			var allFish = FishDataCache.GetFish();

			return allFish
				.Where(f => !string.IsNullOrEmpty(f.Achievement) && AchievementStringMap.ContainsKey(f.Achievement))
				.Select(f => new AchievementFishInfo
				{
					FishId = (uint)f.FishID,
					FishName = f.FishName,
					Achievement = AchievementStringMap[f.Achievement],
					Location = f.RouteShortName,
					Route = IndigoZones.Contains(f.RouteShortName) ? FishingRoute.Indigo : FishingRoute.Ruby,
					PreferredHookType = f.THBonus > 0 ? HookType.Triple : f.DHBonus > 0 ? HookType.Double : HookType.Normal,
					PreferredBait = f.FavoriteBait,
					IsSpectral = f.SpectralFish,
					BiteType = f.BiteType,
					BiteStart = f.BiteStart,
					BiteEnd = f.BiteEnd,
					BiteTimers = f.BiteTimers
				})
				.ToList();
		}

		/// <summary>
		/// Clears the cache to force reload
		/// </summary>
		public static void InvalidateCache()
		{
			_achievementFishList = null;
		}
	}
}
