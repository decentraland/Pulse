namespace DCL.Auth;

public sealed record AuthChainValidationResult(
    string UserAddress,
    string CurrentAuthorityAddress,   // the signer for the final step (user or last delegate)
    AuthLink FinalLink,
    IReadOnlyList<AuthLink> Chain
);
