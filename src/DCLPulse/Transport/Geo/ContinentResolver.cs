using System.Net;
using System.Net.Sockets;

namespace Pulse.Transport.Geo;

/// <summary>
///     IP → continent lookup backed by the CC0 geo-whois-asn-country database
///     (https://github.com/sapics/ip-location-db), loaded once at startup from the
///     "-num" CSV variants (rows: ip_range_start,ip_range_end,country_code with numeric
///     inclusive ranges). All addresses are normalized to UInt128 — IPv4 into the
///     v4-mapped ::ffff:0:0/96 space — so a single sorted array serves both families
///     with one binary search. No dependencies, no network access: the CSVs are baked
///     into the Docker image at build time; when they're absent every lookup returns
///     <see cref="Continent.Unknown" /> (local dev, unit tests).
/// </summary>
public sealed class ContinentResolver
{
    private const string IPV4_FILE = "geo-whois-asn-country-ipv4-num.csv";
    private const string IPV6_FILE = "geo-whois-asn-country-ipv6-num.csv";

    private static readonly UInt128 V4_MAPPED_PREFIX = (UInt128)0xFFFF << 32;

    // ISO 3166-1 alpha-2 → continent. Codes not listed (AQ, BV, GS, HM, IO, TF, ZZ, …)
    // resolve to Unknown. Kept as flat strings so the table is reviewable at a glance.
    private const string AFRICA = "AO,BF,BI,BJ,BW,CD,CF,CG,CI,CM,CV,DJ,DZ,EG,EH,ER,ET,GA,GH,GM,GN,GQ,GW,KE,KM,LR,LS,LY,MA,MG,ML,MR,MU,MW,MZ,NA,NE,NG,RE,RW,SC,SD,SH,SL,SN,SO,SS,ST,SZ,TD,TG,TN,TZ,UG,YT,ZA,ZM,ZW";
    private const string ASIA = "AE,AF,AM,AZ,BD,BH,BN,BT,CN,CY,GE,HK,ID,IL,IN,IQ,IR,JO,JP,KG,KH,KP,KR,KW,KZ,LA,LB,LK,MM,MN,MO,MV,MY,NP,OM,PH,PK,PS,QA,SA,SG,SY,TH,TJ,TL,TM,TR,TW,UZ,VN,YE";
    private const string EUROPE = "AD,AL,AT,AX,BA,BE,BG,BY,CH,CZ,DE,DK,EE,ES,FI,FO,FR,GB,GG,GI,GR,HR,HU,IE,IM,IS,IT,JE,LI,LT,LU,LV,MC,MD,ME,MK,MT,NL,NO,PL,PT,RO,RS,RU,SE,SI,SJ,SK,SM,UA,VA,XK";
    private const string NORTH_AMERICA = "AG,AI,AW,BB,BL,BM,BQ,BS,BZ,CA,CR,CU,CW,DM,DO,GD,GL,GP,GT,HN,HT,JM,KN,KY,LC,MF,MQ,MS,MX,NI,PA,PM,PR,SV,SX,TC,TT,US,VC,VG,VI";
    private const string OCEANIA = "AS,AU,CC,CK,CX,FJ,FM,GU,KI,MH,MP,NC,NF,NR,NU,NZ,PF,PG,PN,PW,SB,TK,TO,TV,UM,VU,WF,WS";
    private const string SOUTH_AMERICA = "AR,BO,BR,CL,CO,EC,FK,GF,GY,PE,PY,SR,UY,VE";

    private static readonly Dictionary<string, Continent> CONTINENT_BY_COUNTRY = BuildCountryTable();

    private readonly IpRange[] ranges;

    private ContinentResolver(IpRange[] ranges)
    {
        this.ranges = ranges;
    }

    public static ContinentResolver Empty { get; } = new ([]);

