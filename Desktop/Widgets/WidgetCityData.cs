using System.Globalization;
using System.Text;

namespace Shamsa.Desktop.Widgets;

/// <summary>
/// Places for the prayer-times / weather pickers: every Iranian county (شهرستان) grouped by province,
/// Iraqi governorates with their main cities, plus a few neighbours.
/// Coordinates: GeoNames (https://www.geonames.org), CC BY 4.0 — see WidgetCityData.g.cs.
/// </summary>
internal static partial class WidgetCityData
{
    // The trailing LRM keeps the closing parenthesis with the Latin run inside right-to-left text.
    public const string Attribution = "داده مختصات: GeoNames (CC BY 4.0)‎";
    public const string OtherGroup = "سایر کشورها";

    /// <summary>Country is "IR", "IQ", "AF" or "TJ".</summary>
    public sealed record Place(string Country, string Province, string Name, double Lat, double Lng)
    {
        /// <summary>The value stored in <c>WidgetSettings.City</c>: «شهرستان، استان».</summary>
        public string Display => Province == OtherGroup ? Name
            : Country == "IQ" && Name == Province ? Name + "، عراق" // «کربلا، عراق» rather than «کربلا، کربلا»
            : Name + "، " + Province;
    }

    /// <summary>A picker group: an Iranian province, an Iraqi governorate or the «other countries» bucket.</summary>
    public sealed record Group(string Country, string Name, IReadOnlyList<Place> Places)
    {
        public string Label => Country switch { "IQ" => "عراق — " + Name, "IR" => Name, _ => Name };
    }

    private static readonly (string Name, string Country, double Lat, double Lng)[] Others =
    {
        ("کابل", "AF", 34.5553, 69.2075), ("هرات", "AF", 34.3529, 62.2040), ("مزار شریف", "AF", 36.7090, 67.1109),
        ("قندهار", "AF", 31.6133, 65.7101), ("دوشنبه", "TJ", 38.5598, 68.7870), ("خجند", "TJ", 40.2826, 69.6222),
        ("غزه", "PS", 31.5017, 34.4668), ("بیروت", "LB", 33.8938, 35.5018), ("صنعا", "YE", 15.3694, 44.1910),
    };

    private static List<Group>? _groups;
    private static List<Place>? _all;

    public static IReadOnlyList<Group> Groups { get { Load(); return _groups!; } }
    public static IReadOnlyList<Place> All { get { Load(); return _all!; } }

    private static void Load()
    {
        if (_groups != null) return;
        var groups = new List<Group>();
        var byKey = new Dictionary<(string, string), List<Place>>();
        string country = "IR";
        foreach (var raw in Raw.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#') { country = line[1..]; continue; }
            var f = line.Split('|');
            if (f.Length != 4 ||
                !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng)) continue;
            if (!byKey.TryGetValue((country, f[0]), out var list))
            {
                byKey[(country, f[0])] = list = new List<Place>();
                groups.Add(new Group(country, f[0], list));
            }
            list.Add(new Place(country, f[0], f[1], lat, lng));
        }
        var fa = CultureInfo.GetCultureInfo("fa-IR").CompareInfo;
        int Cmp(string a, string b) => fa.Compare(a, b, CompareOptions.IgnoreNonSpace);
        foreach (var g in groups) ((List<Place>)g.Places).Sort((a, b) => Cmp(a.Name, b.Name));
        groups = groups.Where(g => g.Country == "IR").OrderBy(g => g.Name, Comparer<string>.Create(Cmp))
            .Concat(groups.Where(g => g.Country == "IQ").OrderBy(g => g.Name, Comparer<string>.Create(Cmp))).ToList();
        groups.Add(new Group("", OtherGroup, Others.Select(o => new Place(o.Country, OtherGroup, o.Name, o.Lat, o.Lng)).ToList()));
        _all = groups.SelectMany(g => g.Places).ToList();
        _groups = groups;
    }

    /// <summary>
    /// Finds the place a stored City string refers to: «شهرستان، استان» exactly, or a bare name
    /// (older settings such as «تهران»), preferring Iran.
    /// </summary>
    public static Place? Find(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        string name = city, province = "";
        int comma = city.IndexOf('،');
        if (comma < 0) comma = city.IndexOf(',');
        if (comma >= 0) { name = city[..comma]; province = city[(comma + 1)..]; }
        string n = Normalize(name), p = Normalize(province);
        if (p.Length > 0)
        {
            var exact = All.FirstOrDefault(x => Normalize(x.Name) == n && (Normalize(x.Province) == p || (p == "عراق" && x.Country == "IQ")));
            if (exact != null) return exact;
        }
        return All.FirstOrDefault(x => Normalize(x.Name) == n);
    }

    /// <summary>Nearest listed place within ~55 km, used when only coordinates are known.</summary>
    public static Place? Nearest(double lat, double lng, double maxDegrees = 0.5)
    {
        Place? best = null; double bestD = maxDegrees * maxDegrees;
        foreach (var x in All)
        {
            double d = (x.Lat - lat) * (x.Lat - lat) + (x.Lng - lng) * (x.Lng - lng);
            if (d <= bestD) { bestD = d; best = x; }
        }
        return best;
    }

    /// <summary>Name part of a stored City («شهرستان، استان» → «شهرستان»).</summary>
    public static string ShortName(string? city)
    {
        if (string.IsNullOrEmpty(city)) return "";
        int comma = city.IndexOf('،');
        return comma > 0 ? city[..comma].Trim() : city;
    }

    /// <summary>Search key: Arabic/Persian letter variants unified, ZWNJ/spaces/diacritics dropped.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            char c = ch switch
            {
                'ي' or 'ى' or 'ئ' => 'ی',
                'ك' => 'ک',
                'ة' => 'ه',
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ؤ' => 'و',
                _ => ch,
            };
            if (c == '‌' || c == '‏' || c == ' ' || c == '-' || c == 'ـ' || (c >= 'ً' && c <= 'ٟ') || c == 'ٰ') continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
