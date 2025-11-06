using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using SoftReferenceableAssets;
using UnityEngine;
using Object = UnityEngine.Object;

[PublicAPI]
public class Logger
{
	public event Action<string>? OnWarning;
	public event Action<string>? OnDebug;
	
	internal void LogDebug(string message) => OnDebug?.Invoke(message);
	internal void LogWarning(string message) => OnWarning?.Invoke(message);
}

public static class LocationManager
{
	[PublicAPI]
	public static List<string> RandomCreaturesToSpawn = new();

	public static readonly Logger logger = new();
	
    // private static object? configManager;
    private static BaseUnityPlugin? _plugin;
    internal static BaseUnityPlugin plugin
    {
        get
        {
            if (_plugin is null)
            {
                IEnumerable<TypeInfo> types;
                try
                {
                    types = Assembly.GetExecutingAssembly().DefinedTypes.ToList();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).Select(t => t.GetTypeInfo());
                }
                _plugin = (BaseUnityPlugin)BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(types.First(t => t.IsClass && typeof(BaseUnityPlugin).IsAssignableFrom(t)));
            }
            return _plugin;
        }
    }
    private static bool hasConfigSync = true;
    private static object? _configSync;
    private static object? configSync
    {
        get
        {
            if (_configSync == null && hasConfigSync)
            {
                if (Assembly.GetExecutingAssembly().GetType("ServerSync.ConfigSync") is { } configSyncType)
                {
                    _configSync = Activator.CreateInstance(configSyncType, plugin.Info.Metadata.GUID + " RS_LocationManager");
                    configSyncType.GetField("CurrentVersion").SetValue(_configSync, plugin.Info.Metadata.Version.ToString());
                    configSyncType.GetProperty("IsLocked")!.SetValue(_configSync, true);
                }
                else
                {
                    hasConfigSync = false;
                }
            }

            return _configSync;
        }
    }
    
	static LocationManager()
    {
	    Harmony harmony = new("org.bepinex.helpers.LocationManager");
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup),nameof(FejdStartup.Awake)), new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_FejdStartup))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(AssetBundleLoader), nameof(AssetBundleLoader.OnInitCompleted)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(SoftAssetLoader), nameof(SoftAssetLoader.Patch_AssetBundleLoader_OnInitCompleted))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(Localization), nameof(Localization.LoadCSV)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocalizeKey), nameof(LocalizeKey.AddLocalizedKeys))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(ZoneSystem), nameof(ZoneSystem.SetupLocations)), prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_SetupLocations))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Awake)), new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PrefabManager), nameof(PrefabManager.Patch_ZNetScene_Awake))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(Location), nameof(Location.Awake)), prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_Location_Awake))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(DungeonDB), nameof(DungeonDB.Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_DungeonDB_Awake))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(DungeonGenerator), nameof(DungeonGenerator.SetupAvailableRooms)), prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_DungeonGenerator_Spawn))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_Minimap_Awake))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(CreatureSpawner), nameof(CreatureSpawner.Awake)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_CreatureSpawner_Awake))));
	    harmony.Patch(AccessTools.DeclaredMethod(typeof(DungeonDB), nameof(DungeonDB.GenerateHashList)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocationManager), nameof(Patch_DungeonDB_GenerateHashList))));
    }
	
	[PublicAPI]
    internal static ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
    {
        ConfigEntry<T> configEntry = plugin.Config.Bind(group, name, value, description);
        configSync?.GetType().GetMethod("AddConfigEntry")!.MakeGenericMethod(typeof(T)).Invoke(_configSync, new object[] { configEntry });
        return configEntry;
    }

    [PublicAPI]
    internal static ConfigEntry<T> config<T>(string group, string name, T value, string description) => config(group, name, value, new ConfigDescription(description));

    internal static void Patch_FejdStartup(FejdStartup __instance)
    {
	    Helpers._ZNetScene = __instance.m_objectDBPrefab.GetComponent<ZNetScene>();
	    Helpers._ObjectDB = __instance.m_objectDBPrefab.GetComponent<ObjectDB>();
	    foreach(var location in CustomLocation.locations.Values) location.Setup();
    }

    internal static void Patch_CreatureSpawner_Awake(CreatureSpawner __instance)
    {
	    if (__instance.m_creaturePrefab != null) return;
	    if (RandomCreaturesToSpawn.Count <= 0)
	    {
			__instance.m_creaturePrefab = ZNetScene.instance.GetPrefab("Skeleton");
	    }
	    else
	    {
		    var creature = RandomCreaturesToSpawn[UnityEngine.Random.Range(0, RandomCreaturesToSpawn.Count)];
		    if (Helpers.GetPrefab(creature) is not { } prefab || !prefab.GetComponent<Character>())
		    {
			    __instance.m_creaturePrefab = ZNetScene.instance.GetPrefab("Skeleton");
		    }
		    else
		    {
			    __instance.m_creaturePrefab = prefab;
		    }
	    }
    }

    internal static void Patch_SetupLocations(ZoneSystem __instance)
    {
	    foreach (CustomLocation location in CustomLocation.locations.Values)
	    {
		    ZoneSystem.ZoneLocation data = location.GetLocation();
		    if (data.m_prefab.IsValid)
		    {
			    __instance.m_locations.Add(data);
			    logger.LogDebug("[Location Manager] registered custom location: " + data.m_prefabName);
		    }
		    else
		    {
			    logger.LogWarning("[Location Manager] " + data.m_prefabName + " is not valid");
		    }
	    }
    }

    internal static bool Patch_Location_Awake(Location __instance)
    {
	    if (!CustomLocation.locations.TryGetValue(Helpers.GetNormalizedName(__instance.name), out CustomLocation? data)) return true;
	    Location.s_allLocations.Add(__instance);
	    if (!__instance.m_hasInterior || !data.InteriorEnvironment.Enabled) return false;
	    Vector3 zoneCenter = __instance.GetZoneCenter();
	    GameObject environment = Object.Instantiate(__instance.m_interiorPrefab, new Vector3(zoneCenter.x, __instance.transform.position.y + data.InteriorEnvironment.Altitude, zoneCenter.z), Quaternion.identity, __instance.transform);
	    environment.transform.localScale = data.InteriorEnvironment.Scale;
	    environment.GetComponent<EnvZone>().m_environment = data.InteriorEnvironment.Environment;

	    return false;
    }

    internal static void Patch_DungeonDB_Awake(DungeonDB __instance)
    {
	    foreach (Dungeon dungeon in Dungeon.dungeons.Values)
	    {
		    if (__instance.m_roomLists.Contains(dungeon.Prefab)) continue;
		    __instance.m_roomLists.Add(dungeon.Prefab);
	    }
    }
    
    internal static void Patch_DungeonDB_GenerateHashList(DungeonDB __instance)
    {
	    foreach (KeyValuePair<string, RoomReference> kvp in RoomReference.rooms)
	    {
		    DungeonDB.RoomData data = kvp.Value.Data;
		    if (!__instance.m_roomByHash.ContainsKey(data.Hash)) __instance.m_roomByHash[data.Hash] = data;
	    }
    }

    internal static bool Patch_DungeonGenerator_Spawn(DungeonGenerator __instance)
    {
	    if (!Dungeon.dungeons.TryGetValue(Helpers.GetNormalizedName(__instance.name), out Dungeon dungeon)) return true;
	    DungeonGenerator.m_availableRooms.Clear();
	    foreach (RoomReference room in dungeon.Rooms.list)
	    {
		    DungeonGenerator.m_availableRooms.Add(room.Data);
	    }
	    return false;
    }

	private static Dictionary<string, Sprite>? _inGameOptions;

	private static Dictionary<string, Sprite> InGameOptions
	{
		get
		{
			if (_inGameOptions != null) return _inGameOptions;
			if (Minimap.instance == null) return new();
			_inGameOptions = new Dictionary<string, Sprite>();
			foreach (var icon in Minimap.instance.m_icons)
			{
				_inGameOptions[icon.m_name.ToString()] = icon.m_icon;
			}

			foreach (var icon in Minimap.instance.m_locationIcons)
			{
				_inGameOptions[icon.m_name] = icon.m_icon;
			}

			return _inGameOptions;
		}
	}
	
    internal static void Patch_Minimap_Awake(Minimap __instance)
    {
	    foreach (CustomLocation? location in CustomLocation.locations.Values)
	    {
		    if (location.Icon.GetSprite() is not {} sprite) continue;
		    __instance.m_locationIcons.Add(new Minimap.LocationSpriteData()
		    {
			    m_name = location.Prefab.name,
			    m_icon = sprite
		    });
	    }
    }

    [PublicAPI]
    public class Dungeon
    {
	    internal static Dictionary<string, Dungeon> dungeons = new ();
	    
	    public readonly GameObject Prefab;
	    public readonly GameObject Generator;
	    public readonly RoomReferences Rooms = new();
	    private readonly RoomList List;
	    
	    public Dungeon(string assetBundleName, string prefabName, string generatorName) : this(AssetBundleManager.GetAssetBundle(assetBundleName), prefabName, generatorName){}

	    public Dungeon(AssetBundle bundle, string prefabName, string generatorName) : this(bundle.LoadAsset<GameObject>(prefabName), bundle.LoadAsset<GameObject>(generatorName)){}

	    public Dungeon(GameObject prefab, GameObject generator)
	    {
		    Prefab = prefab;
		    Generator = generator;
		    List = Prefab.GetComponent<RoomList>();
		    dungeons[generator.name] = this;
		    PrefabManager.RegisterPrefab(generator);
	    }

	    [PublicAPI]
	    public class RoomReferences
	    {
		    internal static List<RoomReferences> roomReferences = new();
		    internal static Dictionary<string, RoomReference> registeredRooms = new ();
		    internal readonly List<RoomReference> list = new();
		    
		    public void Add(string assetBundleName, string name)
		    {
			    if (registeredRooms.TryGetValue(name, out RoomReference? reference))
			    {
				    list.Add(reference);
			    }
			    else
			    {
				    if (AssetBundleManager.LoadAsset<GameObject>(assetBundleName, name) is not { } prefab)
				    {
					    logger.LogWarning($"[Location Manager] GameObject {name} not found");
					    return;
				    }
				    reference = new RoomReference(prefab);
				    registeredRooms[name] = reference;
				    list.Add(reference);
				    foreach (ZNetView? child in prefab.GetComponentsInChildren<ZNetView>(true))
				    {
					    new ZNetViewPrefab(child.gameObject, prefab.name);
				    }
			    }
		    }

		    public void Add(string assetBundleName, string firstName, params string[] otherNames)
		    {
			    Add(assetBundleName, firstName);
			    foreach (var name in otherNames) Add(assetBundleName, name);
		    }
	    }
    }

    [PublicAPI]
    public class RoomReference
    {
	    internal static Dictionary<string, RoomReference> rooms = new Dictionary<string, RoomReference>();
	    
	    private readonly AssetID AssetID;
	    public DungeonDB.RoomData? _data;

	    public DungeonDB.RoomData Data
	    {
		    get
		    {
			    if (_data != null) return _data;
			    _data = new  DungeonDB.RoomData()
			    {
				    m_prefab = GetReference(),
				    m_enabled = true,
				    m_theme = Room.Theme.None
			    };
			    return _data;
		    }
	    }
	    
	    public SoftReference<GameObject> GetReference() => SoftAssetLoader.GetSoftReference(AssetID);

	    public RoomReference(GameObject prefab)
	    {
		    AssetID = SoftAssetLoader.AddAsset(prefab);
		    rooms[prefab.name] = this;
		    if (prefab.GetComponent<ZNetView>())
		    {
			    PrefabManager.RegisterPrefab(prefab);
		    }
	    }
    }

    internal class ZNetViewPrefab
    {
		public readonly GameObject Prefab;

		public string Name => Prefab.name;
		public readonly string ParentName;

		public ZNetViewPrefab(GameObject prefab, string parentName)
		{
			Prefab = prefab;
			ParentName = parentName;
			if (PrefabManager.MissingPrefabs.ContainsKey(prefab.name)) return;
            PrefabManager.MissingPrefabs[prefab.name] = this;
		}
    }
    
    [PublicAPI]
    public class CustomLocation
    {
	    internal static Dictionary<string, CustomLocation> locations = new ();
	    
	    public readonly GameObject Prefab;
	    private readonly AssetID AssetID;
	    public Heightmap.Biome Biome = Heightmap.Biome.None;
	    public Heightmap.BiomeArea BiomeArea = Heightmap.BiomeArea.Everything;
	    public GroupSettings Group = new();
	    public PlacementSettings Placement = new();
	    public IconSettings Icon = new();
	    public InteriorEnvironmentSettings InteriorEnvironment = new();
	    public Configs configs = new();

	    public CustomLocation(string assetBundleName, string prefabName) : this(AssetBundleManager.GetAssetBundle(assetBundleName), prefabName){}
	    
	    public CustomLocation(AssetBundle assetBundle, string prefabName) : this(assetBundle.LoadAsset<GameObject>(prefabName)){}

	    public CustomLocation(GameObject prefab)
	    {
		    Prefab = prefab;
		    Load();
		    AssetID = SoftAssetLoader.AddAsset(prefab);
		    locations[prefab.name] = this;
	    }

	    internal void Setup()
	    {
		    configs.Enabled = config(Prefab.name, "Enabled", Toggle.On, $"If on, {Prefab.name} will load");
	    }
	    
	    private void Load()
	    {
		    if (!Prefab.TryGetComponent(out Location component)) return;
		    Placement.ClearArea = component.m_clearArea;
		    Placement.ExteriorRadius = component.m_exteriorRadius;
		    Placement.InteriorRadius =  component.m_interiorRadius;
		    foreach (var child in component.GetComponentsInChildren<ZNetView>(true))
		    {
			    new ZNetViewPrefab(child.gameObject, component.name);
		    }
	    }
	    
		internal ZoneSystem.ZoneLocation? registeredLocation;
		
	    internal ZoneSystem.ZoneLocation GetLocation()
	    {
		    var data = new ZoneSystem.ZoneLocation()
		    {
				m_enable = configs.Enabled!.Value is Toggle.On,
                m_prefabName = Prefab.name,
                m_prefab = SoftAssetLoader.GetSoftReference(AssetID),
                m_biome = Biome,
                m_biomeArea = BiomeArea,
                m_quantity = Placement.Quantity,
                m_prioritized = Placement.Prioritized,
                m_centerFirst =Placement.CenterFirst,
                m_unique = Placement.Unique,
                m_group = Group.Name,
                m_groupMax = Group.MaxName,
                m_minDistanceFromSimilar = Placement.DistanceFromSimilar.Min,
                m_maxDistanceFromSimilar = Placement.DistanceFromSimilar.Max,
                m_iconAlways = Icon.Always,
                m_iconPlaced = Icon.Enabled,
                m_randomRotation = Placement.RandomRotation,
                m_slopeRotation = Placement.SlopeRotation,
                m_snapToWater = Placement.SnapToWater,
                m_interiorRadius = Placement.InteriorRadius,
                m_exteriorRadius = Placement.ExteriorRadius,
                m_clearArea = Placement.ClearArea,
                m_minTerrainDelta = Placement.TerrainDeltaCheck.Min,
                m_maxTerrainDelta = Placement.TerrainDeltaCheck.Max,
                m_minimumVegetation = Placement.VegetationCheck.Radius.Min,
                m_maximumVegetation = Placement.VegetationCheck.Radius.Max,
                m_surroundCheckVegetation = Placement.VegetationCheck.Check,
                m_surroundCheckDistance = Placement.VegetationCheck.CheckDistance,
                m_surroundCheckLayers = Placement.VegetationCheck.Layers,
                m_surroundBetterThanAverage = Placement.VegetationCheck.BetterThanAverage,
                m_inForest = Placement.InForest,
                m_forestTresholdMin = Placement.ForestThreshold.Min,
                m_forestTresholdMax = Placement.ForestThreshold.Max,
                m_minDistance = Placement.Distance.Min,
                m_maxDistance = Placement.Distance.Max,
                m_minAltitude = Placement.Altitude.Min,
                m_maxAltitude = Placement.Altitude.Max,
                m_foldout = Placement.Foldout,
		    };

			registeredLocation = data;
		    return data;
	    }
    }

    [PublicAPI]
    public class InteriorEnvironmentSettings
    {
	    public float Altitude = 5000f;
	    public Vector3 Scale = new(200f, 500f, 200f);
	    public string Environment = string.Empty;
	    public bool Enabled;
    }

    [PublicAPI]
    public class Configs
    {
	    internal ConfigEntry<Toggle>? Enabled;
    }

    [PublicAPI]
    public class MinMaxSettings
    {
	    public float Min;
	    public float Max;

	    public MinMaxSettings(float min, float max)
	    {
		    Min = min;
		    Max = max;
	    }
    }
    [PublicAPI]
    public class GroupSettings
    {
	    public string Name = "";
		public string MaxName = "";
    }
    [PublicAPI]
    public class PlacementSettings
    {
	    public int Quantity;
		public bool Prioritized;
		public bool CenterFirst;
		public bool Unique;
		public MinMaxSettings DistanceFromSimilar = new(0f, 0f);
		public bool RandomRotation;
		public bool SlopeRotation;
		public bool SnapToWater;
		public float InteriorRadius;
		public float ExteriorRadius = 50f;
		public bool ClearArea;
		public MinMaxSettings TerrainDeltaCheck = new(0f, 100f);
		public VegetationSettings VegetationCheck = new();
		public bool InForest;
		public MinMaxSettings ForestThreshold = new(0f, 1f);
		public MinMaxSettings Distance = new(0f, 10000f);
		public MinMaxSettings Altitude = new(0f, 1000f);
		public bool Foldout;
    }

    [PublicAPI]
    public class VegetationSettings
    {
	    public bool Check;
	    public float CheckDistance;
	    public int Layers = 2;
		public float BetterThanAverage;
	    public MinMaxSettings Radius = new(0f, 1f);
    }

    [PublicAPI]
    public class IconSettings
    {
	    public bool Always;
	    public bool Enabled;
	    public Sprite? Icon;
	    public LocationIcon InGameIcon = LocationIcon.None;

	    internal Sprite? GetSprite()
	    {
		    if (Icon != null) return Icon;
		    if (InGameIcon == LocationIcon.None) return null;
		    return InGameOptions.TryGetValue(InGameIcon.GetInternalName(), out Sprite sprite) ? sprite : null;
	    }

	    [PublicAPI]
	    public enum LocationIcon
	    {
		    [InternalName("None")]None,
		    [InternalName("StartTemple")]StartTemple,
		    [InternalName("Vendor_BlackForest")]Haldor,
		    [InternalName("Hildir_camp")]Hildir,
		    [InternalName("BogWitch_Camp")]BogWitch,
		    [InternalName("Icon 0")]Fire,
		    [InternalName("Icon 1")]House,
		    [InternalName("Icon 2")]Hammer,
		    [InternalName("Icon 3")]Pin,
		    [InternalName("Icon 4")]Portal,
		    [InternalName("Death")]Death,
		    [InternalName("Bed")]Bed,
		    [InternalName("Shout")]Shout,
		    [InternalName("Boss")]Boss,
		    [InternalName("Player")]Player,
		    [InternalName("RandomEvent")]Event,
		    [InternalName("EventArea")]EventArea,
		    [InternalName("Ping")]Ping,
		    [InternalName("Hildir1")]QuestionMark,
	    }
	    
	    internal class InternalName : Attribute
	    {
		    public readonly string internalName;
		    public InternalName(string internalName) => this.internalName = internalName;
	    }
    }
}

