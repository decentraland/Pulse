using Pulse.Transport.Geo;
using System.Net;

namespace DCLPulseTests.Transport;

[TestFixture]
public class ContinentResolverTests
{
    // Minimal GeoNames countryInfo.txt sample: a '#' header/comment line plus tab-separated
    // rows padded to nine fields (field 0 = ISO alpha-2, field 3 = FIPS, field 8 = continent
    // code). AU's FIPS is "AS" while its continent is "OC" — a deliberate trap proving the
    // parser reads field 8, not field 3. AQ carries continent code "AN" (Antarctica) so it
    // folds to UNKNOWN.
    private const string COUNTRY_INFO_SAMPLE =
        "#ISO\tISO3\tISO-Numeric\tfips\tCountry\tCapital\tArea\tPopulation\tContinent\n" +
        "US\tUSA\t840\tUS\tUnited States\tWashington\t9629091\t327167434\tNA\n" +
        "BR\tBRA\t076\tBR\tBrazil\tBrasilia\t8511965\t209469333\tSA\n" +
        "DE\tDEU\t276\tGM\tGermany\tBerlin\t357021\t82927922\tEU\n" +
        "JP\tJPN\t392\tJA\tJapan\tTokyo\t377835\t126529100\tAS\n" +
        "AU\tAUS\t036\tAS\tAustralia\tCanberra\t7686850\t24992369\tOC\n" +
        "ZA\tZAF\t710\tSF\tSouth Africa\tPretoria\t1219912\t57779622\tAF\n" +
        "AQ\tATA\t010\tAY\tAntarctica\t\t14000000\t0\tAN\n";

    /// <summary>Decimal value of an IPv4 address, as the ipv4-num CSV encodes it.</summary>
    private static UInt128 V4(string ip)
    {
        byte[] b = IPAddress.Parse(ip).GetAddressBytes();
        return ((UInt128)b[0] << 24) | ((UInt128)b[1] << 16) | ((UInt128)b[2] << 8) | b[3];
    }

    /// <summary>Decimal value of an IPv6 address, as the ipv6-num CSV encodes it.</summary>
    private static UInt128 V6(string ip)
    {
        byte[] b = IPAddress.Parse(ip).GetAddressBytes();
        UInt128 value = 0;

        foreach (byte t in b)
            value = (value << 8) | t;

        return value;
    }

    private static ContinentResolver CreateResolver(bool withV6 = true)
    {
        var countryInfo = new StringReader(COUNTRY_INFO_SAMPLE);

        var v4 = new StringReader(
            $"{V4("1.0.0.0")},{V4("1.0.0.255")},AU\n" +
            $"{V4("8.8.8.0")},{V4("8.8.8.255")},US\n" +
            $"{V4("41.0.0.0")},{V4("41.255.255.255")},ZA\n");

        StringReader? v6 = withV6
            ? new StringReader($"{V6("2001:db8::")},{V6("2001:db8::ffff")},JP\n")
            : null;

        return ContinentResolver.Load(countryInfo, v4, v6);
    }

    [Test]
    public void Resolves_dotted_ipv4()
    {
        Assert.That(CreateResolver().Resolve("8.8.8.8"), Is.EqualTo(Continent.NORTH_AMERICA));
        Assert.That(CreateResolver().Resolve("1.0.0.5"), Is.EqualTo(Continent.OCEANIA));
        Assert.That(CreateResolver().Resolve("41.1.2.3"), Is.EqualTo(Continent.AFRICA));
    }

    [Test]
    public void Resolves_v4_mapped_ipv6_string()
    {
        Assert.That(CreateResolver().Resolve("::ffff:8.8.8.8"), Is.EqualTo(Continent.NORTH_AMERICA));
    }

    [Test]
    public void Resolves_native_ipv6()
    {
        Assert.That(CreateResolver().Resolve("2001:db8::1"), Is.EqualTo(Continent.ASIA));
    }

    [Test]
    public void Range_ends_are_inclusive()
    {
        Assert.That(CreateResolver().Resolve("8.8.8.0"), Is.EqualTo(Continent.NORTH_AMERICA));
        Assert.That(CreateResolver().Resolve("8.8.8.255"), Is.EqualTo(Continent.NORTH_AMERICA));
        Assert.That(CreateResolver().Resolve("8.8.9.0"), Is.EqualTo(Continent.UNKNOWN));
    }

    [Test]
    public void Unlisted_private_and_garbage_ips_resolve_unknown()
    {
        ContinentResolver resolver = CreateResolver();
        Assert.That(resolver.Resolve("10.0.0.1"), Is.EqualTo(Continent.UNKNOWN));
        Assert.That(resolver.Resolve("banana"), Is.EqualTo(Continent.UNKNOWN));
        Assert.That(resolver.Resolve(""), Is.EqualTo(Continent.UNKNOWN));
    }

