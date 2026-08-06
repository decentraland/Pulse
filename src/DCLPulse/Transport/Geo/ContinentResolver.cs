using System.Net;
using System.Net.Sockets;

namespace Pulse.Transport.Geo;

/// <summary>
///     IP → continent lookup. The IP-range → country data comes from the CC0
///     geo-whois-asn-country database (https://github.com/sapics/ip-location-db), loaded once
///     at startup from the "-num" CSV variants (rows: ip_range_start,ip_range_end,country_code
///     with numeric inclusive ranges); the country → continent mapping comes from GeoNames
///     countryInfo.txt (CC-BY 4.0). Rows whose numeric range fields fail to parse are skipped
///     and counted rather than throwing — every source is re-fetched unpinned at each image
///     build with no checksum, so a corrupt field must degrade the load, not crash startup. All
///     addresses are normalized to UInt128 — IPv4 into the v4-mapped ::ffff:0:0/96 space — so a
///     single sorted array serves both families with one binary search. No dependencies, no
///     network access: the files are baked into the Docker image at build time; when they're
///     absent every lookup returns <see cref="Continent.UNKNOWN" /> (local dev, unit tests).
/// </summary>
public sealed class ContinentResolver
{
    private const string IPV4_FILE = "geo-whois-asn-country-ipv4-num.csv";
    private const string IPV6_FILE = "geo-whois-asn-country-ipv6-num.csv";
    private const string COUNTRY_INFO_FILE = "countryInfo.txt";

    private static readonly UInt128 V4_MAPPED_PREFIX = (UInt128)0xFFFF << 32;

    private readonly IpRange[] ranges;

    private ContinentResolver(IpRange[] ranges, int skippedRows, int countryCount)
    {
        this.ranges = ranges;
        this.skippedRows = skippedRows;
        this.countryCount = countryCount;
    }

    public static ContinentResolver Empty { get; } = new ([], 0, 0);

    /// <summary>Count of CSV rows dropped during load because a numeric range field failed to parse.</summary>
    internal int skippedRows { get; }

    /// <summary>Count of ISO 3166-1 alpha-2 codes loaded from the country → continent mapping file.</summary>
    internal int countryCount { get; }

