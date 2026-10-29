using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Drone.Engine;

/// <summary>
/// Every tunable number in the game. The values live in settings/drone.json; this is only its typed
/// view. Every property is required, so a missing key fails loudly, and unknown keys fail too, so a
/// typo in a sweep never silently runs the defaults.
/// Times are in seconds (the engine turns them into 60 ticks a second). "Per 44 plots" numbers scale
/// with the map: the original's 5 x 9 map has 44 claimable plots.
/// </summary>
public sealed class Settings
{
    public required GameSettings Game { get; init; }
    public required MapSettings Map { get; init; }
    public required StartSettings Start { get; init; }
    public required StoreSettings Store { get; init; }
    public required GoodsSettings Goods { get; init; }
    public required LandSettings Land { get; init; }
    public required TurnSettings Turn { get; init; }
    public required ProductionSettings Production { get; init; }
    public required PersonalEventSettings PersonalEvents { get; init; }
    public required RoundEventSettings RoundEvents { get; init; }
    public required AuctionSettings Auction { get; init; }
    public required ScoringSettings Scoring { get; init; }
    public required Dictionary<string, SpeciesSettings> Species { get; init; }
    public required CpuSettings Cpu { get; init; }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Loads a settings file, then applies each variant file (a partial JSON object merged
    /// over it) and each "path=value" override, in order.</summary>
    public static Settings Load(string path, IEnumerable<string>? variantFiles = null, IEnumerable<string>? overrides = null)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        foreach (var v in variantFiles ?? [])
        {
            var patch = JsonNode.Parse(File.ReadAllText(v), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
            Merge(root, patch, Path.GetFileName(v));
        }
        foreach (var o in overrides ?? []) Override(root, o);
        return FromJson(root);
    }

    /// <summary>The checked-in defaults (settings/drone.json), found by walking up from the app.</summary>
    public static Settings LoadDefault(IEnumerable<string>? variants = null, IEnumerable<string>? overrides = null)
        => Load(DefaultPath(), (variants ?? []).Select(VariantPath), overrides);

    public static string DefaultPath() => Path.Combine(SettingsDir(), "drone.json");

    /// <summary>A variant by name (settings/variants/NAME.json) or by path.</summary>
    public static string VariantPath(string nameOrPath)
        => File.Exists(nameOrPath) ? nameOrPath : Path.Combine(SettingsDir(), "variants", nameOrPath + ".json");

    static string SettingsDir()
    {
        var env = Environment.GetEnvironmentVariable("DRONE_SETTINGS_DIR");
        if (!string.IsNullOrEmpty(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "settings", "drone.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
        }
        throw new FileNotFoundException("settings/drone.json not found above " + AppContext.BaseDirectory + " (or set DRONE_SETTINGS_DIR)");
    }

    public static Settings FromJson(JsonObject root) => root.Deserialize<Settings>(Json)
        ?? throw new InvalidDataException("empty settings");

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Json)!.AsObject();

    /// <summary>A deep copy with overrides applied ("land.storeStartPrice=200").</summary>
    public Settings With(params string[] overrides)
    {
        var root = ToJson();
        foreach (var o in overrides) Override(root, o);
        return FromJson(root);
    }

    static void Merge(JsonObject target, JsonObject patch, string where)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (!target.ContainsKey(key)) throw new InvalidDataException($"{where}: unknown setting '{key}'");
            if (value is JsonObject po && target[key] is JsonObject to) Merge(to, po, where);
            else target[key] = value?.DeepClone();
        }
    }

    /// <summary>"a.b.c=value": value is JSON (a number, true, a list) or else a plain string.</summary>
    static void Override(JsonObject root, string assignment)
    {
        int eq = assignment.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"override '{assignment}' is not path=value");
        var parts = assignment[..eq].Trim().Split('.');
        string raw = assignment[(eq + 1)..].Trim();
        JsonNode? value;
        try { value = JsonNode.Parse(raw); } catch (JsonException) { value = JsonValue.Create(raw); }
        JsonNode node = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            node = Step(node, parts[i], assignment) ?? throw new ArgumentException($"override '{assignment}': '{parts[i]}' not found");
        }
        string last = parts[^1];
        if (node is JsonObject obj)
        {
            if (!obj.ContainsKey(last)) throw new ArgumentException($"override '{assignment}': unknown setting '{last}'");
            obj[last] = value;
        }
        else if (node is JsonArray arr && int.TryParse(last, out int idx) && idx >= 0 && idx < arr.Count) arr[idx] = value;
        else throw new ArgumentException($"override '{assignment}': cannot set '{last}'");
    }

    static JsonNode? Step(JsonNode node, string part, string assignment) => node switch
    {
        JsonObject o => o[part],
        JsonArray a when int.TryParse(part, out int i) && i >= 0 && i < a.Count => a[i],
        _ => throw new ArgumentException($"override '{assignment}': cannot step into '{part}'"),
    };
}