public static class Helpers
{
	internal static ZNetScene? _ZNetScene;
	internal static ObjectDB? _ObjectDB;
	
	internal static string GetInternalName(this LocationManager.IconSettings.LocationIcon table)
	{
		Type type = typeof(LocationManager.IconSettings.LocationIcon);
		MemberInfo[] memInfo = type.GetMember(table.ToString());
		if (memInfo.Length <= 0) return table.ToString();
		LocationManager.IconSettings.InternalName? attr = (LocationManager.IconSettings.InternalName)Attribute.GetCustomAttribute(memInfo[0], typeof(LocationManager.IconSettings.InternalName));
		return attr != null ? attr.internalName : table.ToString();
	}
	
	internal static GameObject? GetPrefab(string prefabName)
	{
		if (ZNetScene.instance != null) return ZNetScene.instance.GetPrefab(prefabName);
		if (_ZNetScene == null) return null;
		GameObject? result = _ZNetScene.m_prefabs.Find(prefab => prefab.name == prefabName);
		return result;
	}
	
	internal static string GetNormalizedName(string name) => Regex.Replace(name, @"\s*\(.*?\)", "").Trim();

	internal static bool Exists<T>(this List<T> list, T obj) where T : UnityEngine.Object
	{
		return list.Contains(obj) || list.Exists(x => x.name == obj.name);
	}

