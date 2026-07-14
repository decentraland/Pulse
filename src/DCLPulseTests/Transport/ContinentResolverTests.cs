using Pulse.Transport.Geo;
using System.Net;
using System.Net.Sockets;

namespace DCLPulseTests.Transport;

[TestFixture]
public class ContinentResolverTests
{
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
        var v4 = new StringReader(
            $"{V4("1.0.0.0")},{V4("1.0.0.255")},AU\n" +
            $"{V4("8.8.8.0")},{V4("8.8.8.255")},US\n" +
            $"{V4("41.0.0.0")},{V4("41.255.255.255")},ZA\n");

        StringReader? v6 = withV6
            ? new StringReader($"{V6("2001:db8::")},{V6("2001:db8::ffff")},JP\n")
            : null;

        return ContinentResolver.Load(v4, v6);
    }

    [Test]
    public void Resolves_dotted_ipv4()
    {
        Assert.That(CreateResolver().Resolve("8.8.8.8"), Is.EqualTo(Continent.NorthAmerica));
        Assert.That(CreateResolver().Resolve("1.0.0.5"), Is.EqualTo(Continent.Oceania));
        Assert.That(CreateResolver().Resolve("41.1.2.3"), Is.EqualTo(Continent.Africa));
    }

    [Test]
    public void Resolves_v4_mapped_ipv6_string()
    {
        Assert.That(CreateResolver().Resolve("::ffff:8.8.8.8"), Is.EqualTo(Continent.NorthAmerica));
    }

    [Test]
    public void Resolves_native_ipv6()
    {
        Assert.That(CreateResolver().Resolve("2001:db8::1"), Is.EqualTo(Continent.Asia));
    }

    [Test]
    public void Range_ends_are_inclusive()
    {
        Assert.That(CreateResolver().Resolve("8.8.8.0"), Is.EqualTo(Continent.NorthAmerica));
        Assert.That(CreateResolver().Resolve("8.8.8.255"), Is.EqualTo(Continent.NorthAmerica));
        Assert.That(CreateResolver().Resolve("8.8.9.0"), Is.EqualTo(Continent.Unknown));
    }

    [Test]
    public void Unlisted_private_and_garbage_ips_resolve_unknown()
    {
        ContinentResolver resolver = CreateResolver();
        Assert.That(resolver.Resolve("10.0.0.1"), Is.EqualTo(Continent.Unknown));
        Assert.That(resolver.Resolve("banana"), Is.EqualTo(Continent.Unknown));
        Assert.That(resolver.Resolve(""), Is.EqualTo(Continent.Unknown));
    }

    [Test]
    public void Missing_ipv6_data_resolves_ipv6_to_unknown()
    {
        Assert.That(CreateResolver(withV6: false).Resolve("2001:db8::1"), Is.EqualTo(Continent.Unknown));
    }

    [Test]
    public void Empty_resolver_resolves_everything_unknown()
    {
        Assert.That(ContinentResolver.Empty.Resolve("8.8.8.8"), Is.EqualTo(Continent.Unknown));
    }

    [Test]
    public void Country_to_continent_table_spot_checks()
    {
        Assert.That(ContinentResolver.ContinentFromCountry("US"), Is.EqualTo(Continent.NorthAmerica));
        Assert.That(ContinentResolver.ContinentFromCountry("BR"), Is.EqualTo(Continent.SouthAmerica));
        Assert.That(ContinentResolver.ContinentFromCountry("DE"), Is.EqualTo(Continent.Europe));
        Assert.That(ContinentResolver.ContinentFromCountry("JP"), Is.EqualTo(Continent.Asia));
        Assert.That(ContinentResolver.ContinentFromCountry("AU"), Is.EqualTo(Continent.Oceania));
        Assert.That(ContinentResolver.ContinentFromCountry("ZA"), Is.EqualTo(Continent.Africa));
        Assert.That(ContinentResolver.ContinentFromCountry("AQ"), Is.EqualTo(Continent.Unknown));
        Assert.That(ContinentResolver.ContinentFromCountry(""), Is.EqualTo(Continent.Unknown));
    }

    [Test]
    public void Rows_with_unknown_country_codes_are_kept_as_unknown_not_dropped()
    {
        var v4 = new StringReader($"{V4("5.5.5.0")},{V4("5.5.5.255")},ZZ\n");
        Assert.That(ContinentResolver.Load(v4, null).Resolve("5.5.5.5"), Is.EqualTo(Continent.Unknown));
    }
}