public sealed class GameSettings
{
    public required int Players { get; init; }
    /// <summary>Round counts the host can pick (12/18/24).</summary>
    public required int[] RoundChoices { get; init; }
    public required int TicksPerSecond { get; init; }
    /// <summary>How rules written for 12 rounds read in longer games: "stretch" maps the round onto
    /// 1..12 (bands get longer), "absolute" uses the round as is (amounts keep growing).</summary>
    public required RoundScaling RoundScaling { get; init; }
    /// <summary>Food need bands, by thirds of the game: 3 / 4 / 5.</summary>
    public required int[] FoodNeedByThird { get; init; }
    /// <summary>Enables the second export good (Helion ice). Off = the original's one export.</summary>
    public required bool HelionEnabled { get; init; }
    /// <summary>Enables species abilities. Off = everyone plays the plain rules.</summary>
    public required bool SpeciesEnabled { get; init; }
    /// <summary>Land auction mode: live (default) or sealed second-price (host option).</summary>
    public required LandAuctionMode LandAuctionMode { get; init; }
}

public enum RoundScaling { Stretch, Absolute }
public enum LandAuctionMode { Live, Sealed }

public sealed class MapSize
{
    public required int Rounds { get; init; }
    public required int Cols { get; init; }
    public required int Rows { get; init; }
    public required int CrystiteSeeds { get; init; }
    public required int HelionSeeds { get; init; }
    public required int Lakes { get; init; }
    public required int Outposts { get; init; }
}

public sealed class MapSettings
{
    /// <summary>Sizes by game length. Columns must be odd so the river runs down the centre.</summary>
    public required MapSize[] Sizes { get; init; }
    /// <summary>Mountains per non-river plot in a row (original 2 in 8).</summary>
    public required double MountainShare { get; init; }
    /// <summary>Chance of a mountain having 1, 2 or 3 ore units (original splits 1+3, 2+2, 3+1).</summary>
    public required double[] MountainUnitWeights { get; init; }
    /// <summary>Crystite and Helion seeds start at this level and drop 1 per hex of distance.</summary>
    public required int SeedLevel { get; init; }
    /// <summary>Deposit levels drop this much per hex away from a seed.</summary>
    public required int SeedDropPerHex { get; init; }
    /// <summary>Deposits reach this many hexes from their seed (high at the seed, then lower).</summary>
    public required int SeedRadius { get; init; }
    /// <summary>Helion seeds keep at least this hex distance from crystite seeds (3 = the seed plots
    /// and their neighbours never overlap, the outer rings may).</summary>
    public required int HelionSeedMinDistance { get; init; }
    /// <summary>Lakes keep this distance from the river, the store and each other.</summary>
    public required int LakeMinDistance { get; init; }
    /// <summary>Outpost rows as a share of the map height (top and bottom of the river).</summary>
    public required double[] OutpostRowShares { get; init; }
}

public sealed class StartSettings
{
    public required int Cash { get; init; }
    /// <summary>CPUs start with this, ignoring species cash changes (A4).</summary>
    public required int CpuCash { get; init; }
    public required int Food { get; init; }
    public required int Energy { get; init; }
}