	internal static bool Exists(this Dictionary<int, DungeonDB.RoomData> dict, AssetID id)
	{
		return dict.Values.ToList().Exists(room => room.m_prefab.m_assetID == id);
	}

	internal static void Destroy<T>(this GameObject obj) where T : MonoBehaviour
	{
		if (!obj.TryGetComponent(out T component)) return;
		Object.Destroy(component);
	}
}

public static class AssetBundleManager
{
	private static readonly Dictionary<string, AssetBundle> CachedBundles = new();

	public static T? LoadAsset<T>(string assetBundle, string prefab) where T : UnityEngine.Object
	{
		return GetAssetBundle(assetBundle) is not { } bundle ? null : bundle.LoadAsset<T>(prefab);
	}
    
	[PublicAPI]
	public static AssetBundle GetAssetBundle(string fileName)
	{
		if (CachedBundles.TryGetValue(fileName, out var assetBundle)) return assetBundle;
		if (AssetBundle.GetAllLoadedAssetBundles().FirstOrDefault(b => b.name == fileName) is {} existing)
		{
			CachedBundles[fileName] = existing;
			return existing;
		}
		Assembly execAssembly = Assembly.GetExecutingAssembly();
		string resourceName = execAssembly.GetManifestResourceNames().Single(str => str.EndsWith(fileName));
		using Stream? stream = execAssembly.GetManifestResourceStream(resourceName);
		AssetBundle? bundle = AssetBundle.LoadFromStream(stream);
		CachedBundles[fileName] = bundle;
		return bundle;
	}
}