    /// <summary>
    ///     Resolve a peer IP string as rendered by ENet's <c>Peer.IP</c> — dotted IPv4,
    ///     v4-mapped IPv6 ("::ffff:a.b.c.d"), or native IPv6. Unparseable input, private
    ///     ranges, and anything outside the loaded ranges resolve to Unknown.
    /// </summary>
    public Continent Resolve(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out IPAddress? address))
            return Continent.UNKNOWN;

        return Lookup(ToUInt128(address));
    }

    public static ContinentResolver Load(TextReader countryInfo, TextReader ipv4Num, TextReader? ipv6Num)
    {
        Dictionary<string, Continent> countryTable = ParseCountryInfo(countryInfo);

        var rows = new List<IpRange>(300_000);
        int skipped = ParseInto(rows, ipv4Num, countryTable, v4Mapped: true);

        if (ipv6Num != null)
            skipped += ParseInto(rows, ipv6Num, countryTable, v4Mapped: false);

        IpRange[] ranges = rows.ToArray();
        Array.Sort(ranges, static (a, b) => a.Start.CompareTo(b.Start));
        return new ContinentResolver(ranges, skipped, countryTable.Count);
    }

    /// <summary>
    ///     Load from the configured directory. Missing country → continent mapping → empty
    ///     resolver with one warning (ranges without a mapping are useless, so all peers
    ///     Unknown). Missing IPv4 file → same. Missing IPv6 file → IPv4-only with an info log
    ///     (native-IPv6 peers Unknown).
    /// </summary>
    public static ContinentResolver LoadFromDirectory(string directory, ILogger logger)
    {
        string countryPath = Path.Combine(directory, COUNTRY_INFO_FILE);
        string v4Path = Path.Combine(directory, IPV4_FILE);
        string v6Path = Path.Combine(directory, IPV6_FILE);

        if (!File.Exists(countryPath))
        {
            logger.LogWarning("Country → continent mapping not found at {Path} — peer RTT will be reported under region=\"unknown\".", countryPath);
            return Empty;
        }

        if (!File.Exists(v4Path))
        {
            logger.LogWarning("Geo database not found at {Path} — peer RTT will be reported under region=\"unknown\".", v4Path);
            return Empty;
        }

        // File.Exists passed above, but the open+read can still fail (permission denied,
        // TOCTOU delete between the check and the open, transient IO error). Degrade like the
        // missing-file branches rather than crashing the DI factory at host startup.
        try
        {
            using var countryInfo = new StreamReader(countryPath);
            using var v4 = new StreamReader(v4Path);
            using StreamReader? v6 = File.Exists(v6Path) ? new StreamReader(v6Path) : null;

            if (v6 == null)
                logger.LogInformation("IPv6 geo database not found at {Path} — native-IPv6 peers will be region=\"unknown\".", v6Path);

            ContinentResolver resolver = Load(countryInfo, v4, v6);

            if (resolver.skippedRows > 0)
                logger.LogInformation(
                    "Geo database loaded: {Ranges} ranges, {Countries} countries from {Directory}, {Skipped} malformed rows skipped.",
                    resolver.ranges.Length, resolver.countryCount, directory, resolver.skippedRows);
            else
                logger.LogInformation(
                    "Geo database loaded: {Ranges} ranges, {Countries} countries from {Directory}.",
                    resolver.ranges.Length, resolver.countryCount, directory);

            return resolver;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to read geo database from {Directory} — peer RTT will be reported under region=\"unknown\".", directory);
            return Empty;
        }
    }

    /// <summary>
    ///     Parse GeoNames <c>countryInfo.txt</c> into an ISO 3166-1 alpha-2 → continent table.
    ///     Tab-separated; blank lines and the leading <c>#</c> comment/header block are skipped.
    ///     Field 0 is the alpha-2 code, field 8 the GeoNames continent code (field 3 is the FIPS
    ///     code — deliberately not read). Rows with fewer than nine fields are ignored.
    /// </summary>
    internal static Dictionary<string, Continent> ParseCountryInfo(TextReader reader)
    {
        var table = new Dictionary<string, Continent>(256, StringComparer.OrdinalIgnoreCase);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            string[] fields = line.Split('\t');

            if (fields.Length < 9)
                continue;

            table[fields[0]] = MapContinentCode(fields[8]);
        }

        return table;
    }

    /// <summary>
    ///     GeoNames continent code → <see cref="Continent" />. Antarctica (AN), blanks, and any
    ///     unrecognized code fold into <see cref="Continent.UNKNOWN" />. This tiny switch is the
    ///     only country mapping left in code — the per-country data lives in the fetched file.
    /// </summary>
    internal static Continent MapContinentCode(string code) =>
        code switch
        {
            "AF" => Continent.AFRICA,
            "AS" => Continent.ASIA,
            "EU" => Continent.EUROPE,
            "NA" => Continent.NORTH_AMERICA,
            "OC" => Continent.OCEANIA,
            "SA" => Continent.SOUTH_AMERICA,
            _ => Continent.UNKNOWN,
        };

    private static int ParseInto(List<IpRange> rows, TextReader reader, Dictionary<string, Continent> countryTable, bool v4Mapped)
    {
        var skipped = 0;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;

            // ip_range_start,ip_range_end,country_code — numeric inclusive ranges.
            int firstComma = line.IndexOf(',');
            int secondComma = line.IndexOf(',', firstComma + 1);

            if (firstComma < 0 || secondComma < 0)
                continue;

            // A corrupt numeric field skips the row rather than crashing the load — the CSVs
            // are re-fetched unpinned at each image build, so a malformed row must degrade.
            if (!UInt128.TryParse(line.AsSpan(0, firstComma), out UInt128 start) ||
                !UInt128.TryParse(line.AsSpan(firstComma + 1, secondComma - firstComma - 1), out UInt128 end))
            {
                skipped++;
                continue;
            }

            Continent continent = countryTable.GetValueOrDefault(line[(secondComma + 1)..].Trim(), Continent.UNKNOWN);

            if (v4Mapped)
            {
                start += V4_MAPPED_PREFIX;
                end += V4_MAPPED_PREFIX;
            }

            rows.Add(new IpRange(start, end, continent));
        }

        return skipped;
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

        return candidate >= 0 && ip <= ranges[candidate].End ? ranges[candidate].Continent : Continent.UNKNOWN;
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

    private readonly record struct IpRange(UInt128 Start, UInt128 End, Continent Continent);
}