public sealed class StoreSettings
{
    public required int Food { get; init; }
    public required int Energy { get; init; }
    public required int Ore { get; init; }
    /// <summary>Drone stock at the start and the store's cap, per 44 plots (original 14).</summary>
    public required double DronesPer44Plots { get; init; }
    /// <summary>Turn order flips when the store holds fewer drones than this, per 44 plots (original 8).</summary>
    public required double TurnFlipDronesPer44Plots { get; init; }
    public required int OrePerDrone { get; init; }
    /// <summary>Drone price = floor(ore price / divisor) x multiplier (original /5 x 10).</summary>
    public required int DronePriceDivisor { get; init; }
    public required int DronePriceMultiplier { get; init; }
    public required int StartDronePrice { get; init; }
}

public sealed class GoodSettings
{
    public required int StartPrice { get; init; }
    public required int OutfitCost { get; init; }
    /// <summary>Share of held stock lost before each auction (after usage).</summary>
    public required double Spoilage { get; init; }
    /// <summary>Held stock is capped here before the auction (0 = no cap).</summary>
    public required int HoldCap { get; init; }
    public required int MaxHeld { get; init; }
    /// <summary>Price step of one auction scale line.</summary>
    public required int AuctionStep { get; init; }
    /// <summary>Lines from the start price down to the bottom of the auction scale.</summary>
    public required int ScaleBelow { get; init; }
    /// <summary>Lines from the bottom to the top of the scale (the store's sell line).</summary>
    public required int ScaleLines { get; init; }
    /// <summary>Minimum and maximum price after the price formula.</summary>
    public required int MinPrice { get; init; }
    public required int MaxPrice { get; init; }
    /// <summary>Exports: a fresh random price each round in [RandomMin, RandomMax].</summary>
    public required int RandomMin { get; init; }
    public required int RandomMax { get; init; }
}

public sealed class GoodsSettings
{
    public required GoodSettings Food { get; init; }
    public required GoodSettings Energy { get; init; }
    public required GoodSettings Ore { get; init; }
    public required GoodSettings Crystite { get; init; }
    public required GoodSettings Helion { get; init; }
    /// <summary>Goods auction order (C2).</summary>
    public required Good[] AuctionOrder { get; init; }
    /// <summary>Food/energy price: ratio = floor(RatioScale x demand / supply), capped at RatioCap
    /// (the original wrapped past 255; DRONE caps, L3).</summary>
    public required int RatioScale { get; init; }
    public required int RatioCap { get; init; }
    /// <summary>Food demand = this x the sum of the players' food needs next round.</summary>
    public required double FoodDemandPerNeed { get; init; }
    /// <summary>Energy demand = total energy need + this.</summary>
    public required int EnergyDemandExtra { get; init; }
    /// <summary>Ore price ratio clamp (original 2..24) and the random swing (7 x N(0,1)).</summary>
    public required int OreRatioMin { get; init; }
    public required int OreRatioMax { get; init; }
    public required int OreSwing { get; init; }
    /// <summary>Ore demand = min(free plots, OreDemandFreePlots) + owned plots with no drone, capped
    /// at OreDemandCap (both per 44 plots).</summary>
    public required double OreDemandFreePlots { get; init; }
    public required double OreDemandCap { get; init; }
    public required int OreFloorAfterSwing { get; init; }
    /// <summary>New price after an auction: weight of the trade average against the formula price
    /// (1 = plain trade average, as the original; I14 tests blends).</summary>
    public required double TradeAverageWeight { get; init; }
}

public sealed class LandSettings
{
    /// <summary>Store plots each round: round(BasePer44Plots x plots/44) + round(N(0,1) x Spread).</summary>
    public required double StorePlotsPer44Plots { get; init; }
    public required double StorePlotsSpread { get; init; }
    public required int StoreStartPrice { get; init; }
    /// <summary>Next start: sold at P gives P - SoldDrop; unsold at S gives S/2 - UnsoldDrop; floor MinPrice.</summary>
    public required int SoldDrop { get; init; }
    public required int UnsoldDrop { get; init; }
    public required int MinPrice { get; init; }
    /// <summary>L4 variant: the next round starts from the average store sale price this round.</summary>
    public required bool UseAveragePrice { get; init; }
    /// <summary>Unsold store plot removes only the next plot from this round's sale (E6).</summary>
    public required int SkipAfterUnsold { get; init; }
    /// <summary>Bid steps grow with price: Steps[i] applies from Thresholds[i] up (E8).</summary>
    public required int[] BidStepThresholds { get; init; }
    public required int[] BidSteps { get; init; }
    /// <summary>Bidders move one step every this many seconds while pushing.</summary>
    public required double BidStepSeconds { get; init; }
    /// <summary>Bidders start this far below the start price (the bottom line).</summary>
    public required int BottomBelowStart { get; init; }
    /// <summary>Auction clock: drains over SlowSeconds while anyone moves, FastSeconds while nobody does.</summary>
    public required double TimerSlowSeconds { get; init; }
    public required double TimerFastSeconds { get; init; }
    /// <summary>Player sale: the seller moves the price in these steps (E3).</summary>
    public required int SellerStep { get; init; }
    /// <summary>Quick sale to the store: share of the store's land start price, plus any drone back.</summary>
    public required double QuickSaleShare { get; init; }
    /// <summary>Draft: seconds to hand in a ranking before random free plots are ranked for you.</summary>
    public required double DraftSeconds { get; init; }
    /// <summary>Sealed auction: seconds to hand in a bid.</summary>
    public required double SealedSeconds { get; init; }
}

