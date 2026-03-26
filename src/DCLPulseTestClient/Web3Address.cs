namespace PulseTestClient;

public readonly struct Web3Address(string address)
{
    public readonly string Address = address;
    private readonly string lowerCaseAddress = address.ToLower();

    public override string ToString() =>
        lowerCaseAddress;

    public override int GetHashCode() =>
        lowerCaseAddress.GetHashCode();

    public override bool Equals(object? obj)
    {
        if (obj == null) return false;

        return obj switch
        {
            string s => Equals(s),
            Web3Address a => Equals(a),
            _ => false,
        };
    }

    public bool Equals(string? s)
    {
        if (s == null) return false;
        return lowerCaseAddress.Equals(s, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(Web3Address a) =>
        Equals(a.lowerCaseAddress);

    public static bool operator ==(Web3Address x, string? y) =>
        x.Equals(y);

    public static bool operator !=(Web3Address x, string? y) =>
        !x.Equals(y);

    public static bool operator ==(string? y, Web3Address x) =>
        x.Equals(y);

    public static bool operator !=(string? y, Web3Address x) =>
        !x.Equals(y);

    public static implicit operator string(Web3Address source) =>
        source.ToString();
}