internal enum Toggle
{
	On,
	Off
}

internal class ConfigurationManagerAttributes
{
	[UsedImplicitly] public int? Order;
	[UsedImplicitly] public bool? Browsable;
	[UsedImplicitly] public string? Category;
	[UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer;
}

[PublicAPI]
public class LocalizeKey
{
	public static readonly List<LocalizeKey> keys = new();

	public readonly string Key;
	public readonly Dictionary<string, string> Localizations = new();

	public LocalizeKey(string key)
	{
		Key = key.Replace("$", "");
		keys.Add(this);
	}

	public void Alias(string alias)
	{
		Localizations.Clear();
		if (!alias.Contains("$"))
		{
			alias = $"${alias}";
		}
		Localizations["alias"] = alias;
		Localization.instance.AddWord(Key, Localization.instance.Localize(alias));
	}

	public LocalizeKey English(string key) => addForLang("English", key);
	public LocalizeKey Swedish(string key) => addForLang("Swedish", key);
	public LocalizeKey French(string key) => addForLang("French", key);
	public LocalizeKey Italian(string key) => addForLang("Italian", key);
	public LocalizeKey German(string key) => addForLang("German", key);
	public LocalizeKey Spanish(string key) => addForLang("Spanish", key);
	public LocalizeKey Russian(string key) => addForLang("Russian", key);
	public LocalizeKey Romanian(string key) => addForLang("Romanian", key);
	public LocalizeKey Bulgarian(string key) => addForLang("Bulgarian", key);
	public LocalizeKey Macedonian(string key) => addForLang("Macedonian", key);
	public LocalizeKey Finnish(string key) => addForLang("Finnish", key);
	public LocalizeKey Danish(string key) => addForLang("Danish", key);
	public LocalizeKey Norwegian(string key) => addForLang("Norwegian", key);
	public LocalizeKey Icelandic(string key) => addForLang("Icelandic", key);
	public LocalizeKey Turkish(string key) => addForLang("Turkish", key);
	public LocalizeKey Lithuanian(string key) => addForLang("Lithuanian", key);
	public LocalizeKey Czech(string key) => addForLang("Czech", key);
	public LocalizeKey Hungarian(string key) => addForLang("Hungarian", key);
	public LocalizeKey Slovak(string key) => addForLang("Slovak", key);
	public LocalizeKey Polish(string key) => addForLang("Polish", key);
	public LocalizeKey Dutch(string key) => addForLang("Dutch", key);
	public LocalizeKey Portuguese_European(string key) => addForLang("Portuguese_European", key);
	public LocalizeKey Portuguese_Brazilian(string key) => addForLang("Portuguese_Brazilian", key);
	public LocalizeKey Chinese(string key) => addForLang("Chinese", key);
	public LocalizeKey Japanese(string key) => addForLang("Japanese", key);
	public LocalizeKey Korean(string key) => addForLang("Korean", key);
	public LocalizeKey Hindi(string key) => addForLang("Hindi", key);
	public LocalizeKey Thai(string key) => addForLang("Thai", key);
	public LocalizeKey Abenaki(string key) => addForLang("Abenaki", key);
	public LocalizeKey Croatian(string key) => addForLang("Croatian", key);
	public LocalizeKey Georgian(string key) => addForLang("Georgian", key);
	public LocalizeKey Greek(string key) => addForLang("Greek", key);
	public LocalizeKey Serbian(string key) => addForLang("Serbian", key);
	public LocalizeKey Ukrainian(string key) => addForLang("Ukrainian", key);