public sealed class TurnSettings
{
    /// <summary>A fed player's turn (original 101 time pixels x 28 jiffies = 47 s).</summary>
    public required double Seconds { get; init; }
    /// <summary>Share of the turn that scales with food (original 91 of 101).</summary>
    public required double FoodScaledShare { get; init; }
    /// <summary>Food scaling never takes the scaled part below this share (F3).</summary>
    public required double FoodFloor { get; init; }
    /// <summary>Walking one hex, by terrain.</summary>
    public required double WalkPlainSeconds { get; init; }
    public required double WalkMountainSeconds { get; init; }
    public required double WalkWaterSeconds { get; init; }
    /// <summary>Going from the map into town (or an outpost) and back out.</summary>
    public required double EnterTownSeconds { get; init; }
    public required double ExitTownSeconds { get; init; }
    public required double CorralSeconds { get; init; }
    public required double OutfitSeconds { get; init; }
    public required double AssayOfficeSeconds { get; init; }
    public required double SampleSeconds { get; init; }
    public required double PubSeconds { get; init; }
    public required double InstallSeconds { get; init; }
    /// <summary>Marking a plot for sale or quick-selling it from the menu (E10).</summary>
    public required double MarkForSaleSeconds { get; init; }
    /// <summary>Pub: random[0, PerSecondLeft x seconds left) + round bonus, capped.</summary>
    public required double PubPerSecondLeft { get; init; }
    public required int[] PubRoundBonus { get; init; }
    public required int PubCap { get; init; }
    public required int PubCapTo { get; init; }
    /// <summary>The Glimmer: shows for ShowSeconds[min,max], hides for HideSeconds[min,max], moves on
    /// MoveChance when hiding. Reward by quarter band.</summary>
    public required double[] GlimmerShowSeconds { get; init; }
    public required double[] GlimmerHideSeconds { get; init; }
    public required double GlimmerMoveChance { get; init; }
    public required int[] GlimmerReward { get; init; }
    /// <summary>Drifting supply crate: seconds to cross one hex, chance of cash vs goods, amounts by band.</summary>
    public required double CrateHexSeconds { get; init; }
    public required double CrateCashChance { get; init; }
    public required int[] CrateCash { get; init; }
    public required int[] CrateGoods { get; init; }
}

public sealed class ProductionSettings
{
    /// <summary>Base output [food, energy, ore]; ore on a mountain is units x OrePerUnit + MountainOreBase.</summary>
    public required int[] Plain { get; init; }
    public required int[] Mountain { get; init; }
    public required int MountainOreBase { get; init; }
    public required int OrePerUnit { get; init; }
    public required int[] Water { get; init; }
    /// <summary>Plains touching the river or a lake make this much more food.</summary>
    public required int BankFoodBonus { get; init; }
    /// <summary>Economies of scale: +ScaleBonus per ScaleStep drones on the same good.</summary>
    public required int ScaleStep { get; init; }
    public required int ScaleBonus { get; init; }
    /// <summary>Learning curve: +LearningBonus per neighbouring own plot with the same good, up to
    /// LearningCap neighbours.</summary>
    public required int LearningCap { get; init; }
    public required int LearningBonus { get; init; }
    /// <summary>A plot switched from another job to mining makes 1/this of its output that round.</summary>
    public required int MiningSwitchDivisor { get; init; }
    /// <summary>Luck: + round(N(0,1) x Spread).</summary>
    public required double LuckSpread { get; init; }
    public required int MaxOutput { get; init; }
    /// <summary>Energy each non-energy drone uses.</summary>
    public required double EnergyPerDrone { get; init; }
}