    /// <summary>
    ///     Resolve a peer IP string as rendered by ENet's <c>Peer.IP</c> — dotted IPv4,
    ///     v4-mapped IPv6 ("::ffff:a.b.c.d"), or native IPv6. Unparseable input, private
    ///     ranges, and anything outside the loaded ranges resolve to Unknown.
    /// </summary>
    public Continent Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out IPAddress? address))
            return Continent.Unknown;

        return Lookup(ToUInt128(address));
    }

    public static ContinentResolver Load(TextReader ipv4Num, TextReader? ipv6Num)
    {
        var rows = new List<IpRange>(300_000);
        ParseInto(rows, ipv4Num, v4Mapped: true);

        if (ipv6Num != null)
            ParseInto(rows, ipv6Num, v4Mapped: false);

        IpRange[] ranges = rows.ToArray();
        Array.Sort(ranges, static (a, b) => a.Start.CompareTo(b.Start));
        return new ContinentResolver(ranges);
    }

    /// <summary>
    ///     Load from the configured directory. Missing IPv4 file → empty resolver with one
    ///     warning (all peers Unknown). Missing IPv6 file → IPv4-only with an info log
    ///     (native-IPv6 peers Unknown).
    /// </summary>
    public static ContinentResolver LoadFromDirectory(string directory, ILogger logger)
    {
        string v4Path = Path.Combine(directory, IPV4_FILE);
        string v6Path = Path.Combine(directory, IPV6_FILE);

        if (!File.Exists(v4Path))
        {
            logger.LogWarning("Geo database not found at {Path} — peer RTT will be reported under region=\"unknown\".", v4Path);
            return Empty;
        }

        using var v4 = new StreamReader(v4Path);
        using StreamReader? v6 = File.Exists(v6Path) ? new StreamReader(v6Path) : null;

        if (v6 == null)
            logger.LogInformation("IPv6 geo database not found at {Path} — native-IPv6 peers will be region=\"unknown\".", v6Path);

        ContinentResolver resolver = Load(v4, v6);
        logger.LogInformation("Geo database loaded: {Count} ranges from {Directory}.", resolver.ranges.Length, directory);
        return resolver;
    }

    internal static Continent ContinentFromCountry(string countryCode) =>
        CONTINENT_BY_COUNTRY.GetValueOrDefault(countryCode, Continent.Unknown);

    private static void ParseInto(List<IpRange> rows, TextReader reader, bool v4Mapped)
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;

            // ip_range_start,ip_range_end,country_code — numeric inclusive ranges.
            int firstComma = line.IndexOf(',');
            int secondComma = line.IndexOf(',', firstComma + 1);

            if (firstComma < 0 || secondComma < 0)
                continue;

            UInt128 start = UInt128.Parse(line.AsSpan(0, firstComma));
            UInt128 end = UInt128.Parse(line.AsSpan(firstComma + 1, secondComma - firstComma - 1));
            Continent continent = ContinentFromCountry(line[(secondComma + 1)..].Trim());

            if (v4Mapped)
            {
                start += V4_MAPPED_PREFIX;
                end += V4_MAPPED_PREFIX;
            }

            rows.Add(new IpRange(start, end, continent));
        }
    }

    private Continent Lookup(UInt128 ip)
    {
        int lo = 0, hi = ranges.Length - 1;
        var candidate = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);

            if (ranges[mid].Start <= ip)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return candidate >= 0 && ip <= ranges[candidate].End ? ranges[candidate].Continent : Continent.Unknown;
    }

    private static UInt128 ToUInt128(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        UInt128 value = 0;

        foreach (byte b in bytes)
            value = (value << 8) | b;

        return address.AddressFamily == AddressFamily.InterNetwork ? value + V4_MAPPED_PREFIX : value;
    }

    private static Dictionary<string, Continent> BuildCountryTable()
    {
        var table = new Dictionary<string, Continent>(256, StringComparer.OrdinalIgnoreCase);
        Add(table, AFRICA, Continent.Africa);
        Add(table, ASIA, Continent.Asia);
        Add(table, EUROPE, Continent.Europe);
        Add(table, NORTH_AMERICA, Continent.NorthAmerica);
        Add(table, OCEANIA, Continent.Oceania);
        Add(table, SOUTH_AMERICA, Continent.SouthAmerica);
        return table;

        static void Add(Dictionary<string, Continent> table, string codes, Continent continent)
        {
            foreach (string code in codes.Split(','))
                table[code] = continent;
        }
    }

    private readonly record struct IpRange(UInt128 Start, UInt128 End, Continent Continent);
}
