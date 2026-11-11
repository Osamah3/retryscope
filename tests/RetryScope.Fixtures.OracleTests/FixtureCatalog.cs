using Microsoft.Extensions.Hosting;

namespace RetryScope.Fixtures.OracleTests;

/// <summary>Every fixture's registration entry point, keyed by its folder name.</summary>
public static class FixtureCatalog
{
    private static readonly Dictionary<string, Action<IHostApplicationBuilder>> Registrations = new(StringComparer.Ordinal)
    {
        ["P01-aspire-defaults"] = P01.Registration.Register,
        ["P02-per-client-standard"] = P02.Registration.Register,
        ["P03-lambda-options"] = P03.Registration.Register,
        ["P04-config-bound"] = P04.Registration.Register,
        ["P05-custom-pipeline"] = P05.Registration.Register,
        ["P06-stacked-defaults-and-client"] = P06.Registration.Register,
        ["P07-remove-then-custom"] = P07.Registration.Register,
        ["P08-unsafe-methods-disabled"] = P08.Registration.Register,
        ["P12-polly-v7"] = P12.Registration.Register,
    };

    public static TheoryData<string> Ids => new(Registrations.Keys.Order(StringComparer.Ordinal));

    public static Action<IHostApplicationBuilder> Get(string fixtureId) => Registrations[fixtureId];
}