public sealed class PersonalEvent
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required PersonalEventKind Kind { get; init; }
    /// <summary>Multiplier: amount = Q x Unit x (band + 1); goods events use Q as units.</summary>
    public required int Q { get; init; }
    public required bool NeedsDrone { get; init; }
}

public enum PersonalEventKind { CarePackage, OreGift, FoodHalved, Cash, PerOreExportDrone, PerEnergyDrone, PerFoodDrone, LosePlot, GainPlot }

public sealed class PersonalEventSettings
{
    public required double ChancePerTurn { get; init; }
    public required int Unit { get; init; }
    /// <summary>Amounts grow by one step every this many (12-round) rounds.</summary>
    public required int RoundsPerStep { get; init; }
    public required int CareFood { get; init; }
    public required int CareEnergy { get; init; }
    /// <summary>A non-leader holding less food than this gets the care package (F13: 1, i.e. none left).</summary>
    public required int CareBelowFood { get; init; }
    public required int OreGift { get; init; }
    public required PersonalEvent[] Events { get; init; }
}

public sealed class RoundEventSettings
{
    /// <summary>Cards per 11 event rounds; the deck grows with the game.</summary>
    public required Dictionary<RoundEventKind, int> Cards { get; init; }
    public required int PestPlotsPer44 { get; init; }
    public required int RadiationDronesPer44 { get; init; }
    public required int AcidRadius { get; init; }
    public required int AcidFoodIn { get; init; }
    public required int AcidEnergyIn { get; init; }
    public required int AcidFoodOut { get; init; }
    public required int AcidEnergyOut { get; init; }
    public required double QuakeShiftChance { get; init; }
    public required int SunspotEnergy { get; init; }
    public required int MeteorLevel { get; init; }
    public required int MeteorMaxBefore { get; init; }
    public required double IceRaidersShare { get; init; }
    public required int SupplyShipFood { get; init; }
    public required int SupplyShipEnergy { get; init; }
}

public enum RoundEventKind { Pest, Pirates, AcidRain, Quake, Sunspots, Meteor, Radiation, StoreFire, IceRaiders, Comet, SupplyShip, Malfunction }

public sealed class AuctionSettings
{
    /// <summary>Goods auction clock: SlowSeconds while anyone moves, FastSeconds while nobody does;
    /// it stops while units change hands.</summary>
    public required double TimerSlowSeconds { get; init; }
    public required double TimerFastSeconds { get; init; }
    /// <summary>Seconds to switch side before trading starts.</summary>
    public required double SideSeconds { get; init; }
    /// <summary>The energy need line sits this far above the need (the original drew it at need + 1).</summary>
    public required int EnergyLineExtra { get; init; }
    /// <summary>Players move one scale line every this many seconds.</summary>
    public required double MoveSeconds { get; init; }
    /// <summary>The auction speed species compare against (AuctionLines per this many lines).</summary>
    public required int SpeciesMoveBase { get; init; }
    /// <summary>First unit of a trade takes FirstUnitSeconds, each next one is quicker down to MinUnitSeconds.</summary>
    public required double FirstUnitSeconds { get; init; }
    public required double MinUnitSeconds { get; init; }
    public required double UnitSpeedUp { get; init; }
}

public sealed class ScoringSettings
{
    public required int PlotValue { get; init; }
    /// <summary>Colony verdicts: rating = floor(colony / (Step x plots/44 x rounds/12)); a rating of
    /// FounderRating or more gives the First Founder.</summary>
    public required int ColonyStep { get; init; }
    public required int ColonyMinimum { get; init; }
    public required int MaxRating { get; init; }
    public required int FounderRating { get; init; }
    public required string[] Verdicts { get; init; }
}