	private LocalizeKey addForLang(string lang, string value)
	{
		Localizations[lang] = value;
		if (Localization.instance.GetSelectedLanguage() == lang)
		{
			Localization.instance.AddWord(Key, value);
		}
		else if (lang == "English" && !Localization.instance.m_translations.ContainsKey(Key))
		{
			Localization.instance.AddWord(Key, value);
		}
		return this;
	}

	[HarmonyPriority(Priority.LowerThanNormal)]
	internal static void AddLocalizedKeys(Localization __instance, string language)
	{
		foreach (LocalizeKey key in keys)
		{
			if (key.Localizations.TryGetValue(language, out string Translation) || key.Localizations.TryGetValue("English", out Translation))
			{
				__instance.AddWord(key.Key, Translation);
			}
			else if (key.Localizations.TryGetValue("alias", out string alias))
			{
				__instance.AddWord(key.Key, Localization.instance.Localize(alias));
			}
		}
	}
}

[PublicAPI]
public static class PrefabManager
{
    internal static List<GameObject> PrefabsToRegister = new();
    internal static Dictionary<string, LocationManager.ZNetViewPrefab> MissingPrefabs = new();

    public static List<GameObject> RegisteredMissingPrefabs = new();
    
	public static event Action? OnFinishedRegistering;