    // Local and private addresses parse as valid IPs, then miss every loaded registry range and
    // fall through to UNKNOWN. This is the "unknown = private/unresolvable" contract the docs
    // promise — no special-casing, just the ordinary lookup-miss path.
    [Test]
    public void Local_and_private_addresses_resolve_unknown()
    {
        ContinentResolver resolver = CreateResolver();
        Assert.That(resolver.Resolve("127.0.0.1"), Is.EqualTo(Continent.UNKNOWN), "IPv4 loopback");
        Assert.That(resolver.Resolve("::1"), Is.EqualTo(Continent.UNKNOWN), "IPv6 loopback");
        Assert.That(resolver.Resolve("::ffff:127.0.0.1"), Is.EqualTo(Continent.UNKNOWN), "v4-mapped loopback");
        Assert.That(resolver.Resolve("192.168.1.10"), Is.EqualTo(Continent.UNKNOWN), "RFC1918 /16");
        Assert.That(resolver.Resolve("172.16.0.1"), Is.EqualTo(Continent.UNKNOWN), "RFC1918 /12");
        Assert.That(resolver.Resolve("fe80::1"), Is.EqualTo(Continent.UNKNOWN), "IPv6 link-local");
    }

    [Test]
    public void Missing_ipv6_data_resolves_ipv6_to_unknown()
    {
        Assert.That(CreateResolver(withV6: false).Resolve("2001:db8::1"), Is.EqualTo(Continent.UNKNOWN));
    }

    [Test]
    public void Empty_resolver_resolves_everything_unknown()
    {
        Assert.That(ContinentResolver.Empty.Resolve("8.8.8.8"), Is.EqualTo(Continent.UNKNOWN));
    }

    [Test]
    public void Country_to_continent_table_spot_checks()
    {
        Dictionary<string, Continent> table = ContinentResolver.ParseCountryInfo(new StringReader(COUNTRY_INFO_SAMPLE));

        Assert.That(table.GetValueOrDefault("US", Continent.UNKNOWN), Is.EqualTo(Continent.NORTH_AMERICA));
        Assert.That(table.GetValueOrDefault("BR", Continent.UNKNOWN), Is.EqualTo(Continent.SOUTH_AMERICA));
        Assert.That(table.GetValueOrDefault("DE", Continent.UNKNOWN), Is.EqualTo(Continent.EUROPE));
        Assert.That(table.GetValueOrDefault("JP", Continent.UNKNOWN), Is.EqualTo(Continent.ASIA));
        // AU's FIPS code (field 3) is "AS"; asserting OCEANIA proves the parser read the
        // continent column (field 8 = "OC"), not the FIPS column.
        Assert.That(table.GetValueOrDefault("AU", Continent.UNKNOWN), Is.EqualTo(Continent.OCEANIA));
        Assert.That(table.GetValueOrDefault("ZA", Continent.UNKNOWN), Is.EqualTo(Continent.AFRICA));
        // Antarctica carries continent code "AN" → folds to UNKNOWN rather than being dropped.
        Assert.That(table.GetValueOrDefault("AQ", Continent.UNKNOWN), Is.EqualTo(Continent.UNKNOWN));
        // A code absent from the file resolves via the caller-supplied default.
        Assert.That(table.GetValueOrDefault("ZZ", Continent.UNKNOWN), Is.EqualTo(Continent.UNKNOWN));
    }

    [Test]
    public void MapContinentCode_folds_antarctica_and_garbage_to_unknown()
    {
        Assert.That(ContinentResolver.MapContinentCode("AN"), Is.EqualTo(Continent.UNKNOWN));
        Assert.That(ContinentResolver.MapContinentCode(""), Is.EqualTo(Continent.UNKNOWN));
        Assert.That(ContinentResolver.MapContinentCode("??"), Is.EqualTo(Continent.UNKNOWN));
        Assert.That(ContinentResolver.MapContinentCode("EU"), Is.EqualTo(Continent.EUROPE));
    }

    [Test]
    public void Rows_with_unknown_country_codes_are_kept_as_unknown_not_dropped()
    {
        var v4 = new StringReader($"{V4("5.5.5.0")},{V4("5.5.5.255")},ZZ\n");
        Assert.That(ContinentResolver.Load(new StringReader(COUNTRY_INFO_SAMPLE), v4, null).Resolve("5.5.5.5"), Is.EqualTo(Continent.UNKNOWN));
    }

    [Test]
    public void Malformed_numeric_rows_are_skipped_and_counted_without_throwing()
    {
        // Two corrupt rows (bad start, then bad end) interleaved with two good ones — mimics a
        // truncated/garbled CSV re-fetched unpinned at image build. Load must not throw.
        var v4 = new StringReader(
            $"{V4("8.8.8.0")},{V4("8.8.8.255")},US\n" +
            "not_a_number,12345,DE\n" +
            $"{V4("41.0.0.0")},garbage,ZA\n" +
            $"{V4("1.0.0.0")},{V4("1.0.0.255")},AU\n");

        ContinentResolver resolver = ContinentResolver.Load(new StringReader(COUNTRY_INFO_SAMPLE), v4, null);

        // Valid rows still resolve; the two malformed rows are skipped and counted.
        Assert.That(resolver.Resolve("8.8.8.8"), Is.EqualTo(Continent.NORTH_AMERICA));
        Assert.That(resolver.Resolve("1.0.0.5"), Is.EqualTo(Continent.OCEANIA));
        Assert.That(resolver.skippedRows, Is.EqualTo(2));
    }
}