/// <summary>A species' ability and its cost, all in whole amounts (Marcus, 2026-10-28).</summary>
public sealed class SpeciesSettings
{
    public required string Ability { get; init; }
    /// <summary>Walking speed in percent (100 = normal; 125 walks 25% faster).</summary>
    public required int WalkSpeedPercent { get; init; }
    public required int ExtraFoodNeed { get; init; }
    /// <summary>Auction lines moved in the time others move Auction.SpeciesMoveBase (3).</summary>
    public required int AuctionLines { get; init; }
    public required int CashDelta { get; init; }
    /// <summary>Assays take 1/AssayDivisor of the time.</summary>
    public required int AssayDivisor { get; init; }
    /// <summary>Food plots making FoodLossFrom or more make FoodLoss less.</summary>
    public required int FoodLoss { get; init; }
    public required int FoodLossFrom { get; init; }
    /// <summary>FreeEnergy units free for every FreeEnergyEvery drones that use energy (0 = none).</summary>
    public required int FreeEnergyEvery { get; init; }
    public required int FreeEnergy { get; init; }
    /// <summary>Dollars added to each drone this species buys.</summary>
    public required int DroneSurcharge { get; init; }
    /// <summary>Personal events in money or goods are divided by these (bad, good).</summary>
    public required int BadEventDivisor { get; init; }
    public required int GoodEventDivisor { get; init; }
    public required bool DraftFirst { get; init; }
    /// <summary>Dollars added to the price of each plot won at auction.</summary>
    public required int LandSurcharge { get; init; }
}

public sealed class CpuSettings
{
    /// <summary>Seconds a CPU keeps back before starting another job (original 35 time pixels).</summary>
    public required double MinJobSeconds { get; init; }
    public required double MinAssaySeconds { get; init; }
    public required int MinJobGain { get; init; }
    /// <summary>When above 0, a job must instead add this many dollars a round.</summary>
    public required int MinJobValue { get; init; }
    /// <summary>Energy auction: a CPU buys for its drones plus one of these many new drones (picked at
    /// random), and with CornerChance also buys out whatever energy the store has left.</summary>
    public required int[] EnergyPlanDrones { get; init; }
    public required double EnergyCornerChance { get; init; }
    /// <summary>Dollars a CPU knocks off a plot's draft value per hex from town.</summary>
    public required double DraftDistanceCost { get; init; }
    /// <summary>Unit value bonuses when short of food or energy.</summary>
    public required int ShortBonus { get; init; }
    public required int NoneYetBonus { get; init; }
    /// <summary>Land: cash a CPU keeps after buying: a drone and outfit for the plot + Reserve +
    /// random[0, ReserveRandom). With OverreachChance it keeps only half the random part.</summary>
    public required int LandReserve { get; init; }
    public required int LandReserveRandom { get; init; }
    public required double LandOverreachChance { get; init; }
    /// <summary>Plot worth = value per round x rounds left x this.</summary>
    public required double LandHorizonShare { get; init; }
    /// <summary>Goods: cash kept back = drone price + this.</summary>
    public required int GoodsReserveExtra { get; init; }
    /// <summary>How far above / below the start price a CPU will go, as shares.</summary>
    public required double BuyerMaxOver { get; init; }
    public required double SellerMinUnder { get; init; }
    /// <summary>Share of the auction clock before a CPU reaches its limit price.</summary>
    public required double ConcedeShare { get; init; }
    /// <summary>Hunt the Glimmer / supply crate when it pays better than the pub.</summary>
    public required bool HuntSideRewards { get; init; }
    /// <summary>Holding exports and ore: an export priced in the bottom ExportHoardBelow of its band
    /// is held with ExportHoardChance, ore below OreSellAllFrom always; a held good is sold only to
    /// bring cash up to SellToCash (and above its hold cap).</summary>
    public required double ExportHoardBelow { get; init; }
    public required double ExportHoardChance { get; init; }
    public required int OreSellAllFrom { get; init; }
    public required int SellToCash { get; init; }
    /// <summary>Glimmer: the longest a CPU hunts, and how long it takes to react when it shows.</summary>
    public required double GlimmerHuntSeconds { get; init; }
    public required double GlimmerReactSeconds { get; init; }
    public required int AssayStopAtHigh { get; init; }
    public required double DistancePenalty { get; init; }
}