    public static void RegisterPrefab(GameObject? prefab)
    {
        if (prefab == null) return;
        PrefabsToRegister.Add(prefab);
    }

    public static void RegisterPrefab(string assetBundleName, string prefabName) => RegisterPrefab(AssetBundleManager.LoadAsset<GameObject>(assetBundleName, prefabName));

    public static void RegisterPrefab(AssetBundle assetBundle, string prefabName) =>  RegisterPrefab(assetBundle.LoadAsset<GameObject>(prefabName));

    [HarmonyPriority(Priority.VeryHigh)]
    internal static void Patch_ZNetScene_Awake(ZNetScene __instance)
    {
        foreach (GameObject prefab in PrefabsToRegister)
        {
            if (!prefab.GetComponent<ZNetView>()) continue;
            if (__instance.m_prefabs.Exists(prefab))
            {
	            LocationManager.logger.LogDebug($"[Location Manager] Prefab {prefab.name} already exists");
            }
            else
                __instance.m_prefabs.Add(prefab);
        }

        foreach (LocationManager.ZNetViewPrefab netViewPrefab in MissingPrefabs.Values)
        {
	        if (!netViewPrefab.Prefab.GetComponent<ZNetView>()) continue;
	        if (__instance.m_prefabs.Exists(netViewPrefab.Prefab)) continue;
	        __instance.m_prefabs.Add(netViewPrefab.Prefab);
	        RegisteredMissingPrefabs.Add(netViewPrefab.Prefab);
	        LocationManager.logger.LogDebug("Registered missing znetview object: " + netViewPrefab.Name + " from: " + netViewPrefab.ParentName);
        }
        
        OnFinishedRegistering?.Invoke();
    }
}

public static class SoftAssetLoader
{
    private static readonly Dictionary<AssetID, AssetRef> m_assets = new();
    private static readonly Dictionary<string, AssetID> m_ids = new();
	
