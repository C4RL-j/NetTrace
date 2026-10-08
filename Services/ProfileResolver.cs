using NetTrace.Models;

namespace NetTrace.Services;

public sealed class ProfileResolver(ProfileStore store)
{
    public ConnectionProfile? Resolve(NetworkIdentity? identity) => identity is not { HasValidIdentifier: true }
        ? null
        : store.ResolveProfile(identity.NetworkType, identity.NetworkIdentifier);
}