    public static void Patch_AssetBundleLoader_OnInitCompleted(AssetBundleLoader __instance)
    {
	    if (!isReady())
	    {
		    LocationManager.logger.LogWarning("[Location Manager] AssetBundle Loader is not ready");
		    return;
	    }
	    
	    foreach (KeyValuePair<AssetID, AssetRef> kvp in m_assets)
        {
            AddAssetToBundleLoader(__instance, kvp.Key, kvp.Value);
        }
    }

    public static SoftReference<GameObject> GetSoftReference(AssetID assetID)
    {
        return assetID.IsValid ? new SoftReference<GameObject>(assetID) : default;
    }

    public static SoftReference<GameObject>? GetSoftReference(string name)
    {
        AssetID? assetID = GetAssetID(name);
        if (assetID == null) return null;
        return assetID.Value.IsValid ? new SoftReference<GameObject>(assetID.Value) : default;
    }

    private static void AddAssetToBundleLoader(AssetBundleLoader __instance, AssetID assetID, AssetRef assetRef)
    {
        string bundleName = $"RustyMods_{assetRef.asset.name}";
        string bundlePath = $"{assetRef.sourceMod.GUID}/Bundles/{bundleName}";
        string assetPath = $"{assetRef.sourceMod.GUID}/Prefabs/{assetRef.asset.name}";

        if (__instance.m_bundleNameToLoaderIndex.ContainsKey(bundleName))
        {
	        return;
        }
        
        AssetLocation location = new AssetLocation(bundleName, assetPath);
        BundleLoader bundleLoader = new BundleLoader(bundleName, bundlePath);
        bundleLoader.HoldReference();
        __instance.m_bundleNameToLoaderIndex[bundleName] = __instance.m_bundleLoaders.Length;
        __instance.m_bundleLoaders = __instance.m_bundleLoaders.AddItem(bundleLoader).ToArray();

        int originalBundleLoaderIndex = __instance.m_assetLoaders
            .FirstOrDefault(l => l.m_assetID == assetID).m_bundleLoaderIndex;

        if (assetID.IsValid && originalBundleLoaderIndex > 0)
        {
            BundleLoader originalBundleLoader = __instance.m_bundleLoaders[originalBundleLoaderIndex];

            bundleLoader.m_bundleLoaderIndicesOfThisAndDependencies = originalBundleLoader.m_bundleLoaderIndicesOfThisAndDependencies
                .Where(i => i != originalBundleLoaderIndex)
                .AddItem(__instance.m_bundleNameToLoaderIndex[bundleName])
                .OrderBy(i => i)
                .ToArray();
        }
        else
        {
            bundleLoader.SetDependencies(Array.Empty<string>());
        }

        __instance.m_bundleLoaders[__instance.m_bundleNameToLoaderIndex[bundleName]] = bundleLoader;

        AssetLoader loader = new AssetLoader(assetID, location)
        {
            m_asset = assetRef.asset
        };
        loader.HoldReference();
        __instance.m_assetIDToLoaderIndex[assetID] = __instance.m_assetLoaders.Length;
        __instance.m_assetLoaders = __instance.m_assetLoaders.AddItem(loader).ToArray();

        m_ids[assetRef.asset.name] = assetID;
    }

    private static AssetID? GetAssetID(string name) => m_ids.TryGetValue(name, out AssetID id) ? id : null;
	
    public static AssetID AddAsset(UnityEngine.Object asset)
    {
        AssetID assetID = GenerateID(asset);
        AssetRef assetRef = new(LocationManager.plugin.Info.Metadata, asset, assetID);
        m_assets[assetID] = assetRef;
        return assetID;
    }

    private static AssetID GenerateID(UnityEngine.Object asset)
    {
        uint u = (uint)asset.name.GetStableHashCode();
        return new AssetID(u, u, u, u);
    }

    private static bool isReady()
    {
        return Runtime.s_assetLoader != null && ((AssetBundleLoader)Runtime.s_assetLoader).Initialized;
    }
}

public struct AssetRef
{
    public readonly BepInPlugin sourceMod;
    public readonly UnityEngine.Object asset;
    public AssetID originalID;

    public AssetRef(BepInPlugin sourceMod, UnityEngine.Object asset, AssetID assetID)
    {
        this.sourceMod = sourceMod;
        this.asset = asset;
        this.originalID = assetID;
    }
}